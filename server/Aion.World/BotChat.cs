using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aion.Application.Chat;

namespace Aion.World;

/// <summary>
/// Aion:BotChat (OURS): world bots talk in the shared chat like players, written by a language model (docs/WORLDBOTS.md "Bot chat").
/// Any server that speaks the OpenAI chat completions API works: Ollama on the same machine is free and needs no key (the default),
/// and hosted free tiers (Groq, OpenRouter, Google Gemini's OpenAI endpoint) need an API key, which goes in the environment
/// (Aion__BotChat__ApiKey), never in a file in the repository. Without a model the bots use short canned lines.
/// </summary>
public sealed record BotChatOptions
{
    public bool Enabled { get; init; }
    /// <summary>OpenAI-compatible base URL (".../v1"); empty: canned lines only.</summary>
    public string Endpoint { get; init; } = "http://127.0.0.1:11434/v1";
    public string Model { get; init; } = "llama3.2:3b";
    /// <summary>Bearer key for hosted providers. Set it in the environment or user secrets only.</summary>
    public string ApiKey { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxTokens { get; init; } = 60;
    public double Temperature { get; init; } = 0.9;
    /// <summary>The room bots talk in: WORLD (the server's global channel) or REGION (the common region channel).</summary>
    public string Room { get; init; } = "WORLD";
    /// <summary>Bots' messages reach players of their own race only (faction chat); off: everyone.</summary>
    public bool FactionOnly { get; init; } = true;
    /// <summary>Seconds between unprompted lines from some bot (a random time between the two).</summary>
    public int AmbientMinSeconds { get; init; } = 90;
    public int AmbientMaxSeconds { get; init; } = 300;
    /// <summary>Chance a bot answers a player's message that names no bot (a question counts 1.5 times).</summary>
    public double ReplyChance { get; init; } = 0.35;
    /// <summary>Chance another bot answers a bot's line (one answer at most, so bots never talk among themselves forever).</summary>
    public double BotReplyChance { get; init; } = 0.2;
    /// <summary>Seconds a bot "types" before its answer appears (a random time between the two).</summary>
    public int TypingMinSeconds { get; init; } = 2;
    public int TypingMaxSeconds { get; init; } = 7;
    /// <summary>No bot says anything more often than this overall, except answers to whispers and to a bot's name (seconds).</summary>
    public int MinGapSeconds { get; init; } = 4;
    /// <summary>Extra rules for every bot's prompt (server flavour: "We are a friends-only server called ...").</summary>
    public string ExtraPrompt { get; init; } = "";

    public BotChatOptions Validated() =>
        Room is not ("WORLD" or "REGION") ? throw new InvalidOperationException("Aion:BotChat:Room must be WORLD or REGION.")
        : AmbientMinSeconds < 10 || AmbientMaxSeconds < AmbientMinSeconds ? throw new InvalidOperationException("Aion:BotChat:AmbientMinSeconds must be 10 or more and at most AmbientMaxSeconds.")
        : ReplyChance is < 0 or > 1 || BotReplyChance is < 0 or > 1 ? throw new InvalidOperationException("Aion:BotChat: chances are from 0 to 1.")
        : TypingMinSeconds < 0 || TypingMaxSeconds < TypingMinSeconds || TimeoutSeconds < 1 || MaxTokens < 8 ? throw new InvalidOperationException("Aion:BotChat: timings and MaxTokens are out of range.")
        : this;
}

/// <summary>A chat model: messages (system, user, assistant) in, one reply out (null on any failure).</summary>
public interface IChatModel
{
    Task<string?> CompleteAsync(IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct);
}

/// <summary>POST {endpoint}/chat/completions, the OpenAI format that Ollama, LM Studio, llama.cpp, Groq, OpenRouter and Gemini speak.</summary>
public sealed class OpenAiChatModel(HttpClient http, BotChatOptions options) : IChatModel
{
    public async Task<string?> CompleteAsync(IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxTokens,
            ["temperature"] = options.Temperature,
            ["stream"] = false,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint.TrimEnd('/') + "/chat/completions") { Content = JsonContent.Create(body) };
        if (options.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ct.IsCancellationRequested ? throw new OperationCanceledException(ct) : null;
        }
    }
}

/// <summary>What chat needs to know about a bot that can talk.</summary>
public sealed record BotSpeaker(string Name, int Race, string ClassName, int Level, string Personality, string Activity, string? Place);

/// <summary>A line a bot says: in the bots' room (WhisperTo null) or as a whisper to a player.</summary>
public sealed record BotLine(string Name, int Race, string Text, string? WhisperTo = null, ChatRoomKey? WhisperRoom = null, string WhisperSubType = "NORMAL");

/// <summary>
/// Decides when bots talk and what they say (OURS, after AzerothCore's mod-ollama-chat): now and then a random bot says something
/// unprompted about what it is doing; a player who names a bot (or whispers to one) gets an answer from it; other player messages
/// get an answer from a bot of the player's race by chance; a bot's line may get one answer from another bot. Answers wait a few
/// seconds (typing), one model call runs at a time, and nothing waits on chat: listeners only queue.
/// </summary>
public sealed class BotChatter(BotChatOptions options, Func<IReadOnlyList<BotSpeaker>> speakers, IChatModel? model, Random? random = null,
    Func<long>? clock = null)
{
    sealed record Pending(long DueAt, BotSpeaker Bot, string Kind, ChatHeard? Heard, int Depth);

    readonly Random rng = random ?? new Random();
    readonly Func<long> now = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    readonly Lock gate = new();
    readonly List<Pending> pending = [];
    /// <summary>Recent lines per race channel (0 when not faction chat): "name: text".</summary>
    readonly Dictionary<int, List<string>> history = [];
    long nextAmbientAt, lastSaidAt;

    public const int HistoryLines = 12;

    public BotChatOptions Options => options;

    /// <summary>Lines the bots said, for diagnostics (the last 50).</summary>
    public IReadOnlyList<BotLine> Said { get { lock (gate) return [.. said]; } }
    readonly List<BotLine> said = [];

    int Channel(int race) => options.FactionOnly ? race : 0;

    void Remember(int race, string line)
    {
        var list = history.TryGetValue(Channel(race), out var l) ? l : history[Channel(race)] = [];
        list.Add(line);
        if (list.Count > HistoryLines) list.RemoveAt(0);
    }

    /// <summary>A player's message (ChatRelay.Heard): remembered as context and maybe answered. Never blocks.</summary>
    public void Hear(ChatHeard heard)
    {
        if (!options.Enabled) return;
        var bots = speakers();
        lock (gate)
        {
            if (heard.WhisperTo is { } to)
            {
                if (bots.FirstOrDefault(b => b.Name.Equals(to, StringComparison.OrdinalIgnoreCase)) is { } whispered)
                    Queue(whispered, "whisper", heard, 0, direct: true);
                return;
            }
            if (heard.RoomType != options.Room) return;
            Remember(heard.Race, $"{heard.Speaker}: {heard.Content}");
            var mine = bots.Where(b => !options.FactionOnly || heard.Race == 0 || b.Race == heard.Race).ToList();
            if (mine.Count == 0) return;
            var named = mine.Where(b => Mentions(heard.Content, b.Name)).Take(2).ToList();
            if (named.Count > 0)
            {
                foreach (var bot in named) Queue(bot, "reply", heard, 0, direct: true);
                return;
            }
            var chance = options.ReplyChance * (heard.Content.Contains('?') ? 1.5 : 1);
            if (rng.NextDouble() < chance) Queue(mine[rng.Next(mine.Count)], "reply", heard, 0, direct: false);
        }
    }

    static bool Mentions(string text, string name) => Regex.IsMatch(text, $@"(?<![\p{{L}}]){Regex.Escape(name)}(?![\p{{L}}])", RegexOptions.IgnoreCase);

    void Queue(BotSpeaker bot, string kind, ChatHeard? heard, int depth, bool direct)
    {
        if (pending.Count >= 20 || pending.Any(p => p.Bot.Name == bot.Name && p.Kind == kind && p.Heard == heard)) return;
        var typing = rng.Next(options.TypingMinSeconds * 1000, options.TypingMaxSeconds * 1000 + 1);
        pending.Add(new Pending(now() + typing, bot, direct ? kind + "!" : kind, heard, depth));
    }

    /// <summary>
    /// The lines due now: queued answers whose typing time is over, or an unprompted line when it is time. Call it about once a
    /// second; it awaits the model, one call at a time.
    /// </summary>
    public async Task<IReadOnlyList<BotLine>> TickAsync(CancellationToken ct)
    {
        if (!options.Enabled) return [];
        var at = now();
        Pending? next;
        lock (gate)
        {
            // Stale answers (the model was slow, or chat was busy) are dropped rather than said late.
            pending.RemoveAll(p => at - p.DueAt > 60_000);
            next = pending.Where(p => p.DueAt <= at).OrderBy(p => p.DueAt).FirstOrDefault();
            var direct = next?.Kind.EndsWith('!') == true;
            if (next is not null && !direct && at - lastSaidAt < options.MinGapSeconds * 1000L) next = null;
            if (next is not null) pending.Remove(next);
            else if (nextAmbientAt == 0) nextAmbientAt = at + rng.Next(options.AmbientMinSeconds, options.AmbientMaxSeconds + 1) * 1000L / 2;
            else if (at >= nextAmbientAt && at - lastSaidAt >= options.MinGapSeconds * 1000L)
            {
                nextAmbientAt = at + rng.Next(options.AmbientMinSeconds, options.AmbientMaxSeconds + 1) * 1000L;
                var bots = speakers();
                if (bots.Count > 0) next = new Pending(at, bots[rng.Next(bots.Count)], "ambient", null, 0);
            }
        }
        if (next is null) return [];
        // The bot may have left the world meanwhile: use what it is doing now.
        var bot = speakers().FirstOrDefault(b => b.Name == next.Bot.Name) ?? next.Bot;
        var text = await WriteAsync(bot, next, ct);
        if (string.IsNullOrWhiteSpace(text)) return [];
        var line = next.Heard is { WhisperTo: not null } whisper
            ? new BotLine(bot.Name, bot.Race, text, whisper.Speaker, whisper.Room, whisper.SubType)
            : new BotLine(bot.Name, bot.Race, text);
        lock (gate)
        {
            lastSaidAt = now();
            said.Add(line);
            if (said.Count > 50) said.RemoveAt(0);
            if (line.WhisperTo is null)
            {
                Remember(bot.Race, $"{bot.Name}: {text}");
                // Another bot of the same race may answer once.
                if (next.Depth == 0 && rng.NextDouble() < options.BotReplyChance)
                {
                    var others = speakers().Where(b => b.Name != bot.Name && (!options.FactionOnly || b.Race == bot.Race)).ToList();
                    if (others.Count > 0) Queue(others[rng.Next(others.Count)], "reply", new ChatHeard(options.Room, bot.Name, text, bot.Race), 1, direct: false);
                }
            }
        }
        return [line];
    }

    /// <summary>The model's line for this bot (cleaned up), or a canned one when there is no model or it failed.</summary>
    async Task<string> WriteAsync(BotSpeaker bot, Pending what, CancellationToken ct)
    {
        if (model is not null && options.Endpoint.Length > 0)
        {
            var reply = await model.CompleteAsync(Prompt(bot, what), ct);
            if (Clean(reply, bot.Name) is { Length: > 0 } clean) return clean;
        }
        return Canned(bot, what);
    }

    /// <summary>The system prompt (who the bot is, the rules) and the request (recent chat, what to answer).</summary>
    public IReadOnlyList<(string Role, string Content)> Prompt(BotSpeaker bot, ChatHeard? heard, string kind)
        => Prompt(bot, new Pending(0, bot, kind, heard, 0));

    IReadOnlyList<(string Role, string Content)> Prompt(BotSpeaker bot, Pending what)
    {
        var race = bot.Race == 2 ? "Asmodian" : "Elyos";
        var system = new StringBuilder()
            .Append($"You are {bot.Name}, a level {bot.Level} {race} {bot.ClassName} playing the MMORPG Aion 2 on a small private server. ")
            .Append($"Your personality: {bot.Personality}. Right now you are {bot.Activity}{(bot.Place is { } p ? " in " + p : "")}. ")
            .Append("You are typing in the game's chat channel. Write ONE short chat message, at most 15 words, the way real players type: casual, ")
            .Append("lowercase is fine, abbreviations are fine, no emojis, no hashtags, no quotes, no name prefix. ")
            .Append("Stay a player inside the game: never say you are an AI, a bot or a language model, never mention prompts or real-world news. ")
            .Append("Keep it friendly and PG-13. If you do not know something about the game, say so casually instead of making things up.");
        if (options.ExtraPrompt.Length > 0) system.Append(' ').Append(options.ExtraPrompt);
        string[] recent;
        lock (gate) recent = [.. history.GetValueOrDefault(Channel(bot.Race)) ?? []];
        var user = new StringBuilder();
        if (recent.Length > 0) user.Append("Recent chat:\n").AppendJoin('\n', recent).Append("\n\n");
        var kind = what.Kind.TrimEnd('!');
        user.Append(kind switch
        {
            "whisper" => $"{what.Heard!.Speaker} whispers to you: \"{what.Heard.Content}\". Write your whisper back to them.",
            "reply" when what.Heard is { } h => $"Answer {h.Speaker}'s last message (\"{h.Content}\") in the channel.",
            _ => "Say something in the channel: about what you are doing, a question for other players, a joke, or a reaction to the chat above.",
        });
        return [("system", system.ToString()), ("user", user.ToString())];
    }

    /// <summary>A model's text as a chat line: first line, no quotes or "Name:" prefix, no stage directions, within 100 characters.</summary>
    public static string? Clean(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Thinking models wrap their reasoning in <think> tags: drop it.
        text = Regex.Replace(text, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = Regex.Replace(line, $@"^\s*(\[[^\]]*\]\s*)?{Regex.Escape(name)}\s*:\s*", "", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"\*[^*]{1,40}\*", "").Trim();
        line = line.Trim().Trim('"', '\'', '“', '”').Trim();
        if (line.Length == 0 || Regex.IsMatch(line, @"\b(as an ai|language model|i am an ai|i'm an ai)\b", RegexOptions.IgnoreCase)) return null;
        var clipped = ChatRelay.Clip(line);
        return clipped.Length == 0 ? null : clipped;
    }

    static readonly string[] AmbientLines =
    [
        "anyone up for a group in {place}?", "these mobs in {place} hit way too hard", "lvl {level} {class} lf party", "finally got a decent drop",
        "how long till the next field boss?", "{place} is nice this time of day", "need kinah lol", "any {class} tips?", "back from afk",
        "this grind never ends", "anyone selling mats?", "gg on that boss earlier", "who else is out in {place}?", "o/",
    ];
    static readonly string[] ReplyLines =
    [
        "haha true", "not sure tbh", "yeah same", "{speaker} lol", "good question, no idea", "agreed", "nice", "ty", "omw", "same here",
        "maybe check the map?", "lol", "hey {speaker}", "fair enough",
    ];

    /// <summary>A short canned line (no model configured, or it failed), filled with the bot's own details.</summary>
    string Canned(BotSpeaker bot, Pending what)
    {
        var pool = what.Heard is null ? AmbientLines : ReplyLines;
        return pool[rng.Next(pool.Length)].Replace("{place}", bot.Place ?? "here").Replace("{level}", bot.Level.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Replace("{class}", bot.ClassName.ToLowerInvariant()).Replace("{speaker}", what.Heard?.Speaker ?? "");
    }

    /// <summary>A subzone's internal name as players would say it ("AltamiaHighland_Eastern_Subzone" -> "Altamia Highland Eastern").</summary>
    public static string? PlaceName(string? area)
    {
        if (string.IsNullOrWhiteSpace(area)) return null;
        var name = Regex.Replace(area, @"_?Subzone\w*$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"(?<=[a-z])(of|the)(?=[A-Z])", " $1 ");
        name = Regex.Replace(name, @"^L\d_", "");
        name = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])|_", " ");
        name = Regex.Replace(name, @"\b(default|pc|\d+)\b", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.Length == 0 ? null : name;
    }
}

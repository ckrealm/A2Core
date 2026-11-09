using System.Collections.Concurrent;
using Aion.Application.Chat;

namespace Aion.World;

/// <summary>Characters currently in the world on this host: name → game session (and its account, when authenticated).</summary>
public sealed class WorldPresence
{
    public sealed record Entry(GameSession Session, Guid? AccountId, DateTimeOffset Since);

    readonly ConcurrentDictionary<string, Entry> byName = new(StringComparer.OrdinalIgnoreCase);
    int connections;

    public void Enter(string name, GameSession session, Guid? accountId) => byName[name] = new Entry(session, accountId, DateTimeOffset.UtcNow);

    /// <summary>Open game connections, in the world or not (staff overview).</summary>
    public int Connections => Volatile.Read(ref connections);
    public void Connected() => Interlocked.Increment(ref connections);
    public void Disconnected() => Interlocked.Decrement(ref connections);

    /// <summary>Everyone in the world now (staff overview).</summary>
    public IReadOnlyList<Entry> All => [.. byName.Values];

    public void Leave(string name, GameSession session)
    {
        if (byName.TryGetValue(name, out var entry) && entry.Session == session) byName.TryRemove(new KeyValuePair<string, Entry>(name, entry));
    }

    public Entry? Find(string name) => byName.GetValueOrDefault(name);

    /// <summary>
    /// Aion:Native:MaxPlayers (OURS): whether a login of <paramref name="account"/> must be turned away because the host already has
    /// that many game connections (in the world or on the character screen). An account already in the world may always come
    /// back (a reconnect replaces its old connection); null or 0 is no cap.
    /// </summary>
    public bool Full(int? maxPlayers, Guid account) =>
        maxPlayers is > 0 and var max && Connections >= max && !byName.Values.Any(e => e.AccountId == account);

    /// <summary>The session playing a stored character, if it is in the world.</summary>
    public Entry? FindCharacter(Guid character) => byName.Values.FirstOrDefault(e => e.Session.CharacterId == character);
}

/// <summary>Who may use GM commands, from trusted configuration and authentication only.</summary>
public sealed record GmPolicy
{
    /// <summary>Development logins only: character names granted GM (no accounts exist there).</summary>
    public IReadOnlySet<string> DevelopmentNames { get; init; } = new HashSet<string>();
    /// <summary>
    /// Authenticated accounts: the account's staff role ("admin" or "gamemaster", see
    /// CharacterNames.DisplayRole), or null for players. Read from the database on each call. A character
    /// carries the role only when flagged staff (CharacterState.Staff; GameSession.IsAdmin).
    /// </summary>
    public Func<Guid, CancellationToken, Task<string?>> StaffRole { get; init; } = (_, _) => Task.FromResult<string?>(null);
    /// <summary>Item catalogue search (template ID or name), when the catalogue is available.</summary>
    public Func<Guid, string, CancellationToken, Task<IReadOnlyList<(int Id, string Name)>>>? SearchItems { get; init; }
    /// <summary>Stored grant: (staff account, character, template ID, count).</summary>
    public Func<Guid, Guid, int, long, CancellationToken, Task>? GrantItem { get; init; }
    /// <summary>Stored level change: (staff account, character, level).</summary>
    public Func<Guid, Guid, int, CancellationToken, Task>? SetLevel { get; init; }
    /// <summary>
    /// Stored class change: (staff account, character, class name) → whether it changed, the
    /// class's name, and the classes available to the character (its race and gender).
    /// </summary>
    public Func<Guid, Guid, string, CancellationToken, Task<(bool Changed, string? Name, IReadOnlyList<string> Available)>>? ChangeClass { get; init; }
    /// <summary>Stored ascension grade: (staff account, character, grade 1..6).</summary>
    public Func<Guid, Guid, int, CancellationToken, Task>? SetAscension { get; init; }
    /// <summary>Stored wing registration: (staff account, character, Wing.dat ids).</summary>
    public Func<Guid, Guid, int[], CancellationToken, Task>? AcquireWings { get; init; }
}

/// <summary>
/// "//&lt;builder command&gt;" in chat (e.g. "//SetInvisible true"): a fallback for the client console's "s &lt;command&gt;"
/// (0x362e), running the same <see cref="BuilderCommands"/> on the sender's game loop. The
/// sender is the character in the world on this host under the name the client sends with
/// the message; rights and audit are the builder commands'.
/// </summary>
public sealed class GmCommands(WorldPresence presence, BuilderCommands builder) : IChatCommandHandler
{
    public bool Owns(string content) => content.TrimStart().StartsWith('/');

    /// <summary>
    /// "//report &lt;text&gt;" for every player (not a builder command): a ticket for staff with the sender's account and
    /// character; the reply to show. Null: reports are not available on this host.
    /// </summary>
    public Func<ChatIdentity, string, string, CancellationToken, Task<string>>? Report { get; init; }

    /// <summary>"//bot ..." (docs/PLAYERBOTS.md): staff, or every player with Aion:Bots:PlayersMayUse. Null: no playerbots on this host.</summary>
    public PlayerBots? Bots { get; init; }

    /// <summary>"//ah ..." (docs/MARKET.md): the market for every player. Null: no market on this host (no game database).</summary>
    public MarketCommands? Market { get; init; }

    /// <summary>"//buff" and "//hub" (Aion:Fun, docs/friends-server/FUN.md) for every player. Null: off on this host.</summary>
    public FunFeatures? Fun { get; init; }

    /// <summary>Commands every player may type with a single slash (the client's own slash commands never reach the server).</summary>
    static readonly HashSet<string> PlayerWords = new(StringComparer.OrdinalIgnoreCase) { "help", "report", "buff", "hub", "bot", "ah", "boost", "mail", "pmail", "sendkinah", "senditem", "dungeon", "lfg", "lfm" };

    static bool Is(string line, string word) =>
        line.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (line.Length == word.Length || char.IsWhiteSpace(line[word.Length]));

    public async Task<string> HandleAsync(ChatIdentity sender, string userName, string content, CancellationToken ct)
    {
        var text = content.Trim();
        string line;
        if (text.StartsWith("//", StringComparison.Ordinal)) line = text[2..].Trim();
        // One slash works for the player commands (/buff, /hub, /report, /help, /bot, /ah, /boost, /mail, /pmail, /sendkinah, /senditem,
        // /dungeon);
        // "//" works for them too, and builder (GM) commands keep "//".
        else if (text.StartsWith('/') && text[1..].Split(' ', 2)[0] is var word && PlayerWords.Contains(word)) line = text[1..].Trim();
        else return "Unknown command. Use /help.";
        if (Is(line, "dungeon")) return await DungeonAsync(userName, line[7..].Trim(), ct);
        if (line.StartsWith("report", StringComparison.OrdinalIgnoreCase) && (line.Length == 6 || char.IsWhiteSpace(line[6])))
        {
            var report = line[6..].Trim();
            if (Report is null) return "Reports are not available on this server.";
            if (report.Length < 3) return "Write what happened: /report <text> (a bug, a player, or being stuck).";
            return await Report(sender, Aion.Core.Characters.CharacterNames.Stored(userName), report, ct);
        }
        // The client may send the displayed name ("[GM] Elyra"); the world knows the stored one.
        var actor = presence.Find(userName) ?? presence.Find(Aion.Core.Characters.CharacterNames.Stored(userName));
        if (actor is null) return "Enter the world first.";
        try
        {
            // //ah (Aion:Market, docs/MARKET.md): every player; "//ah help" lists its commands.
            if (line.StartsWith("ah", StringComparison.OrdinalIgnoreCase) && (line.Length == 2 || char.IsWhiteSpace(line[2])))
                return Market is { } market ? await market.HandleAsync(actor.Session, line[2..].Trim(), ct) : "The market is not available on this server.";
            if (line.StartsWith("bot", StringComparison.OrdinalIgnoreCase) && (line.Length == 3 || char.IsWhiteSpace(line[3])))
                return await BotAsync(actor.Session, line[3..].Trim(), ct);
            if (Is(line, "lfg") || Is(line, "lfm")) return await LfgAsync(actor.Session, line[3..].Trim(), ct);
            if (Fun is { Options.BuffCommand: true } && Is(line, "buff"))
                return await actor.Session.InvokeAsync(now => actor.Session.FunBuff(now)).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (Fun is { Options.HubCommand: true } && Is(line, "hub"))
                return await actor.Session.InvokeAsync(now => actor.Session.FunHub(line[3..].Trim(), now)).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (line.Length == 0 || line.Equals("help", StringComparison.OrdinalIgnoreCase))
                return await builder.AllowedAsync(actor.Session, ct)
                    ? "Builder commands (in chat: //<command>; console: s <command>): " + string.Join(", ", BuilderCommands.Supported)
                        + (Bots is { Options.Enabled: true } ? "; //bot add <character> for playerbots" : "")
                    : "Commands: /report <text> sends a report to the staff. " + GameSession.DungeonHelp
                        + (Fun is { Options.BuffCommand: true } ? " /buff gives you the food and scroll buffs." : "")
                        + (Fun is { Options.HubCommand: true } ? " /hub teleports to a town or the Abyss." : "")
                        + (Market is not null ? " /ah help shows the auction house commands." : "")
                        + (Bots is { Options: { Enabled: true, Recruits: true } } ? " /lfg <what> [tank|heal|dps] posts your group: bots of the missing roles join it." : "")
                        + (Bots is { Options: { Enabled: true, PlayersMayUse: true } } ? " /bot add <character> summons one of your characters as a bot." : "");
            return await actor.Session.InvokeAsync(async now =>
            {
                var outcome = await builder.RunAsync(actor.Session, line, now, ct);
                return (outcome.Message, outcome.Packets);
            }).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception error) when (error is TimeoutException or TaskCanceledException)
        {
            return "The game connection did not respond.";
        }
    }

    /// <summary>/dungeon (GameSession.DungeonCommand): every player; runs on the sender's game loop.</summary>
    async Task<string> DungeonAsync(string userName, string args, CancellationToken ct)
    {
        var actor = presence.Find(userName) ?? presence.Find(Aion.Core.Characters.CharacterNames.Stored(userName));
        if (actor is null) return "Enter the world first.";
        if (actor.Session.Bot) return "Bots follow their party.";
        try
        {
            return await actor.Session.InvokeAsync(now => actor.Session.DungeonCommand(args, now)).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception error) when (error is TimeoutException or TaskCanceledException)
        {
            return "The game connection did not respond.";
        }
    }

    /// <summary>/lfg (PlayerBots.Recruit): every player when Aion:Bots:Recruits; runs on the sender's game loop.</summary>
    async Task<string> LfgAsync(GameSession actor, string args, CancellationToken ct)
    {
        if (Bots is not { Options: { Enabled: true, Recruits: true } } bots) return "Bots do not join posted groups on this server.";
        if (actor.Bot) return "Bots do not post groups.";
        return await actor.InvokeAsync(now => (bots.Recruit(actor, args, now), new List<byte[]>())).WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    /// <summary>//bot: allowed for staff, or for everyone with Aion:Bots:PlayersMayUse; runs on the sender's game loop.</summary>
    async Task<string> BotAsync(GameSession actor, string args, CancellationToken ct)
    {
        if (Bots is not { Options.Enabled: true } bots) return "Playerbots are off on this server.";
        if (actor.Bot) return "Bots do not command bots.";
        if (!bots.Options.PlayersMayUse && !await builder.AllowedAsync(actor, ct)) return "Playerbots are for staff on this server.";
        return await actor.InvokeAsync(async now => (await bots.CommandAsync(actor, args, now, ct), new List<byte[]>()))
            .WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
}

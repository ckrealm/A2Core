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

    public async Task<string> HandleAsync(ChatIdentity sender, string userName, string content, CancellationToken ct)
    {
        var text = content.Trim();
        if (!text.StartsWith("//", StringComparison.Ordinal)) return "Unknown command. Use //help.";
        var line = text[2..].Trim();
        if (line.StartsWith("report", StringComparison.OrdinalIgnoreCase) && (line.Length == 6 || char.IsWhiteSpace(line[6])))
        {
            var report = line[6..].Trim();
            if (Report is null) return "Reports are not available on this server.";
            if (report.Length < 3) return "Write what happened: //report <text> (a bug, a player, or being stuck).";
            return await Report(sender, Aion.Core.Characters.CharacterNames.Stored(userName), report, ct);
        }
        // The client may send the displayed name ("[GM] Elyra"); the world knows the stored one.
        var actor = presence.Find(userName) ?? presence.Find(Aion.Core.Characters.CharacterNames.Stored(userName));
        if (actor is null) return "Enter the world first.";
        try
        {
            if (line.StartsWith("bot", StringComparison.OrdinalIgnoreCase) && (line.Length == 3 || char.IsWhiteSpace(line[3])))
                return await BotAsync(actor.Session, line[3..].Trim(), ct);
            // Player mail (Aion:Alts:PlayerMail, GameSession.Alts.cs; off: as before). "//mail" without a '|' stays the staff builder's Mail for staff.
            var mailWord = line.Split(' ', 2)[0];
            if (actor.Session.Alts.PlayerMail && GameSession.MailCommands.FirstOrDefault(c => c.Equals(mailWord, StringComparison.OrdinalIgnoreCase)) is { } mailCommand
                && (mailCommand != "mail" || line.Contains('|') || !await builder.AllowedAsync(actor.Session, ct)))
                return await actor.Session.InvokeAsync(async _ => await actor.Session.PlayerMailAsync(mailCommand, line[mailWord.Length..].Trim(), ct))
                    .WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (line.Length == 0 || line.Equals("help", StringComparison.OrdinalIgnoreCase))
                return await builder.AllowedAsync(actor.Session, ct)
                    ? "Builder commands (in chat: //<command>; console: s <command>): " + string.Join(", ", BuilderCommands.Supported)
                        + (Bots is { Options.Enabled: true } ? "; //bot add <character> for playerbots" : "")
                        + (actor.Session.Alts.PlayerMail ? "; player mail: " + GameSession.MailUsage : "")
                    : "Commands: //report <text> sends a report to the staff."
                        + (actor.Session.Alts.PlayerMail ? " Mail: " + GameSession.MailUsage + "." : "")
                        + (Bots is { Options: { Enabled: true, PlayersMayUse: true } } ? " //bot add <character> summons one of your characters as a bot." : "");
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

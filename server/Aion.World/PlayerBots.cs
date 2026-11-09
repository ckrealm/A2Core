using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Aion:Bots (OURS: the official game has no playerbots). Off unless <see cref="Enabled"/>; players (not only staff) may summon
/// bots only with <see cref="PlayersMayUse"/>.
/// </summary>
public sealed record BotOptions
{
    public bool Enabled { get; init; }
    /// <summary>Non-staff players may use //bot (else staff only).</summary>
    public bool PlayersMayUse { get; init; }
    /// <summary>Bots one account may have in the world at once.</summary>
    public int MaxPerAccount { get; init; } = 3;
    /// <summary>Bots on this host at once.</summary>
    public int MaxTotal { get; init; } = 20;
    /// <summary>Bots join their leader's party (up to the party limit; the rest only follow).</summary>
    public bool JoinParty { get; init; } = true;
    /// <summary>A bot farther than this from its leader on the same map, or on another map, is moved next to it (cm).</summary>
    public double TeleportBeyond { get; init; } = 4000;
    /// <summary>
    /// Every player may post a group with /lfg; bots of the missing roles then join the party one by one (docs/PLAYERBOTS.md
    /// "Posting a group"). Bots never join a group by themselves otherwise.
    /// </summary>
    public bool Recruits { get; init; } = true;
    /// <summary>OURS: hired bots join this long apart (ms), the first one this long after the post.</summary>
    public int RecruitJoinMs { get; init; } = 3000;

    public BotOptions Validated() =>
        MaxPerAccount < 0 || MaxTotal < 0 ? throw new InvalidOperationException("Aion:Bots:MaxPerAccount and MaxTotal must be 0 or more.")
        : TeleportBeyond < 1000 ? throw new InvalidOperationException("Aion:Bots:TeleportBeyond must be at least 1000 (cm).")
        : RecruitJoinMs < 0 ? throw new InvalidOperationException("Aion:Bots:RecruitJoinMs must be 0 or more.") : this;
}

/// <summary>One playerbot: a character in the world without a connection, its leader (owner) and its brain.</summary>
public sealed class PlayerBot
{
    public enum Phase { Pending, InWorld, Leaving, Gone }

    internal PlayerBot(GameSession session, GameSession owner, string name, Guid? characterId, uint selectKey, int creationId)
    {
        Session = session;
        Owner = owner;
        Name = name;
        CharacterId = characterId;
        SelectKey = selectKey;
        CreationId = creationId;
    }

    public GameSession Session { get; }
    public GameSession Owner { get; }
    public string Name { get; }
    public Guid? CharacterId { get; }
    internal uint SelectKey { get; }
    internal int CreationId { get; }
    public Phase State { get; internal set; }
    public BotBrain? Brain { get; internal set; }
    public bool InParty { get; internal set; }
    public string? LastError { get; internal set; }
    /// <summary>Why the bot is not with its leader right now (an instance copy it cannot follow into), for //bot list.</summary>
    public string? Note { get; internal set; }
    /// <summary>The next try at following the leader into (or out of) an instance copy.</summary>
    internal long CopyRetryAt { get; set; }
    internal WorldTurn? Turn { get; init; }
    /// <summary>Held while the bot loop works on this bot (taken before the world lock, never inside it).</summary>
    internal SemaphoreSlim Busy { get; } = new(1, 1);
    internal Queue<int> Loads { get; } = new();
    /// <summary>A bot hired by a posted group (/lfg): a new unsaved character of the poster's race and level that leaves with the group.</summary>
    public bool Hired { get; internal init; }
    /// <summary>The role a hired bot was hired for (tank, heal, dps).</summary>
    public string? HiredRole { get; internal init; }
    /// <summary>What the group was posted for (/lfg's words), for /lfg list.</summary>
    public string? Activity { get; internal init; }
    /// <summary>A hired bot logs in no sooner than this (they arrive one by one).</summary>
    internal long EnterAt { get; init; }
}

/// <summary>
/// Playerbots (docs/PLAYERBOTS.md): characters of the summoner's own account played by the server. A bot is a normal
/// <see cref="GameSession"/> with no socket (the null transport): its loop feeds it client request bodies (login, select, load,
/// moves, casts) and ticks it every 50 ms like a connection does, under its own <see cref="WorldTurn"/>; what it would send to a
/// client is dropped, except the map loads it answers. Others see it through the session's own broadcast paths.
/// </summary>
public sealed class PlayerBots(GameData data, BotOptions options, SharedWorld world, BotRotations? rotations = null, WorldPresence? presence = null)
{
    public const int TickMs = 50;
    readonly List<PlayerBot> bots = [];
    readonly Lock gate = new();

    public BotOptions Options => options;
    public BotRotations Rotations { get; } = rotations ?? BotRotations.Embedded;

    public IReadOnlyList<PlayerBot> All { get { lock (gate) return [.. bots]; } }
    public IReadOnlyList<PlayerBot> Of(GameSession owner) { lock (gate) return bots.Where(b => b.Owner == owner && b.State != PlayerBot.Phase.Gone).ToList(); }
    static Guid? Account(GameSession s) => s.Store?.AccountId;

    /// <summary>
    /// //bot commands of <paramref name="owner"/> (run on its loop: the caller checked staff or Aion:Bots:PlayersMayUse).
    /// add &lt;character&gt; [class], remove &lt;name|all&gt;, list, follow|stay|assist|passive [name|all], role &lt;tank|dps|heal&gt; [name|all].
    /// </summary>
    public async Task<string> CommandAsync(GameSession owner, string line, long now, CancellationToken ct)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!options.Enabled) return "Playerbots are off on this server (Aion:Bots:Enabled).";
        var verb = words.Length > 0 ? words[0].ToLowerInvariant() : "help";
        var who = words.Length > 1 ? words[1] : "all";
        switch (verb)
        {
            case "add" or "summon" when words.Length is 2 or 3: return await AddAsync(owner, words[1], words.Length == 3 ? words[2] : null, ct);
            case "remove" or "dismiss" when words.Length == 2: return Remove(owner, words[1]);
            case "list":
            {
                var mine = Of(owner);
                return mine.Count == 0 ? "No bots. //bot add <character> summons one of your characters."
                    : "Bots: " + string.Join(", ", mine.Select(b => $"{b.Name} ({b.State}{(b.Brain is { } br ? $", {br.Movement}, {(br.Assist ? "assist" : "passive")}, {br.Role}" : "")}{(b.LastError is { } e ? ", " + e : "")}{(b.Note is { } n ? ", " + n : "")})"));
            }
            case "follow" or "stay" or "assist" or "passive":
                return Apply(owner, who, b =>
                {
                    if (verb is "follow" or "stay") b.Movement = verb == "follow" ? BotMovement.Follow : BotMovement.Stay;
                    else b.Assist = verb == "assist";
                }, verb);
            case "role" when words.Length is 2 or 3 && words[1].ToLowerInvariant() is "tank" or "dps" or "heal":
            {
                var role = words[1].ToLowerInvariant();
                return Apply(owner, words.Length == 3 ? words[2] : "all", b => b.Role = role, "role " + role);
            }
            default:
                return "//bot add <character> | remove <name|all> | list | follow | stay | assist | passive [name] | role <tank|dps|heal> [name]";
        }
    }

    string Apply(GameSession owner, string who, Action<BotBrain> change, string what)
    {
        var chosen = Of(owner).Where(b => b.Brain is not null && (who.Equals("all", StringComparison.OrdinalIgnoreCase) || b.Name.Equals(who, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var b in chosen) change(b.Brain!);
        return chosen.Count == 0 ? $"No bot named {who}." : $"{what}: {string.Join(", ", chosen.Select(b => b.Name))}.";
    }

    /// <summary>
    /// Summons one of the owner's other characters (stored logins), or on development logins a new unsaved character named
    /// <paramref name="name"/> (<paramref name="className"/> picks its class). Checks the caps; the bot enters the world on the
    /// next bot tick. Call on the owner's loop.
    /// </summary>
    public async Task<string> AddAsync(GameSession owner, string name, string? className, CancellationToken ct)
    {
        if (!options.Enabled) return "Playerbots are off on this server (Aion:Bots:Enabled).";
        if (!owner.InWorld || owner.Bot) return "Enter the world first.";
        if (owner.Options.World != world) return "Playerbots need the shared world.";
        lock (gate)
        {
            var live = bots.Where(b => b.State != PlayerBot.Phase.Gone).ToList();
            if (live.Count >= options.MaxTotal) return $"This server already has {options.MaxTotal} bots (Aion:Bots:MaxTotal).";
            var account = Account(owner);
            var mine = live.Count(b => !b.Hired && (account is not null ? Account(b.Owner) == account : b.Owner == owner));
            if (mine >= options.MaxPerAccount) return $"You already have {options.MaxPerAccount} bots (Aion:Bots:MaxPerAccount).";
            if (live.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return $"{name} is already a bot.";
        }
        Guid? characterId = null;
        uint key;
        var creation = 1;
        string botName;
        if (owner.Store is { } store)
        {
            IReadOnlyList<CharacterState> mine;
            try { mine = await store.LobbyAsync(ct); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return "Your characters could not be read."; }
            var state = mine.FirstOrDefault(c => c.Summary.Name.Equals(CharacterNames.Stored(name), StringComparison.OrdinalIgnoreCase));
            if (state is null) return $"{name} is not one of your characters on this server.";
            if (state.Summary.CharacterId == owner.CharacterId) return "You cannot summon yourself.";
            if (world.FindOnline(state.Summary.CharacterId) is not null) return $"{state.Summary.Name} is already in the world.";
            characterId = state.Summary.CharacterId;
            key = (uint)state.WireId << 16;
            botName = state.Summary.Name;
        }
        else
        {
            // Development logins have no stored characters: the bot is a new unsaved one.
            if (world.FindOnline(name) is not null) return $"{name} is already in the world.";
            if (className is not null)
            {
                var found = Enumerable.Range(1, 64).FirstOrDefault(c => data.Profile(c, 1)?.Class.Equals(className, StringComparison.OrdinalIgnoreCase) == true);
                if (found == 0) return $"{className} is not a class.";
                creation = found;
            }
            key = 65536;
            botName = name;
        }
        var turn = new WorldTurn(world);
        var session = new GameSession(data, owner.Options, new Random()) { Bot = true };
        if (owner.Store is { } ownerStore) session.Store = TurnStore.Wrap(TurnStore.Unwrap(ownerStore), turn);
        var bot = new PlayerBot(session, owner, botName, characterId, key, creation) { Turn = turn };
        lock (gate)
        {
            // Checked again: another summon may have taken the last place while the characters were read.
            if (bots.Count(b => b.State != PlayerBot.Phase.Gone) >= options.MaxTotal) return $"This server already has {options.MaxTotal} bots (Aion:Bots:MaxTotal).";
            bots.Add(bot);
        }
        return $"Summoning {botName}.";
    }

    /// <summary>OURS: the classes hired for each role, in order of preference (a class already in the group is skipped while another is left).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> HireClasses = new Dictionary<string, string[]>
    {
        ["tank"] = ["Templar", "Gladiator", "Fighter"],
        ["heal"] = ["Cleric", "Chanter"],
        ["dps"] = ["Sorcerer", "Ranger", "Assassin", "Gladiator", "Elementalist", "Chanter", "Fighter"],
    };

    /// <summary>OURS: names for hired bots (a number is added when one is taken).</summary>
    static readonly string[] HireNames =
    [
        "Aerin", "Belka", "Caius", "Daria", "Eldric", "Fiora", "Galen", "Hesta", "Ivor", "Jalen", "Kira", "Lysa", "Marek", "Nyla",
        "Orin", "Perra", "Quill", "Rhea", "Soren", "Talia", "Ulric", "Vesna", "Wren", "Xara", "Yorik", "Zelda",
    ];

    /// <summary>The role a group member plays: a bot's brain role, a player's by class (Templar tank, Cleric and Chanter heal).</summary>
    static string RoleOf(GameSession member) => member.BotRole ?? member.ClassName switch
    {
        "Templar" => "tank",
        "Cleric" or "Chanter" => "heal",
        _ => "dps",
    };

    /// <summary>
    /// /lfg (and /lfm): the poster's group for something ("/lfg Ludra", "/lfg Fire Temple tank heal") is posted, and bots of the
    /// roles it lacks join its party one by one, <see cref="BotOptions.RecruitJoinMs"/> apart: as the words name them (a number
    /// before a role repeats it), else one tank and one healer if the group has none, then damage dealers to the party limit.
    /// Hired bots are new unsaved characters of the poster's race and level; they leave with the group (kicked from the party,
    /// the poster gone, or "/lfg cancel"). "/lfg list" shows them. Call on the poster's loop.
    /// </summary>
    public string Recruit(GameSession poster, string args, long now)
    {
        if (!options.Enabled || !options.Recruits) return "Bots do not join posted groups on this server (Aion:Bots:Recruits).";
        if (!poster.InWorld || poster.Bot) return "Enter the world first.";
        if (poster.Options.World != world) return "Bots need the shared world.";
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hired = Of(poster).Where(b => b.Hired).ToList();
        if (words.Length == 1 && words[0].ToLowerInvariant() is "cancel" or "done" or "stop")
        {
            foreach (var b in hired) b.State = PlayerBot.Phase.Leaving;
            return hired.Count == 0 ? "You have no posted group." : $"Group closed: {string.Join(", ", hired.Select(b => b.Name))} leave.";
        }
        if (words.Length == 1 && words[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            return hired.Count == 0 ? "No bots joined your group. /lfg <what> [tank|heal|dps] posts one."
                : $"Hired for {hired[0].Activity}: " + string.Join(", ", hired.Select(b => $"{b.Name} ({b.Session.ClassName ?? "?"}, {b.HiredRole}{(b.State == PlayerBot.Phase.Pending ? ", on the way" : "")})"));
        if (!poster.LeadsOrSolo) return "Only your party's leader can post the group.";
        // What the group is for, and the roles asked for ("2 dps" counts twice).
        var asked = new List<string>();
        var activity = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i].ToLowerInvariant();
            var count = 1;
            if (int.TryParse(w, out var n) && n is > 0 and <= 9 && i + 1 < words.Length && Role(words[i + 1]) is not null) { count = n; w = words[++i].ToLowerInvariant(); }
            if (Role(w) is { } role) asked.AddRange(Enumerable.Repeat(role, count));
            else activity.Add(words[i]);
        }
        var what = activity.Count > 0 ? string.Join(' ', activity) : "anything";
        var members = poster.PartyMembers();
        var coming = hired.Where(b => b.State == PlayerBot.Phase.Pending).ToList();
        var open = GameSession.PartyLimit - members.Count - coming.Count;
        if (open <= 0) return members.Count >= GameSession.PartyLimit ? "Your party is full." : "Bots are already on their way to fill your party.";
        var roles = members.Select(RoleOf).Concat(coming.Select(b => b.HiredRole ?? "dps")).ToList();
        var classes = members.Select(m => m.ClassName).Concat(coming.Select(b => b.Session.ClassName)).OfType<string>().ToList();
        var needed = new List<string>();
        if (asked.Count > 0) needed.AddRange(asked.Take(open));
        else
        {
            if (!roles.Contains("tank")) needed.Add("tank");
            if (!roles.Contains("heal") && needed.Count < open) needed.Add("heal");
            while (needed.Count < open) needed.Add("dps");
        }
        var hires = new List<string>();
        lock (gate)
        {
            var live = bots.Count(b => b.State != PlayerBot.Phase.Gone);
            var start = Math.Max(now, coming.Select(b => b.EnterAt).DefaultIfEmpty(now).Max());
            foreach (var role in needed)
            {
                if (live >= options.MaxTotal) break;
                var cls = HireClasses[role].FirstOrDefault(c => !classes.Contains(c) && poster.CreationFor(c) != 0)
                          ?? HireClasses[role].FirstOrDefault(c => poster.CreationFor(c) != 0);
                if (cls is null) continue;
                var name = HireName();
                var session = new GameSession(data, poster.Options with { CharacterLevel = Math.Max(1, poster.Level) }, new Random()) { Bot = true };
                start += options.RecruitJoinMs;
                bots.Add(new PlayerBot(session, poster, name, null, 65536, poster.CreationFor(cls))
                {
                    Turn = new WorldTurn(world), Hired = true, HiredRole = role, Activity = what, EnterAt = start,
                });
                classes.Add(cls);
                hires.Add($"{name} ({cls}, {role})");
                live++;
            }
        }
        if (hires.Count == 0) return $"No bots are free right now (Aion:Bots:MaxTotal {options.MaxTotal}).";
        return $"Group posted for {what}: {string.Join(", ", hires)} will join one by one. /lfg cancel closes it.";
    }

    static string? Role(string word) => word.ToLowerInvariant() switch
    {
        "tank" or "tanks" => "tank",
        "heal" or "heals" or "healer" or "healers" => "heal",
        "dps" or "dd" or "damage" => "dps",
        _ => null,
    };

    /// <summary>A free name for a hired bot (not a bot and not online). Under the gate.</summary>
    string HireName()
    {
        for (var round = 0; ; round++)
            foreach (var basis in HireNames.OrderBy(_ => Random.Shared.Next()))
            {
                var name = round == 0 ? basis : basis + round;
                if (!bots.Any(b => b.State != PlayerBot.Phase.Gone && b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) && world.FindOnline(name) is null)
                    return name;
            }
    }

    /// <summary>Dismisses a bot (or all) of the owner; it leaves the world on the next bot tick.</summary>
    public string Remove(GameSession owner, string name)
    {
        var all = name.Equals("all", StringComparison.OrdinalIgnoreCase);
        var chosen = Of(owner).Where(b => all || b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        // The bot loop does the leaving (it may be in the middle of this bot's login right now).
        foreach (var b in chosen) b.State = PlayerBot.Phase.Leaving;
        return chosen.Count == 0 ? $"No bot named {name}." : $"Dismissed {string.Join(", ", chosen.Select(b => b.Name))}.";
    }

    /// <summary>One bot tick for every bot: login, leaving, the session's timers and the brain's requests.</summary>
    public async Task TickAsync(long now, CancellationToken ct = default)
    {
        foreach (var bot in All)
        {
            await bot.Busy.WaitAsync(ct);
            try
            {
                if (bot.State == PlayerBot.Phase.Gone) continue;
                await bot.Turn!.EnterWorldAsync(ct);
                try { await StepAsync(bot, now, ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    bot.LastError = error.GetType().Name;
                    bot.State = PlayerBot.Phase.Leaving;
                }
                finally { bot.Turn.Exit(); }
                // LeaveAsync never throws (a failing bot is still removed), so one bot cannot stop the others' ticks.
                if (bot.State == PlayerBot.Phase.Leaving) await LeaveAsync(bot, now);
            }
            finally { bot.Busy.Release(); }
        }
    }

    /// <summary>The bot loop (the host runs it): every bot every <see cref="TickMs"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (await timer.WaitForNextTickAsync(ct))
            await TickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
    }

    /// <summary>The owner's connection ends: its bots leave and save before its store closes. Call outside the world lock.</summary>
    public async Task OwnerLeftAsync(GameSession owner, CancellationToken ct = default)
    {
        foreach (var bot in Of(owner))
        {
            await bot.Busy.WaitAsync(ct);
            try
            {
                if (bot.State == PlayerBot.Phase.Gone) continue;
                bot.State = PlayerBot.Phase.Leaving;
                await LeaveAsync(bot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            finally { bot.Busy.Release(); }
        }
    }

    async Task StepAsync(PlayerBot bot, long now, CancellationToken ct)
    {
        var session = bot.Session;
        if (bot.State == PlayerBot.Phase.Pending)
        {
            if (now >= bot.EnterAt) await EnterAsync(bot, now, ct);
            return;
        }
        if (bot.State != PlayerBot.Phase.InWorld) return;
        // The leader left the world (logout, character screen), or staff kicked the bot: it leaves.
        if (!bot.Owner.InWorld || bot.Owner.WorldPlayer is not { } leader || !world.Online.ContainsKey(leader.Key) || session.CloseReason is not null)
        {
            bot.State = PlayerBot.Phase.Leaving;
            return;
        }
        // A hired bot kicked from the group (or whose group broke up) leaves.
        if (bot.Hired && !session.InParty)
        {
            bot.State = PlayerBot.Phase.Leaving;
            return;
        }
        await Feed(bot, await session.TickAsync(now, ct), now, ct);
        if (session.Dead) return;   // the session revives it at its respawn point after GameSession.ReviveDelayMs
        if (await FollowCopyAsync(bot, now, ct)) return;
        if (FarFromLeader(bot) is { } spot)
        {
            await Feed(bot, session.Teleport(spot.Map, spot.At, now), now, ct);
            return;
        }
        foreach (var request in bot.Brain!.Think(now))
        {
            var replies = await session.HandleAsync(request, now, ct);
            bot.Brain.Observe(replies, now);
            await Feed(bot, replies, now, ct);
        }
    }

    /// <summary>A leader on another map or beyond TeleportBeyond, in the same copy as the bot (or both outside one): the spot next to it.</summary>
    (int Map, Vector3d At)? FarFromLeader(PlayerBot bot)
    {
        var (session, owner) = (bot.Session, bot.Owner);
        if (owner.MapId is not { } map || owner.WorldPosition is not { } lead || session.WorldPosition is not { } me || owner.InstanceKey != session.InstanceKey) return null;
        var near = map == session.MapId && Math.Sqrt((me.X - lead.X) * (me.X - lead.X) + (me.Y - lead.Y) * (me.Y - lead.Y)) <= options.TeleportBeyond;
        return near ? null : (map, bot.Brain!.SlotAround(lead));
    }

    /// <summary>OURS: waits between tries at following into a copy, and after a try that did not get in.</summary>
    public const int CopyRetryMs = 3000, CopyGiveUpMs = 30_000;

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>
    /// Instance copies (docs/PLAYERBOTS.md "Instances"). A bot in a copy its leader is not in leaves it with the client's 0x4004.
    /// A leader in a copy the bot is not in: a bot in its party goes to where the leader entered from (a teleport, as following
    /// does), then uses the same entry a player uses there, the dungeon's portal (0x3636) or entry NPC (0x4006); the session's
    /// EnterPartyDungeon puts a party member into the copy a member is in, with the entry rules (MapEntrance level, item level,
    /// quest step; a full copy) a player has. Copies with no such entry (a trigger box's solo copy, a staff EnterDungeon, sealed
    /// one-member dungeons) are not followed: the bot waits outside. True while this step is about copies.
    /// </summary>
    async Task<bool> FollowCopyAsync(PlayerBot bot, long now, CancellationToken ct)
    {
        var (session, owner) = (bot.Session, bot.Owner);
        if (session.InstanceKey != 0 && session.InstanceKey != owner.InstanceKey)
        {
            if (now < bot.CopyRetryAt) return true;
            // Leaving a copy of its own while the leader is in another one means the try to join it failed: wait a while.
            bot.CopyRetryAt = now + (owner.InstanceKey != 0 ? CopyGiveUpMs : CopyRetryMs);
            await Feed(bot, await session.HandleAsync(BotRequests.LeaveInstance(), now, ct), now, ct);
            return true;
        }
        if (owner.InstanceKey == 0 || session.InstanceKey == owner.InstanceKey)
        {
            bot.Note = null;
            return false;
        }
        if (now < bot.CopyRetryAt) return true;
        bot.CopyRetryAt = now + CopyRetryMs;
        var room = owner.MapId is { } copyMap ? MapEventTables.Embedded.DungeonOfMap(copyMap) : null;
        if (!bot.InParty || room is null || room.MaxMember == 1 || owner.InstanceReturn is not { } back || owner.MapId is not { } inside)
        {
            bot.Note = !bot.InParty ? "not in the leader's party, waits outside its instance" : "waits outside the leader's solo instance";
            return true;
        }
        if (session.MapId != back.Map || session.WorldPosition is not { } me || Flat(me, back.At) > options.TeleportBeyond)
        {
            await Feed(bot, session.Teleport(back.Map, back.At, now), now, ct);
            return true;
        }
        if (DungeonEntry(session, inside, back.At) is not { } entry)
        {
            bot.Note = $"no portal or entry NPC to map {inside} here, waits outside";
            return true;
        }
        if (Flat(me, entry.At) > 150)
        {
            await Feed(bot, session.Teleport(back.Map, entry.At with { X = entry.At.X + 50 }, now), now, ct);
            return true;
        }
        bot.Note = null;
        bot.CopyRetryAt = now + entry.WaitMs + CopyRetryMs;
        await Feed(bot, await session.HandleAsync(entry.Request, now, ct), now, ct);
        return true;
    }

    /// <summary>The nearest portal (EnvObjData PartyDungeon/EnterDungeon) or entry NPC on the bot's map leading to the copy's map.</summary>
    static (byte[] Request, Vector3d At, int WaitMs)? DungeonEntry(GameSession session, int copyMap, Vector3d near)
    {
        var tables = MapEventTables.Embedded;
        bool Leads(int dungeon) => tables.Dungeon(dungeon)?.Map == copyMap;
        var found = new List<(byte[] Request, Vector3d At, int WaitMs)>();
        if (session.MapId is { } map)
        {
            var objects = WorldObjects.ObjectsOn(map);
            for (var i = 0; i < objects.Count; i++)
                if (objects[i].Portal is { } portal && portal.Any(Leads))
                    found.Add((BotRequests.UseObject(WorldObjects.FirstKey + i, objects[i].Env), objects[i].At, objects[i].InteractTime));
        }
        foreach (var npc in session.Targets.Values)
            if (!npc.Dead && tables.EntryNpc(npc.NpcId) is { Dungeon: not 0 } e && Leads(e.Dungeon)) found.Add((BotRequests.NpcConversation(npc.Key), npc.Position, 0));
        return found.Count == 0 ? null : found.MinBy(f => Flat(f.At, near));
    }

    /// <summary>The null transport: drops what a client would get, answers map loads (0x3621) as the client does (0x3622).</summary>
    async Task Feed(PlayerBot bot, List<byte[]> packets, long now, CancellationToken ct)
    {
        foreach (var p in packets)
            if (p.Length >= 6 && p[0] == 0x21 && p[1] == 0x36) bot.Loads.Enqueue(BitConverter.ToInt32(p, 2));
        while (bot.Loads.TryDequeue(out var ticket))
        {
            var loaded = await bot.Session.HandleAsync(BotRequests.LoadComplete(ticket), now, ct);
            foreach (var p in loaded)
                if (p.Length >= 6 && p[0] == 0x21 && p[1] == 0x36) bot.Loads.Enqueue(BitConverter.ToInt32(p, 2));
        }
    }

    /// <summary>The client's login steps, fed to the session: game login, (create,) select, load complete.</summary>
    async Task EnterAsync(PlayerBot bot, long now, CancellationToken ct)
    {
        var session = bot.Session;
        var owner = bot.Owner;
        if (!owner.InWorld) { bot.State = PlayerBot.Phase.Leaving; return; }
        if (bot.CharacterId is { } id && world.FindOnline(id) is not null) { bot.LastError = "already in the world"; bot.State = PlayerBot.Phase.Leaving; return; }
        await session.HandleAsync(BotRequests.GameLogin($"bot:{bot.CharacterId ?? Guid.Empty}"), now, ct);
        if (session.Store is null) await session.HandleAsync(BotRequests.Create(bot.Name, bot.CreationId), now, ct);
        var selected = await session.HandleAsync(BotRequests.Select(bot.SelectKey), now, ct);
        await Feed(bot, selected, now, ct);
        if (!session.InWorld)
        {
            bot.LastError = session.PersistenceErrors.LastOrDefault() ?? "could not enter the world";
            bot.State = PlayerBot.Phase.Leaving;
            return;
        }
        int slot;
        lock (gate) slot = bots.Count(b => b.Owner == owner && b != bot && b.State == PlayerBot.Phase.InWorld);
        bot.Brain = new BotBrain(data, session, owner, Rotations.For(session.ClassName), slot, Rotations.GlobalCooldownMs, Rotations.TankHateMultiplier);
        if (bot.State != PlayerBot.Phase.Pending) return;   // dismissed while it logged in: it leaves at once
        bot.State = PlayerBot.Phase.InWorld;
        presence?.Enter(session.CharacterName!, session, session.Store?.AccountId);
        if (options.JoinParty || bot.Hired) bot.InParty = session.JoinPartyAsBot(owner);
        // A hired bot that finds the group full (players joined meanwhile) is not needed.
        if (bot.Hired && !bot.InParty) { bot.State = PlayerBot.Phase.Leaving; return; }
        // Next to the leader at once rather than at its saved spot (the teleport check would move it on the next tick anyway).
        if (FarFromLeader(bot) is { } spot) await Feed(bot, session.Teleport(spot.Map, spot.At, now), now, ct);
    }

    /// <summary>Checks only: runs inside <see cref="LeaveAsync"/>'s world turn (a throw there stands for a failing leave).</summary>
    internal Action<PlayerBot>? LeaveFault { get; set; }

    /// <summary>
    /// Leaves the world (others see it go, it leaves the party) and saves its position. Holds the bot's Busy. Never throws:
    /// whatever fails, the bot ends Gone and out of the list (its error in <see cref="PlayerBot.LastError"/>), so a broken bot
    /// cannot be retried forever nor stop the bot loop.
    /// </summary>
    async Task LeaveAsync(PlayerBot bot, long now)
    {
        var session = bot.Session;
        try
        {
            await bot.Turn!.EnterWorldAsync(CancellationToken.None);
            try
            {
                LeaveFault?.Invoke(bot);
                if (session.CharacterName is { } name) presence?.Leave(name, session);
                session.ExitSharedWorld();
                session.Abandon();
            }
            finally { bot.Turn.Exit(); }
            await session.CheckpointAsync(now, force: true, CancellationToken.None);
        }
        catch (Exception error) { bot.LastError = error.GetType().Name; }
        finally
        {
            bot.State = PlayerBot.Phase.Gone;
            lock (gate) bots.Remove(bot);
        }
    }
}

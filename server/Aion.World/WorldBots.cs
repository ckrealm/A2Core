using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Aion:WorldBots (OURS: the official game has no bots). World bots are server-owned characters that live in the open world like
/// AzerothCore's random bots: each has a class, a level and a race, picks a hunting ground that suits its level on its race's maps,
/// walks around there, fights monsters with its class rotation, loots, rests, levels up, and moves on. Off unless <see cref="Enabled"/>.
/// </summary>
public sealed record WorldBotOptions
{
    public bool Enabled { get; init; }
    /// <summary>World bots in the world at once.</summary>
    public int Count { get; init; } = 20;
    /// <summary>The lowest and highest starting level (0: the level cap).</summary>
    public int MinLevel { get; init; } = 10;
    public int MaxLevel { get; init; } = 0;
    /// <summary>light, dark or both (half each).</summary>
    public string Race { get; init; } = "both";
    /// <summary>Bots may also hunt on the maps both races share (the Abyss, where players of the other race can attack them).</summary>
    public bool Abyss { get; init; }
    /// <summary>Only these maps (empty: every race map with hunting grounds).</summary>
    public int[] Maps { get; init; } = [];
    /// <summary>The roster's random seed: the same seed gives the same names, classes and levels after every restart.</summary>
    public int Seed { get; init; } = 1204;
    /// <summary>Names to use first (the rest are generated).</summary>
    public string[] Names { get; init; } = [];
    /// <summary>How long a bot stays at one hunting ground before it moves to another (minutes, a random share of it is added).</summary>
    public double GroundMinutes { get; init; } = 12;
    /// <summary>Bots that log in per second while the roster fills (spreads the start-up cost).</summary>
    public int LoginsPerSecond { get; init; } = 4;
    /// <summary>
    /// How much less damage world bots take from monsters: 2 halves it (their auto gear has no enchants, stones or titles, and past
    /// level 30 monsters out-hit unenchanted gear). 1 treats them exactly like players.
    /// </summary>
    public double Toughness { get; init; } = 2;
    /// <summary>Monsters a bot hunts: from its level minus this...</summary>
    public int LevelsBelow { get; init; } = 6;
    /// <summary>...to its level plus this.</summary>
    public int LevelsAbove { get; init; } = 0;

    public WorldBotOptions Validated() =>
        Count < 0 ? throw new InvalidOperationException("Aion:WorldBots:Count must be 0 or more.")
        : MinLevel < 1 || MaxLevel < 0 || MaxLevel != 0 && MaxLevel < MinLevel ? throw new InvalidOperationException("Aion:WorldBots:MinLevel/MaxLevel are out of range.")
        : Race.ToLowerInvariant() is not ("light" or "dark" or "both") ? throw new InvalidOperationException("Aion:WorldBots:Race must be light, dark or both.")
        : GroundMinutes <= 0 || LoginsPerSecond < 1 || LevelsBelow < 0 || LevelsAbove < 0 || Toughness < 1 ? throw new InvalidOperationException("Aion:WorldBots: GroundMinutes, LoginsPerSecond and the level band must be positive.")
        : this;
}

/// <summary>One world bot: an unsaved character the server plays, with its roster entry and brain.</summary>
public sealed class WorldBot
{
    public enum Phase { Pending, InWorld, Leaving, Gone }

    internal WorldBot(string name, string className, int creationId, int gender, int race, int level, string personality)
    {
        Name = name;
        ClassName = className;
        CreationId = creationId;
        Gender = gender;
        Race = race;
        StartLevel = level;
        Personality = personality;
    }

    public string Name { get; }
    public string ClassName { get; }
    internal int CreationId { get; }
    internal int Gender { get; }
    /// <summary>1 Light (Elyos), 2 Dark (Asmodian).</summary>
    public int Race { get; }
    public int StartLevel { get; }
    /// <summary>A few words on how the bot talks in chat (BotChat).</summary>
    public string Personality { get; }
    public Phase State { get; internal set; }
    public GameSession? Session { get; internal set; }
    public BotBrain? Brain { get; internal set; }
    public WorldBotRoam? Roam { get; internal set; }
    /// <summary>A content bot (<see cref="WorldBots.Spawn"/>): not in the roster, enters at <see cref="Start"/>, gone for good when it leaves.</summary>
    public bool Content { get; internal init; }
    internal (int Map, Vector3d At)? Start { get; init; }
    internal Func<WorldBot, IBotWander>? WanderFactory { get; init; }
    public string? LastError { get; internal set; }
    /// <summary>Next login try after a failed one.</summary>
    internal long RetryAt { get; set; }
    internal WorldTurn? Turn { get; set; }
    internal Queue<int> Loads { get; } = new();
    internal int GearedAt { get; set; }

    public int Level => Session?.Level ?? StartLevel;
    public string RaceName => Race == 2 ? "Asmodian" : "Elyos";

    /// <summary>What the bot is doing, in a few words (chat context, //bot world).</summary>
    public string Activity
    {
        get
        {
            if (Session is not { InWorld: true } s) return "logging in";
            if (s.Dead) return "dead, waiting to revive";
            if (Brain?.FightingKey is not null) return "fighting monsters";
            if (Roam?.Resting == true) return "resting after a fight";
            return Brain?.Moving == true ? "walking around looking for monsters" : "standing around";
        }
    }
}

/// <summary>
/// World bots (docs/WORLDBOTS.md). Each is a <see cref="GameSession"/> without a socket, like a playerbot (docs/PLAYERBOTS.md): the
/// loop feeds it the client's own requests (login, create, select, load complete, moves, casts) so others see it on the same paths
/// as a player, and its <see cref="BotBrain"/> runs with the bot as its own leader and a <see cref="WorldBotRoam"/> that picks what
/// to hunt and where to walk. World bots are unsaved: the roster (names, classes, races, starting levels) comes from the seed, so
/// the same bots come back after a restart, at their starting levels. Map work (ticks, moves) runs in the map's turn so maps run in
/// parallel; casts and teleports take the world turn, as a player's do.
/// </summary>
public sealed class WorldBots
{
    public const int TickMs = 50;
    static readonly string[] Classes = ["Gladiator", "Templar", "Assassin", "Ranger", "Sorcerer", "Elementalist", "Cleric", "Chanter"];
    static readonly string[] Personalities =
    [
        "friendly and helpful, likes to give tips", "sarcastic but good-natured", "a cheerful newbie who asks a lot of questions",
        "a veteran who has seen it all and is a bit tired", "a loot goblin always talking about drops and Kinah", "quiet, says little, dry humour",
        "competitive, brags about kills and gear", "a laid-back player who chats about anything", "an eager roleplayer who stays in character",
        "a casual player who complains about the grind", "an excitable player who uses caps sometimes", "a trader always looking for deals",
    ];

    readonly GameData data;
    readonly WorldBotOptions options;
    readonly SharedWorld world;
    readonly GameSessionOptions sessionOptions;
    readonly WorldPresence? presence;
    readonly IReadOnlyList<HuntingGround> grounds;
    readonly List<WorldBot> roster;
    readonly Lock gate = new();
    long nextLoginAt;

    public WorldBots(GameData data, WorldBotOptions options, SharedWorld world, GameSessionOptions sessionOptions, BotRotations? rotations = null,
        WorldPresence? presence = null, IReadOnlyList<HuntingGround>? grounds = null)
    {
        this.data = data;
        this.options = options;
        this.world = world;
        this.sessionOptions = sessionOptions with { World = world, Builder = null, IntroCharacter = null, WorldRace = null, TrainingDummy = false };
        this.presence = presence;
        this.grounds = grounds ?? HuntingGrounds.Embedded;
        Rotations = rotations ?? BotRotations.Embedded;
        roster = options.Enabled ? Roster(options, data) : [];
    }

    public WorldBotOptions Options => options;
    public BotRotations Rotations { get; }
    public IReadOnlyList<HuntingGround> Grounds => grounds;

    /// <summary>The whole roster, in the world or not.</summary>
    public IReadOnlyList<WorldBot> All { get { lock (gate) return [.. roster]; } }
    /// <summary>The bots in the world now.</summary>
    public IReadOnlyList<WorldBot> InWorld { get { lock (gate) return roster.Where(b => b.State == WorldBot.Phase.InWorld).ToList(); } }

    /// <summary>
    /// The roster from the seed: races alternate (or one race), classes are dealt so every class appears, levels spread evenly from
    /// MinLevel to MaxLevel (shuffled), names from <see cref="WorldBotOptions.Names"/> then generated.
    /// </summary>
    public static List<WorldBot> Roster(WorldBotOptions options, GameData data)
    {
        var rng = new Random(options.Seed);
        var max = options.MaxLevel == 0 ? data.MaxLevel : Math.Min(options.MaxLevel, data.MaxLevel);
        var min = Math.Min(options.MinLevel, max);
        var levels = Enumerable.Range(0, options.Count).Select(i => options.Count <= 1 ? min : min + (int)Math.Round((max - min) * i / (double)(options.Count - 1))).ToList();
        Shuffle(levels, rng);
        var deck = new List<string>();
        var names = new NameMaker(rng, options.Names);
        var list = new List<WorldBot>();
        for (var i = 0; i < options.Count; i++)
        {
            if (deck.Count == 0) { deck.AddRange(Classes); Shuffle(deck, rng); }
            var className = deck[^1];
            deck.RemoveAt(deck.Count - 1);
            var race = options.Race.ToLowerInvariant() switch { "light" => 1, "dark" => 2, _ => i % 2 == 0 ? 1 : 2 };
            var creation = Enumerable.Range(1, 16).FirstOrDefault(c => data.Profile(c, 1) is { } p && p.Class == className && p.Race == race);
            if (creation == 0) creation = race == 2 ? 9 : 1;
            list.Add(new WorldBot(names.Next(), className, creation, rng.Next(2) + 1, race, levels[i], Personalities[rng.Next(Personalities.Length)]));
        }
        return list;
    }

    static void Shuffle<T>(List<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// A content bot (PvP fillers, events; docs/WORLDBOTS.md "Content bots"): a generated character of this class, race and level
    /// that logs in on the next tick at <paramref name="map"/> / <paramref name="at"/> and follows the caller's
    /// <paramref name="wander"/> (where to go and what to fight when its brain has nothing else to do), else hunts there like a
    /// roster bot. It is not in the roster, does not come back after <see cref="Dismiss"/> or a failure, and works with
    /// Aion:WorldBots off (Count 0). Watch <see cref="WorldBot.State"/> for InWorld.
    /// </summary>
    public WorldBot Spawn(string className, int race, int level, int map, Vector3d at, Func<WorldBot, IBotWander>? wander = null, string? name = null)
    {
        race = race == 2 ? 2 : 1;
        var creation = Enumerable.Range(1, 16).FirstOrDefault(c => data.Profile(c, 1) is { } p && p.Class.Equals(className, StringComparison.OrdinalIgnoreCase) && p.Race == race);
        if (creation == 0) throw new ArgumentException($"{className} is not a class.", nameof(className));
        lock (gate)
        {
            var maker = new NameMaker(new Random(Environment.TickCount ^ roster.Count), []);
            foreach (var b in roster) maker.Reserve(b.Name);
            var bot = new WorldBot(name is { Length: > 0 } wanted && !roster.Any(b => b.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) ? wanted : maker.Next(),
                data.Profile(creation, 1)!.Class, creation, 1 + roster.Count % 2, race, data.ClampLevel(level), Personalities[roster.Count % Personalities.Length])
            {
                Content = true, Start = (map, at), WanderFactory = wander,
            };
            roster.Add(bot);
            return bot;
        }
    }

    /// <summary>A content bot (or any bot) leaves the world on the next tick; a content bot is then forgotten.</summary>
    public void Dismiss(WorldBot bot)
    {
        if (bot.State is WorldBot.Phase.Pending) { bot.State = WorldBot.Phase.Gone; lock (gate) if (bot.Content) roster.Remove(bot); }
        else if (bot.State == WorldBot.Phase.InWorld) bot.State = WorldBot.Phase.Leaving;
    }

    /// <summary>One tick: logs in the next pending bots (LoginsPerSecond; content bots at once), steps every bot in the world.</summary>
    public async Task TickAsync(long now, CancellationToken ct = default)
    {
        foreach (var bot in All)
        {
            try
            {
                switch (bot.State)
                {
                    case WorldBot.Phase.Leaving:
                        await LeaveAsync(bot);
                        if (bot.Content) lock (gate) roster.Remove(bot);
                        break;
                    case WorldBot.Phase.Pending when bot.Content:
                        await EnterAsync(bot, now, ct);
                        break;
                    case WorldBot.Phase.Pending when now >= nextLoginAt && now >= bot.RetryAt:
                        nextLoginAt = now + 1000 / options.LoginsPerSecond;
                        await EnterAsync(bot, now, ct);
                        break;
                    case WorldBot.Phase.InWorld:
                        await StepAsync(bot, now, ct);
                        break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                bot.LastError = error.GetType().Name + ": " + error.Message;
                await LeaveAsync(bot);
                bot.State = WorldBot.Phase.Pending;
                bot.RetryAt = now + 60_000;
                if (bot.Content) { bot.State = WorldBot.Phase.Gone; lock (gate) roster.Remove(bot); }
            }
        }
    }

    /// <summary>The loop (the host runs it): every bot every <see cref="TickMs"/>.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (await timer.WaitForNextTickAsync(ct))
            await TickAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
    }

    /// <summary>Every bot leaves the world (shutdown).</summary>
    public async Task StopAsync()
    {
        foreach (var bot in All)
            if (bot.State == WorldBot.Phase.InWorld) await LeaveAsync(bot);
    }

    /// <summary>The client's login steps (game login, create, select, load complete), then straight to a hunting ground.</summary>
    async Task EnterAsync(WorldBot bot, long now, CancellationToken ct)
    {
        if (world.FindOnline(bot.Name) is not null)
        {
            bot.LastError = "a player of that name is in the world";
            bot.RetryAt = now + 60_000;
            if (bot.Content) { bot.State = WorldBot.Phase.Gone; lock (gate) roster.Remove(bot); }
            return;
        }
        var start = bot.Start is null ? Pick(bot.Race, bot.StartLevel, new Random(options.Seed ^ bot.Name.GetHashCode(StringComparison.Ordinal) ^ (int)now)) : null;
        var turn = new WorldTurn(world);
        var session = new GameSession(data, sessionOptions with { CharacterLevel = bot.StartLevel, DevelopmentMap = start?.Map ?? bot.Start?.Map }, new Random()) { Bot = true };
        bot.Session = session;
        bot.Turn = turn;
        await turn.EnterWorldAsync(ct);
        try
        {
            await session.HandleAsync(BotRequests.GameLogin($"worldbot:{bot.Name}"), now, ct);
            await session.HandleAsync(BotRequests.Create(bot.Name, bot.CreationId, bot.Gender), now, ct);
            session.EquipUnsaved(Gear(data, bot.ClassName, bot.Race, bot.StartLevel));
            bot.GearedAt = bot.StartLevel;
            Toughen(session);
            await Feed(bot, await session.HandleAsync(BotRequests.Select(65536), now, ct), now, ct);
            if (!session.InWorld)
            {
                bot.LastError = "could not enter the world";
                bot.RetryAt = now + 60_000;
                return;
            }
            bot.Brain = new BotBrain(data, session, session, Rotations.For(session.ClassName), 0, Rotations.GlobalCooldownMs, 1);
            bot.Roam = new WorldBotRoam(this, bot, new Random(options.Seed + roster.IndexOf(bot)));
            bot.Brain.Wander = bot.WanderFactory?.Invoke(bot) ?? bot.Roam;
            bot.State = WorldBot.Phase.InWorld;
            bot.LastError = null;
            presence?.Enter(session.CharacterName!, session, null);
            if (bot.Start is { } fixedStart)
            {
                // A content bot hunts around its start when its own wander has nothing for it.
                bot.Roam.Arrive(new HuntingGround(fixedStart.Map, bot.Race, fixedStart.At, bot.StartLevel, bot.StartLevel, bot.StartLevel, 0), now, stay: true);
                await Feed(bot, session.Teleport(fixedStart.Map, fixedStart.At, now), now, ct);
            }
            else if (start is not null)
            {
                bot.Roam.Arrive(start, now);
                await Feed(bot, session.Teleport(start.Map, start.Centre, now), now, ct);
            }
        }
        finally { turn.Exit(); }
    }

    /// <summary>
    /// Toughness T as defense: hits are scaled by 1000 / (1000 + defense), so a bonus of (T - 1) x (1000 + defense) makes them 1/T.
    /// </summary>
    void Toughen(GameSession session)
    {
        session.BotDefense = 0;
        session.BotDefense = (int)Math.Round((options.Toughness - 1) * (1000 + session.Defense));
    }

    /// <summary>A world bot re-gears after gaining this many levels.</summary>
    public const int GearEveryLevels = 3;

    /// <summary>Equip slots world bots fill (EquipDetail: 1 weapon, 3 helm, 4 shoulder, 5 torso, 6 pants, 7 gloves, 8 boots, 19 cape).</summary>
    static readonly (string Category, byte Slot)[] ArmourSlots =
        [("Helmet", 3), ("Shoulder", 4), ("Torso", 5), ("Pants", 6), ("Gloves", 7), ("Boots", 8), ("Cape", 19)];

    /// <summary>
    /// Auto gear (OURS, like mod-playerbots' random bot gear): for the class weapon and each armour slot, the item the bot may wear
    /// (permit level at most its level, its race or any) with the highest item level, the lowest item ID on ties. A bot on starter
    /// gear dies to monsters of its own level.
    /// </summary>
    public static IReadOnlyList<(int ItemId, byte Slot)> Gear(GameData data, string className, int race, int level)
    {
        var weapon = data.ClassWeaponType(className);
        var wanted = new List<(string Category, byte Slot)>(ArmourSlots);
        if (weapon is not null) wanted.Insert(0, (weapon, 1));
        var gear = new List<(int, byte)>();
        foreach (var (category, slot) in wanted)
        {
            var best = data.Items.Where(i => i.Info.Category == category && i.Info.PermitLevel <= level && (i.Info.Race == 0 || i.Info.Race == race))
                .OrderByDescending(i => i.Info.Level).ThenBy(i => i.ItemId).Select(i => i.ItemId).FirstOrDefault();
            if (best != 0) gear.Add((best, slot));
        }
        return gear;
    }

    /// <summary>A hunting ground for this race and level (random among those that fit), null when the data has none.</summary>
    internal HuntingGround? Pick(int race, int level, Random rng, HuntingGround? not = null)
    {
        var fit = HuntingGrounds.For(grounds, race, level, options.Abyss, options.Maps, options.LevelsBelow, options.LevelsAbove);
        if (fit.Count > 1 && not is not null) fit = fit.Where(g => g != not).ToList();
        return fit.Count == 0 ? null : fit[rng.Next(fit.Count)];
    }

    async Task StepAsync(WorldBot bot, long now, CancellationToken ct)
    {
        var session = bot.Session!;
        var turn = bot.Turn!;
        if (session.CloseReason is not null || !session.InWorld)
        {
            await LeaveAsync(bot);
            bot.State = WorldBot.Phase.Pending;
            bot.RetryAt = now + 60_000;
            if (bot.Content) { bot.State = WorldBot.Phase.Gone; lock (gate) roster.Remove(bot); }
            return;
        }
        // The session's timers: map work in the map's turn (maps run in parallel), world work (a revive, posted work) alone.
        await InTurnAsync(turn, session, () => !session.NeedsWorldTick(now), async () => await Feed(bot, await session.TickAsync(now, ct), now, ct), ct);
        if (session.Dead) return;
        // New gear every few levels it gains (unsaved, like the character).
        if (session.Level >= bot.GearedAt + GearEveryLevels)
        {
            session.EquipUnsaved(Gear(data, bot.ClassName, bot.Race, session.Level));
            bot.GearedAt = session.Level;
            Toughen(session);
        }
        if (bot.Roam?.Relocation(now) is { } go)
        {
            await turn.EnterWorldAsync(ct);
            try { await Feed(bot, session.Teleport(go.Map, go.Centre, now), now, ct); }
            finally { turn.Exit(); }
            return;
        }
        var requests = await InTurnAsync(turn, session, () => true, () => Task.FromResult(bot.Brain!.Think(now)), ct);
        if (requests.Count == 0) return;
        // Moves are map work; a cast (and anything else) is world work, as for a player's connection.
        await InTurnAsync(turn, session, () => requests.All(GameSession.IsMapRequest), async () =>
        {
            foreach (var request in requests)
            {
                var replies = await session.HandleAsync(request, now, ct);
                bot.Brain!.Observe(replies, now);
                await Feed(bot, replies, now, ct);
            }
        }, ct);
    }

    /// <summary>Runs <paramref name="work"/> in the map's turn when <paramref name="mapWork"/> holds and the bot is on a shared map, else the world turn.</summary>
    static async Task<T> InTurnAsync<T>(WorldTurn turn, GameSession session, Func<bool> mapWork, Func<Task<T>> work, CancellationToken ct)
    {
        if (session.SharedMap is { } map)
        {
            await turn.EnterMapAsync(map, ct);
            try
            {
                if (session.SharedMap == map && mapWork()) return await work();
            }
            finally { turn.Exit(); }
        }
        await turn.EnterWorldAsync(ct);
        try { return await work(); }
        finally { turn.Exit(); }
    }

    static Task InTurnAsync(WorldTurn turn, GameSession session, Func<bool> mapWork, Func<Task> work, CancellationToken ct) =>
        InTurnAsync(turn, session, mapWork, async () => { await work(); return true; }, ct);

    /// <summary>The null transport: drops what a client would get, answers map loads (0x3621) as the client does (0x3622).</summary>
    static async Task Feed(WorldBot bot, List<byte[]> packets, long now, CancellationToken ct)
    {
        foreach (var p in packets)
            if (p.Length >= 6 && p[0] == 0x21 && p[1] == 0x36) bot.Loads.Enqueue(BitConverter.ToInt32(p, 2));
        while (bot.Loads.TryDequeue(out var ticket))
        {
            var loaded = await bot.Session!.HandleAsync(BotRequests.LoadComplete(ticket), now, ct);
            foreach (var p in loaded)
                if (p.Length >= 6 && p[0] == 0x21 && p[1] == 0x36) bot.Loads.Enqueue(BitConverter.ToInt32(p, 2));
        }
    }

    async Task LeaveAsync(WorldBot bot)
    {
        if (bot.Session is not { } session || bot.Turn is not { } turn) return;
        await turn.EnterWorldAsync(CancellationToken.None);
        try
        {
            if (session.CharacterName is { } name) presence?.Leave(name, session);
            session.ExitSharedWorld();
            session.Abandon();
        }
        finally { turn.Exit(); }
        bot.State = WorldBot.Phase.Gone;
        bot.Session = null;
        bot.Brain = null;
        bot.Roam = null;
    }

    /// <summary>The bots in the world as chat sees them (BotChat): what each is doing and where. Read-only; safe from any thread.</summary>
    public IReadOnlyList<BotSpeaker> Speakers()
    {
        var list = new List<BotSpeaker>();
        foreach (var bot in InWorld)
        {
            if (bot.Session is not { } s) continue;
            var place = s.MapId is { } map && s.WorldPosition is { } at ? BotChatter.PlaceName(Subzones.AreaAt(map, at)) : null;
            list.Add(new BotSpeaker(bot.Name, bot.Race, bot.ClassName, bot.Level, bot.Personality, bot.Activity, place));
        }
        return list;
    }

    /// <summary>Whether a world bot of this name is in the world (chat: whispers to it are answered by bot chat).</summary>
    public bool IsBot(string name) => InWorld.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Staff summary for //bot world.</summary>
    public string Summary()
    {
        var live = InWorld;
        if (!options.Enabled) return "World bots are off on this server (Aion:WorldBots:Enabled).";
        var byRace = live.GroupBy(b => b.RaceName).Select(g => $"{g.Count()} {g.Key}");
        var levels = live.Count == 0 ? "" : $", levels {live.Min(b => b.Level)}-{live.Max(b => b.Level)}";
        return $"World bots: {live.Count}/{options.Count} in the world ({string.Join(", ", byRace)}{levels}).";
    }

    /// <summary>Generated names: two or three syllables, capitalised, unique, not staff-like (OURS).</summary>
    sealed class NameMaker(Random rng, string[] first)
    {
        public void Reserve(string name) => used.Add(name);
        static readonly string[] Starts = ["Ae", "Al", "Ar", "Ba", "Bel", "Ca", "Cy", "Da", "Del", "El", "Er", "Fa", "Fen", "Ga", "Gal", "Ha", "Il", "Is", "Ja", "Ka", "Kel", "Ky", "La", "Lin", "Ma", "Mor", "Na", "Ny", "Or", "Pa", "Ra", "Rin", "Sa", "Sel", "Sha", "Ta", "Tir", "Va", "Vel", "Xa", "Ya", "Za", "Zel"];
        static readonly string[] Middles = ["ra", "ri", "lo", "na", "ve", "da", "mi", "ro", "le", "si", "ka", "the", "va", "ne", "lu", "dor", "gan", "mir"];
        static readonly string[] Ends = ["n", "s", "th", "ra", "el", "on", "is", "ar", "ia", "or", "us", "ix", "a", "en", "yn", "as", "iel", "wyn"];
        readonly HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        int given;

        public string Next()
        {
            while (given < first.Length)
            {
                var name = first[given++].Trim();
                if (name.Length is >= 2 and <= 16 && name.All(char.IsLetter) && used.Add(name)) return name;
            }
            for (var tries = 0; ; tries++)
            {
                var name = Starts[rng.Next(Starts.Length)] + (rng.Next(3) == 0 ? Middles[rng.Next(Middles.Length)] : "") + Ends[rng.Next(Ends.Length)];
                if (tries > 50) name += (char)('a' + rng.Next(26));
                if (name.Length is >= 3 and <= 12 && !Aion.Core.Characters.CharacterNames.IsStaffStyle(name) && used.Add(name)) return name;
            }
        }
    }
}

/// <summary>
/// How a world bot spends its time when its brain has nothing to fight, heal or loot (OURS): it hunts monsters of its level at its
/// hunting ground, rests when hurt or out of mana, idles a few seconds between walks, and after a while (or once it outlevels the
/// ground) moves to another ground: on foot when it is near, by teleport when it is on another map or far away.
/// </summary>
public sealed class WorldBotRoam(WorldBots owner, WorldBot bot, Random rng) : IBotWander
{
    /// <summary>A bot pulls monsters within this distance of itself (cm).</summary>
    public const double PullRange = 2200;
    /// <summary>Beyond this from its next ground (or on another map) a bot teleports there instead of walking (cm).</summary>
    public const double WalkUpTo = 12000;
    /// <summary>Rest below these shares of HP and MP until back above the upper ones (percent).</summary>
    public const double RestHpBelow = 65, RestMpBelow = 30, RestedHp = 95, RestedMp = 80;
    /// <summary>How often the bot looks for a monster to pull (ms): the search walks the map's NPC grid.</summary>
    public const int PullSearchMs = 750;

    long groundUntil, idleUntil, nextSearchAt;
    bool staying;
    Vector3d? spot;
    HuntingGround? relocating;
    int? pullKey;

    public HuntingGround? Ground { get; private set; }
    public bool Resting { get; private set; }

    GameSession Session => bot.Session!;

    /// <summary>
    /// The bot is at (or teleporting to) <paramref name="ground"/> from <paramref name="now"/> on; with <paramref name="stay"/> it never
    /// moves on (a content bot's start).
    /// </summary>
    public void Arrive(HuntingGround ground, long now, bool stay = false)
    {
        Ground = ground;
        groundUntil = stay ? long.MaxValue : now + (long)(owner.Options.GroundMinutes * 60_000 * (0.75 + rng.NextDouble() * 0.5));
        staying = stay;
        spot = null;
        idleUntil = now + rng.Next(500, 3000);
    }

    /// <summary>A teleport the bot should make now (to a new ground on another map or far away), once.</summary>
    public HuntingGround? Relocation(long now)
    {
        if (relocating is not { } go) return null;
        relocating = null;
        Arrive(go, now);
        return go;
    }

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    bool CheckRest()
    {
        var (hp, hpMax, mp, mpMax, _, _) = Session.Resource;
        if (hpMax <= 0) return false;
        var hpPct = hp * 100.0 / hpMax;
        var mpPct = mpMax > 0 ? mp * 100.0 / mpMax : 100;
        if (Resting) Resting = hpPct < RestedHp || mpPct < RestedMp;
        else Resting = hpPct < RestHpBelow || mpPct < RestMpBelow;
        return Resting;
    }

    public Target? Pull(long now, Vector3d me)
    {
        if (CheckRest() || Ground is not { } ground || Session.MapId != ground.Map || Flat(me, ground.Centre) > HuntingGround.Radius * 2) return null;
        if (Session.SharedMap is not { } map) return null;
        // The same pull until it is dead, engaged by a player, or out of range (the brain then fights it through AttackTarget).
        if (pullKey is { } held && map.Targets.GetValueOrDefault(held) is { } kept && Fits(map, kept, me, ground, PullRange * 1.5)) return kept;
        pullKey = null;
        if (now < nextSearchAt) return null;
        nextSearchAt = now + PullSearchMs;
        Target? best = null;
        var bestDistance = double.MaxValue;
        foreach (var key in SpatialGrid.Npcs(map.Targets).Near(me.X, me.Y, PullRange + SpatialGrid.NpcMargin))
        {
            if (map.Targets.GetValueOrDefault(key) is not { } npc || !Fits(map, npc, me, ground, PullRange)) continue;
            var d = Flat(me, npc.Position);
            if (d < bestDistance) (best, bestDistance) = (npc, d);
        }
        pullKey = best?.Key;
        return best;
    }

    /// <summary>A monster this bot may hunt: alive, hostile, ordinary, of a fitting level, near, and not someone else's fight.</summary>
    bool Fits(MapInstance map, Target npc, Vector3d me, HuntingGround ground, double range)
    {
        if (npc.Dead || !npc.Hostile || npc.Elite) return false;
        var level = Session.Level;
        if (npc.Level < level - owner.Options.LevelsBelow || npc.Level > level + owner.Options.LevelsAbove) return false;
        if (Flat(me, npc.Position) > range || Flat(ground.Centre, npc.Position) > HuntingGround.Radius * 1.5) return false;
        // Not a monster already fighting another player (no kill stealing).
        return map.Ai.Brains.GetValueOrDefault(npc.Key) is not { Engaged: true, VictimIsNpc: false } brain || brain.VictimKey == Session.PlayerKey;
    }

    public Vector3d? Destination(long now, Vector3d me)
    {
        if (CheckRest()) return null;
        var level = Session.Level;
        if (Ground is not { } ground || !staying && (now >= groundUntil || level > ground.Level + owner.Options.LevelsBelow || Session.MapId != ground.Map && relocating is null))
        {
            var next = owner.Pick(bot.Race, level, rng, Ground);
            if (next is null) return null;
            if (Session.MapId != next.Map || Flat(me, next.Centre) > WalkUpTo)
            {
                relocating = next;
                return null;
            }
            Arrive(next, now);
            ground = next;
        }
        // On the way to the ground: walk to its centre.
        if (Flat(me, ground.Centre) > HuntingGround.Radius * 1.2) return ground.Centre;
        // At the ground: walk to a random spot in it, idle a few seconds, pick another.
        if (spot is { } s && Flat(me, s) > BotBrain.ArriveWithin) return s;
        if (spot is not null)
        {
            spot = null;
            idleUntil = now + rng.Next(2000, 9000);
        }
        if (now < idleUntil) return null;
        var angle = rng.NextDouble() * Math.PI * 2;
        var distance = Math.Sqrt(rng.NextDouble()) * HuntingGround.Radius;
        spot = new Vector3d(ground.Centre.X + distance * Math.Cos(angle), ground.Centre.Y + distance * Math.Sin(angle), ground.Centre.Z);
        return spot;
    }
}

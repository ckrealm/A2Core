using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Aion:Abyss:Bots (OURS): the bots that fill organised PvP for a small server. With 5-10 friends the battlefield's 10-a-side teams, a
/// siege's two factions and the rifts would be empty, so world bots (WorldBots content bots, docs/WORLDBOTS.md) take the free places.
/// </summary>
public sealed record AbyssBotOptions
{
    /// <summary>Fill battlefield teams, siege factions and rifts with bots (needs the world bot loop, which the host then runs).</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Bots per faction that fight over the artifacts while a siege runs.</summary>
    public int SiegePerFaction { get; init; } = 8;
    /// <summary>Bots per faction that go through each open rift (rift PvP on) and fight in the enemy's field.</summary>
    public int RiftPerFaction { get; init; } = 3;
    /// <summary>At most this many PvP bots at once; idle ones leave after <see cref="IdleMinutes"/>.</summary>
    public int Max { get; init; } = 60;
    public int IdleMinutes { get; init; } = 10;

    public AbyssBotOptions Validated() =>
        SiegePerFaction is < 0 or > 100 || RiftPerFaction is < 0 or > 50 || Max is < 0 or > 500 || IdleMinutes < 1
            ? throw new InvalidOperationException("Aion:Abyss:Bots: SiegePerFaction 0-100, RiftPerFaction 0-50, Max 0-500, IdleMinutes >= 1.") : this;
}

/// <summary>What a PvP bot is doing for AbyssWar (its <see cref="GameSession.PvpJob"/>); the battlefield needs none (the match says it).</summary>
public enum PvpJob { None, Siege, Rift }

/// <summary>
/// A PvP bot's wander (BotBrain.Wander): fight the nearest player it may hit, else its job's objective (the battlefield's Shard Catalyst,
/// a siege core), else stand. The brain does the casting, healing and moving.
/// </summary>
public sealed class PvpWander(GameSession session) : IBotWander
{
    /// <summary>A bot engages enemy players within this distance of itself (cm, OURS).</summary>
    public const double EngageRange = 2500;

    public Target? Pull(long now, Vector3d me) => session.PvpFoe(me, EngageRange) ?? session.PvpObjective(me).Npc;
    public Vector3d? Destination(long now, Vector3d me) => session.PvpObjective(me).Point;
}

/// <summary>
/// The PvP bot pool (<see cref="IPvpBotSource"/>) over the world bots: <see cref="Take"/> hands out idle bots already in the world and
/// spawns more for the rest of the need (they are ready on a later call), <see cref="Release"/> takes them back (home, idle), and
/// <see cref="Tick"/> lets bots idle too long leave. Bots park at their race's home field start.
/// </summary>
public sealed class PvpBots(WorldBots bots, GameData data, AbyssBotOptions options) : IPvpBotSource
{
    sealed class Entry(WorldBot bot)
    {
        public WorldBot Bot { get; } = bot;
        public bool Busy { get; set; }
        public long IdleSince { get; set; }
    }

    readonly List<Entry> pool = [];
    readonly Lock gate = new();
    int spawned;

    public AbyssBotOptions Options => options;
    public int Count { get { lock (gate) return pool.Count; } }
    public int BusyCount { get { lock (gate) return pool.Count(e => e.Busy); } }

    /// <summary>The playable classes (one per creation row of the Light race), rotated through so a team gets a mix.</summary>
    readonly string[] classes = Enumerable.Range(1, 16).Select(c => data.Profile(c, 1)).OfType<StarterProfile>().Where(p => p.Race == 1)
        .Select(p => p.Class).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static int ParkMap(int race) => race == 2 ? 1110 : 1010;

    public IReadOnlyList<GameSession> Take(PvpNeed need, long now)
    {
        lock (gate)
        {
            pool.RemoveAll(e => e.Bot.State == WorldBot.Phase.Gone);
            var ready = pool.Where(e => !e.Busy && e.Bot is { State: WorldBot.Phase.InWorld, Session: { InWorld: true, Dead: false, InstanceKey: 0 } s }
                    && (need.Race == 0 || e.Bot.Race == need.Race) && s.Level >= need.MinLevel)
                .Take(need.Count).ToList();
            foreach (var e in ready) e.Busy = true;
            var pending = pool.Count(e => !e.Busy && e.Bot.State == WorldBot.Phase.Pending && (need.Race == 0 || e.Bot.Race == need.Race));
            var missing = Math.Min(need.Count - ready.Count - pending, options.Max - pool.Count);
            for (var i = 0; i < missing; i++)
            {
                var race = need.Race != 0 ? need.Race : 1 + spawned % 2;
                var map = ParkMap(race);
                var at = data.MapStart(map)?.Position ?? default;
                var className = classes.Length > 0 ? classes[spawned % classes.Length] : "Gladiator";
                spawned++;
                var level = Math.Max(need.MinLevel, 45);
                var bot = bots.Spawn(className, race, level, map, at with { X = at.X + (i % 5) * 150, Y = at.Y + i / 5 * 150 }, b => new PvpWander(b.Session!));
                pool.Add(new Entry(bot) { IdleSince = now });
            }
            return ready.Select(e => e.Bot.Session!).ToList();
        }
    }

    public void Release(GameSession session)
    {
        Entry? entry;
        lock (gate) entry = pool.FirstOrDefault(e => e.Bot.Session == session);
        if (entry is null) return;
        session.PvpJob = PvpJob.None;
        _ = session.InvokeAsync(at =>
        {
            lock (gate) { entry.Busy = false; entry.IdleSince = at; }
            // Home to park, unless still inside a copy (the copy's exit brings it out first; the next Take skips it until then).
            if (!session.InWorld || session.InstanceKey != 0 || session.MapId == ParkMap(session.Race)) return (true, new List<byte[]>());
            var park = data.MapStart(ParkMap(session.Race))?.Position ?? default;
            return (true, session.Teleport(ParkMap(session.Race), park, at));
        });
    }

    /// <summary>Bots idle longer than IdleMinutes leave the world.</summary>
    public void Tick(long now)
    {
        lock (gate)
            foreach (var e in pool.Where(e => !e.Busy && e.Bot.State == WorldBot.Phase.InWorld && now - e.IdleSince > options.IdleMinutes * 60_000L).ToList())
            {
                bots.Dismiss(e.Bot);
                pool.Remove(e);
            }
    }

    /// <summary>The sessions currently on a job of this kind.</summary>
    public IReadOnlyList<GameSession> OnJob(PvpJob job)
    {
        lock (gate) return pool.Where(e => e.Busy && e.Bot.Session is { } s && s.PvpJob == job).Select(e => e.Bot.Session!).ToList();
    }
}

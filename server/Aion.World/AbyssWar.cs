using System.Globalization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Aion:Abyss: the organised faction PvP of global Aion 2 (docs/ABYSS_PVP.md). Defaults follow the global service as of the
/// Nov 4 2026 patch where fan sources agree; the values no source gives are OURS and marked so.
/// </summary>
public sealed record AbyssOptions
{
    /// <summary>Run the sieges, rifts and battlefield at all (each also has its own switch).</summary>
    public bool Enabled { get; init; } = true;
    public SiegeOptions Siege { get; init; } = new();
    public RiftOptions Rifts { get; init; } = new();
    public BattlefieldOptions Battlefield { get; init; } = new();
    /// <summary>The clock the schedules run on (a TimeZoneInfo id such as "Europe/Berlin"); empty: the host's local time.</summary>
    public string TimeZone { get; init; } = "";

    public static AbyssOptions Default { get; } = new();

    TimeZoneInfo? zone;
    /// <summary>The schedule clock (checks set UTC).</summary>
    public TimeZoneInfo Zone { get => zone ??= TimeZone.Length == 0 ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(TimeZone); init => zone = value; }

    public AbyssOptions Validated()
    {
        if (TimeZone.Length > 0)
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZone); }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            { throw new InvalidOperationException($"Aion:Abyss:TimeZone '{TimeZone}' is not a time zone of this host."); }
        Siege.Validate();
        Rifts.Validate();
        Battlefield.Validate();
        return this;
    }

    internal static DayOfWeek Day(string name, string setting) =>
        Enum.TryParse<DayOfWeek>(name.Trim(), ignoreCase: true, out var day) ? day : throw new InvalidOperationException($"{setting}: '{name}' is not a day of the week.");
}

/// <summary>
/// Aion:Abyss:Siege, the artifact siege. Global: Thursday and Saturday at 21:00 (Nov 4 2026), three artifacts per layer, the faction
/// that lands the last hit on an artifact's Dimensional Core claims it, Executor bosses at 21:30, +2.5% Abyss Points per artifact
/// held, a 5-minute Abyss Corridor for the holder. OURS: the siege length, the capture points and how many contributors get them.
/// </summary>
public sealed record SiegeOptions
{
    public bool Enabled { get; init; } = true;
    public string[] Days { get; init; } = ["Thursday", "Saturday"];
    public int Hour { get; init; } = 21;
    public int Minute { get; init; }
    /// <summary>OURS: no source gives the length; the siege also ends once every artifact is claimed.</summary>
    public int DurationMinutes { get; init; } = 60;
    /// <summary>The Executors (the layers' AbBoss spawns) appear this long after the start (global: 21:30).</summary>
    public int ExecutorsAfterMinutes { get; init; } = 30;
    /// <summary>The Abyss layers with a siege: Lower Reshanta (map 20) and Middle (map 22, item level 3000).</summary>
    public int[] Layers { get; init; } = [20, 22];
    /// <summary>OURS: Abyss Points for each contributor of the capturing faction, and how many of them (by damage) get them.</summary>
    public int CapturePoints { get; init; } = 3000;
    public int RewardedPlayers { get; init; } = 30;
    /// <summary>Abyss Point gain per artifact the faction holds, in percent (global: 2.5, so 7.5 for all three).</summary>
    public double BonusPercentPerArtifact { get; init; } = 2.5;
    /// <summary>The Abyss Corridor (the artifact's AbyssArtifact dungeon): solo, this long, for the holding faction only.</summary>
    public int CorridorMinutes { get; init; } = 5;

    public long DurationMs => DurationMinutes * 60_000L;
    public long ExecutorsMs => ExecutorsAfterMinutes * 60_000L;
    public long CorridorMs => CorridorMinutes * 60_000L;

    internal void Validate()
    {
        foreach (var day in Days) AbyssOptions.Day(day, "Aion:Abyss:Siege:Days");
        if (Hour is < 0 or > 23 || Minute is < 0 or > 59) throw new InvalidOperationException("Aion:Abyss:Siege:Hour/Minute must be a time of day.");
        if (DurationMinutes is < 1 or > 600) throw new InvalidOperationException("Aion:Abyss:Siege:DurationMinutes must be between 1 and 600.");
        if (CorridorMinutes is < 1 or > 120) throw new InvalidOperationException("Aion:Abyss:Siege:CorridorMinutes must be between 1 and 120.");
        if (CapturePoints < 0 || RewardedPlayers < 0 || BonusPercentPerArtifact is < 0 or > 100) throw new InvalidOperationException("Aion:Abyss:Siege rewards must not be negative.");
    }
}

/// <summary>
/// Aion:Abyss:Rifts, the Spacetime Rifts. Global: every 3 hours a rift portal opens in each faction's home field for 10 minutes and
/// leads into the enemy's (Verteron ↔ Altgard); level 45; 400 a portal; an hour on the other side, then back home; no automatic PvP
/// (both players must have rift PvP on; it is switched in one's own territory with a 70-minute cooldown).
/// </summary>
public sealed record RiftOptions
{
    public bool Enabled { get; init; } = true;
    public int EveryHours { get; init; } = 3;
    /// <summary>The first opening of the day (global fan timetables: 02:00, 05:00, ... 23:00 server time).</summary>
    public int FirstHour { get; init; } = 2;
    public int OpenMinutes { get; init; } = 10;
    public int StayMinutes { get; init; } = 60;
    public int MinLevel { get; init; } = 45;
    public int MaxPerPortal { get; init; } = 400;
    public int PvpCooldownMinutes { get; init; } = 70;
    /// <summary>OURS: Abyss Points for a kill in the enemy's field (only Korean sources describe rift PvP points).</summary>
    public int KillPoints { get; init; } = 100;

    public long OpenMs => OpenMinutes * 60_000L;
    public long StayMs => StayMinutes * 60_000L;
    public long PvpCooldownMs => PvpCooldownMinutes * 60_000L;

    internal void Validate()
    {
        if (EveryHours is < 1 or > 24 || FirstHour is < 0 or > 23) throw new InvalidOperationException("Aion:Abyss:Rifts:EveryHours must be 1-24 and FirstHour 0-23.");
        if (OpenMinutes is < 1 or > 600 || StayMinutes is < 1 or > 600) throw new InvalidOperationException("Aion:Abyss:Rifts:OpenMinutes and StayMinutes must be between 1 and 600.");
        if (MaxPerPortal < 1 || PvpCooldownMinutes < 0) throw new InvalidOperationException("Aion:Abyss:Rifts:MaxPerPortal must be at least 1.");
    }
}

/// <summary>
/// Aion:Abyss:Battlefield, the Arena of Tactics (10v10 on Odyle Shard Cradle, the client's Battlefield_A_1, map 62). Global: open
/// 11:00-14:00 and 19:00-21:00, level 45, 10 minutes, push the Shard Catalyst into the enemy camp (time out: the team that pushed it
/// farther wins; extra time), 10 s respawns, equalised stats, 1,000 Abyss Points and 1 Silver Medal of Merit to join, 3,000 and 2
/// more to win, 3 of each a week (reset Wednesday). OURS: the push speed and radius, the extra time, the medal's item id (CANDIDATE),
/// and the bot fill (the 10-a-side teams are filled with bots after <see cref="FillAfterSeconds"/>).
/// </summary>
public sealed record BattlefieldOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>The Matching.dat id the client queues with (CANDIDATE: Strategy, after Single 1 and Team 2).</summary>
    public int MatchingId { get; init; } = 3;
    /// <summary>Open hours, "HH:mm-HH:mm" in the schedule clock; empty: always open.</summary>
    public string[] Windows { get; init; } = ["11:00-14:00", "19:00-21:00"];
    public int TeamSize { get; init; } = 10;
    public int MinLevel { get; init; } = 45;
    /// <summary>Dungeon.dat ids of the battlefields to pick from (10 Battlefield_A_1: the Shard Catalyst push).</summary>
    public int[] Dungeons { get; init; } = [10];
    public int PlayMinutes { get; init; } = 10;
    public int ExtraTimeMinutes { get; init; } = 2;
    public int RespawnSeconds { get; init; } = 10;
    public int JoinPoints { get; init; } = 1000;
    public int WinPoints { get; init; } = 3000;
    public int MedalItemId { get; init; } = 930100014;
    public int JoinMedals { get; init; } = 1;
    public int WinMedals { get; init; } = 2;
    /// <summary>Rewarded matches a week (0: no cap); the week starts <see cref="ResetDay"/> at <see cref="ResetHour"/>.</summary>
    public int WeeklyJoinRewards { get; init; } = 3;
    public int WeeklyWinRewards { get; init; } = 3;
    public string ResetDay { get; init; } = "Wednesday";
    public int ResetHour { get; init; } = 7;
    /// <summary>Stats are equalised to this level (skills at skill level 1, no gear defense, HP to the class's base).</summary>
    public int NormalizedLevel { get; init; } = 45;
    /// <summary>OURS: the catalyst moves this fast (cm/s) toward the camp of the team with more living players within the radius.</summary>
    public double CatalystSpeed { get; init; } = 60;
    public double CatalystRadius { get; init; } = 800;
    /// <summary>After this wait with too few players, the free places are filled with bots (when a bot source exists); else a match starts with
    /// <see cref="MinPlayers"/> or more, split evenly.</summary>
    public int FillAfterSeconds { get; init; } = 60;
    public int MinPlayers { get; init; } = 2;

    public long PlayMs => PlayMinutes * 60_000L;
    public long ExtraMs => ExtraTimeMinutes * 60_000L;
    public long RespawnMs => RespawnSeconds * 1000L;

    internal void Validate()
    {
        foreach (var window in Windows) _ = Window(window);
        AbyssOptions.Day(ResetDay, "Aion:Abyss:Battlefield:ResetDay");
        if (TeamSize is < 1 or > 50 || PlayMinutes is < 1 or > 120 || RespawnSeconds is < 0 or > 300 || ExtraTimeMinutes is < 0 or > 60)
            throw new InvalidOperationException("Aion:Abyss:Battlefield: TeamSize 1-50, PlayMinutes 1-120, RespawnSeconds 0-300, ExtraTimeMinutes 0-60.");
        if (Dungeons.Length == 0 || Dungeons.Any(d => MapEventTables.Embedded.Dungeon(d) is not { Type: "Matching" }))
            throw new InvalidOperationException("Aion:Abyss:Battlefield:Dungeons must name Matching dungeons (10, 12, 14).");
        if (CatalystSpeed <= 0 || CatalystRadius <= 0 || MinPlayers < 2 || ResetHour is < 0 or > 23) throw new InvalidOperationException("Aion:Abyss:Battlefield: CatalystSpeed, CatalystRadius > 0, MinPlayers >= 2.");
    }

    internal static (TimeSpan From, TimeSpan To) Window(string text)
    {
        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && TimeSpan.TryParseExact(parts[0], @"h\:mm", CultureInfo.InvariantCulture, out var from)
            && TimeSpan.TryParseExact(parts[1], @"h\:mm", CultureInfo.InvariantCulture, out var to)) return (from, to);
        throw new InvalidOperationException($"Aion:Abyss:Battlefield:Windows: '{text}' is not HH:mm-HH:mm.");
    }
}

/// <summary>One artifact of a siege layer: its Dimensional Core (the client's M_*_Artifact_OrcusGem spawn) and its holder.</summary>
public sealed class AbyssArtifact(int id, int layer, string region, string core, int coreNpcId, Vector3d at)
{
    public int Id { get; } = id;
    public int Layer { get; } = layer;
    /// <summary>RR (Reshanta's Root), STI (Sulfur Tree Island), WSA (Siel's Wings Archipelago).</summary>
    public string Region { get; } = region;
    public string CoreSpawn { get; } = core;
    public int CoreNpcId { get; } = coreNpcId;
    public Vector3d At { get; } = at;
    /// <summary>The holding race (1 Elyos, 2 Asmodian), 0 none.</summary>
    public byte Owner { get; set; }
    public bool ClaimedThisSiege { get; set; }
    public long ClaimedAt { get; set; }
    public string Title => Region switch { "RR" => "Reshanta's Root", "STI" => "Sulfur Tree Island", "WSA" => "Siel's Wings Archipelago", _ => Region } + (Layer == 20 ? "" : " (Middle)");
}

/// <summary>An open rift portal: in <see cref="HomeMap"/> for <see cref="Race"/>, leading into <see cref="TargetMap"/>.</summary>
public sealed class RiftPortal(int homeMap, int targetMap, byte race, int env, Vector3d at, long closesAt)
{
    public int HomeMap { get; } = homeMap;
    public int TargetMap { get; } = targetMap;
    public byte Race { get; } = race;
    public int Env { get; } = env;
    public Vector3d At { get; } = at;
    public long ClosesAt { get; } = closesAt;
    public int Entered { get; set; }
}

/// <summary>A player on the other side of a rift: sent back to <see cref="Back"/> at <see cref="Until"/> or at death.</summary>
public sealed record RiftVisit(int HomeMap, Vector3d Back, long Until);

/// <summary>
/// The Abyss PvP state of the host (docs/ABYSS_PVP.md): the artifact sieges, the rift portals and the battlefield queue. It runs
/// on the world: <see cref="Tick"/> under the world lock held exclusively (the host's AbyssWarService, once a second; checks call it
/// directly). Work on a player (a teleport, a reward) is posted to that player's own loop.
/// </summary>
public sealed class AbyssWar
{
    readonly GameData data;
    readonly SharedWorld world;
    public AbyssOptions Options { get; }
    public AbyssBotOptions BotOptions { get; init; } = new();
    public IReadOnlyList<AbyssArtifact> Artifacts { get; }
    /// <summary>Executors per layer: the AbBoss spawn names.</summary>
    readonly Dictionary<int, List<string>> executors = [];

    /// <summary>The bots that fill PvP sides (the open-world bots); null: matches start with the players there are.</summary>
    public IPvpBotSource? Bots { get; set; }

    public AbyssWar(GameData data, AbyssOptions options, SharedWorld world)
    {
        this.data = data;
        this.world = world;
        Options = options;
        var artifacts = new List<AbyssArtifact>();
        foreach (var layer in options.Siege.Layers)
        {
            var targets = Combat.MapTargets(data, layer);
            foreach (var t in targets.Values.Where(t => t.Name.Contains("_Artifact_OrcusGem_", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(t => RegionOf(t.Name) switch { "RR" => 0, "STI" => 1, _ => 2 }))
                artifacts.Add(new AbyssArtifact(artifacts.Count + 1, layer, RegionOf(t.Name), t.Name, t.NpcId, t.Position));
            executors[layer] = targets.Values.Where(t => t.Name.Contains("_AbBoss_", StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).ToList();
        }
        Artifacts = artifacts;
    }

    static string RegionOf(string spawn) =>
        spawn.Contains("_RR", StringComparison.OrdinalIgnoreCase) ? "RR" : spawn.Contains("_STI", StringComparison.OrdinalIgnoreCase) ? "STI" : "WSA";

    // ---------- The schedule clock ----------

    DateTime Local(long now) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(now), Options.Zone).DateTime;

    /// <summary>Once a second under the world lock (exclusive).</summary>
    public void Tick(long now)
    {
        if (!Options.Enabled) return;
        var local = Local(now);
        SiegeTick(now, local);
        RiftTick(now, local);
        BattlefieldTick(now, local);
        if (Bots is { } bots && now >= botsCheckAt)
        {
            botsCheckAt = now + BotsCheckMs;
            BotsTick(now);
            bots.Tick(now);
        }
    }

    long botsCheckAt;
    /// <summary>How often the siege and rift bot numbers are topped up (bots spawned on one call are ready on a later one).</summary>
    public const long BotsCheckMs = 5000;

    /// <summary>
    /// Bots for the siege (SiegePerFaction a race, sent to the Lower layer's start point to fight over the cores) and the rifts
    /// (RiftPerFaction a race through each open portal, rift PvP on); the ones whose job is over go back to the pool.
    /// </summary>
    void BotsTick(long now)
    {
        var bots = Bots!;
        foreach (var done in bots.OnJob(PvpJob.Siege).Where(s => !SiegeRunning || !s.InWorld)) bots.Release(done);
        foreach (var done in bots.OnJob(PvpJob.Rift).Where(s => !visits.ContainsKey(s))) bots.Release(done);
        if (!BotOptions.Enabled) return;
        foreach (var race in new[] { 1, 2 })
        {
            if (SiegeRunning && Options.Siege.Layers.Length > 0 && BotOptions.SiegePerFaction > 0)
            {
                var have = bots.OnJob(PvpJob.Siege).Count(s => s.Race == race);
                var layer = Options.Siege.Layers[0];
                var start = data.MapPoints(layer).FirstOrDefault(p => p.Key.Contains(race == 2 ? "_Dark_StartPoint" : "_Light_StartPoint", StringComparison.OrdinalIgnoreCase)).Value;
                if (have < BotOptions.SiegePerFaction && start is { Length: >= 3 })
                    foreach (var s in bots.Take(new PvpNeed("siege", race, BotOptions.SiegePerFaction - have, 45), now))
                    {
                        s.PvpJob = PvpJob.Siege;
                        var at = new Vector3d(start[0] + Random.Shared.Next(-400, 400), start[1] + Random.Shared.Next(-400, 400), start[2]);
                        _ = s.InvokeAsync(t => (true, s.Teleport(layer, at, t)));
                    }
            }
            if (portals.FirstOrDefault(p => p.Race == race) is { } portal && BotOptions.RiftPerFaction > 0)
            {
                var have = bots.OnJob(PvpJob.Rift).Count(s => s.Race == race);
                if (have < BotOptions.RiftPerFaction)
                    foreach (var s in bots.Take(new PvpNeed("rift", race, BotOptions.RiftPerFaction - have, Options.Rifts.MinLevel), now))
                    {
                        var (arrival, refusal) = EnterRift(s, portal, portal.At, now);
                        if (refusal is not null) { bots.Release(s); continue; }
                        s.PvpJob = PvpJob.Rift;
                        s.RiftBotArrived(arrival);
                        _ = s.InvokeAsync(t => (true, s.Teleport(portal.TargetMap, arrival, t)));
                    }
            }
        }
    }

    static void Tell(WorldPlayer p, string text) => p.Send(GameSession.Notice(text));

    void TellAll(string text, Func<WorldPlayer, bool>? who = null)
    {
        var notice = GameSession.Notice(text);
        foreach (var p in world.Online.Values)
            if (who?.Invoke(p) != false) p.Send(notice);
    }

    static string RaceName(int race) => race == 2 ? "Asmodians" : race == 1 ? "Elyos" : "nobody";

    // ---------- Artifact siege ----------

    public bool SiegeRunning { get; private set; }
    public long SiegeStartedAt { get; private set; }
    public long SiegeEndsAt { get; private set; }
    bool executorsCalled;
    string lastSiegeSlot = "";

    void SiegeTick(long now, DateTime local)
    {
        var o = Options.Siege;
        if (!SiegeRunning && o.Enabled && Artifacts.Count > 0 && o.Days.Any(d => AbyssOptions.Day(d, "") == local.DayOfWeek))
        {
            var start = local.Date.AddHours(o.Hour).AddMinutes(o.Minute);
            var slot = start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (local >= start && local < start.AddMinutes(o.DurationMinutes) && slot != lastSiegeSlot)
            {
                lastSiegeSlot = slot;
                StartSiege(now, (long)(start.AddMinutes(o.DurationMinutes) - local).TotalMilliseconds);
            }
        }
        if (SiegeRunning)
        {
            if (!executorsCalled && now >= SiegeStartedAt + o.ExecutorsMs)
            {
                executorsCalled = true;
                CallExecutors(now);
            }
            if (now >= SiegeEndsAt || Artifacts.All(a => a.ClaimedThisSiege)) EndSiege(now);
        }
        ApplyCores(now);
    }

    /// <summary>Starts a siege now (the schedule, or //abyss siege start) for <paramref name="lengthMs"/> (default the configured length).</summary>
    public void StartSiege(long now, long? lengthMs = null)
    {
        if (SiegeRunning || Artifacts.Count == 0) return;
        SiegeRunning = true;
        SiegeStartedAt = now;
        SiegeEndsAt = now + (lengthMs ?? Options.Siege.DurationMs);
        executorsCalled = Options.Siege.ExecutorsMs <= 0;
        foreach (var a in Artifacts) a.ClaimedThisSiege = false;
        var start = AbyssPackets.EventStart(1, SiegeEndsAt);
        foreach (var p in world.Online.Values) p.Send([start, AbyssPackets.Status(1, SiegeEndsAt, Artifacts)]);
        TellAll("The Artifact Siege has begun in Reshanta: the Dimensional Cores are open. The faction that breaks a core claims its artifact.");
    }

    public void EndSiege(long now)
    {
        if (!SiegeRunning) return;
        SiegeRunning = false;
        var update = AbyssPackets.EventUpdate(EventInfo(now));
        foreach (var p in world.Online.Values)
        {
            var race = p.Session.Race;
            var held = Artifacts.Count(a => a.Owner == race);
            p.Send([update, AbyssPackets.EventEnd(1, held, Bonus(race) is var b ? (int)Math.Round(b * 100) : 0)]);
        }
        var light = Artifacts.Count(a => a.Owner == 1);
        var dark = Artifacts.Count(a => a.Owner == 2);
        TellAll($"The Artifact Siege is over. Elyos hold {light}, Asmodians hold {dark} artifacts. Holders may enter the Abyss Corridors.");
    }

    void CallExecutors(long now)
    {
        foreach (var (layer, names) in executors)
        {
            if (world.LoadedMap(layer) is not { } map) continue;
            var back = new List<byte[]>();
            foreach (var t in map.Targets.Values.Where(t => names.Contains(t.Name) && t.Dead))
            {
                map.RespawnAt.Remove(t.Key);
                t.Hp = t.HpMax;
                t.CorpseRemoved = false;
                back.Add(GamePackets.NpcAppear(t.Appearance()));
            }
            map.BroadcastAll(back);
        }
        TellAll("The Executors have appeared in Reshanta.", p => p.Map is { } m && Options.Siege.Layers.Contains(m.MapId));
    }

    /// <summary>
    /// The cores on the loaded siege layers follow the siege: alive while it runs and their artifact is unclaimed this siege, gone
    /// otherwise (global: a core spawns only at the siege start).
    /// </summary>
    void ApplyCores(long now)
    {
        foreach (var layer in Options.Siege.Layers)
        {
            if (world.LoadedMap(layer) is not { } map || map.Instance != 0) continue;
            var changes = new List<byte[]>();
            foreach (var a in Artifacts.Where(a => a.Layer == layer))
                foreach (var core in map.Targets.Values.Where(t => t.Name == a.CoreSpawn))
                {
                    var open = SiegeRunning && !a.ClaimedThisSiege;
                    if (open && core.Dead)
                    {
                        map.RespawnAt.Remove(core.Key);
                        core.Hp = core.HpMax;
                        core.CorpseRemoved = false;
                        changes.Add(GamePackets.NpcAppear(core.Appearance()));
                    }
                    else if (!open && !core.Dead)
                    {
                        core.Hp = 0;
                        core.CorpseRemoved = true;
                        changes.Add(GamePackets.Death(core.Key));
                    }
                    if (!open)
                    {
                        map.RespawnAt.Remove(core.Key);
                        core.CorpseRemoved = true;
                    }
                }
            map.BroadcastAll(changes);
        }
    }

    /// <summary>The artifact whose core this NPC is (on its layer), or null.</summary>
    public AbyssArtifact? CoreOf(int mapId, Target target) =>
        Artifacts.FirstOrDefault(a => a.Layer == mapId && a.CoreSpawn == target.Name);

    /// <summary>
    /// A core broke (the killing player's race claims the artifact): Abyss Points to that race's contributors (its hate table, most
    /// damage first, up to RewardedPlayers), the holders' notices. Call under the world lock (the killer's cast is world work).
    /// </summary>
    public List<WorldPlayer> CoreBroken(MapInstance map, Target core, byte race, long now)
    {
        if (!SiegeRunning || CoreOf(map.MapId, core) is not { ClaimedThisSiege: false } artifact) return [];
        artifact.Owner = race;
        artifact.ClaimedThisSiege = true;
        artifact.ClaimedAt = now;
        var hate = map.Ai.Brains.TryGetValue(core.Key, out var brain) ? brain.Hate : new Dictionary<int, long>();
        var rewarded = hate.OrderByDescending(h => h.Value).Select(h => map.Players.GetValueOrDefault(h.Key))
            .OfType<WorldPlayer>().Where(p => p.Session.Race == race).Take(Options.Siege.RewardedPlayers).ToList();
        foreach (var p in rewarded) p.Session.PostReward([(GameSession.AbyssPointItemId, Options.Siege.CapturePoints)]);
        var update = AbyssPackets.Update(artifact, SiegeEndsAt);
        foreach (var p in world.Online.Values) p.Send(update);
        TellAll($"The {RaceName(race)} have claimed the artifact of {artifact.Title}.");
        return rewarded;
    }

    /// <summary>The faction's Abyss Point bonus from the artifacts it holds (a fraction: 0.075 for three at 2.5%).</summary>
    public double Bonus(int race) => race is 1 or 2 ? Math.Min(3, Artifacts.Count(a => a.Owner == race)) * Options.Siege.BonusPercentPerArtifact / 100.0 : 0;

    /// <summary>The corridor behind an AbyssArtifact portal: its artifact (the core standing at the portal), or null.</summary>
    public AbyssArtifact? CorridorArtifact(int mapId, Vector3d portalAt) =>
        Artifacts.Where(a => a.Layer == mapId).MinBy(a => (a.At.X - portalAt.X) * (a.At.X - portalAt.X) + (a.At.Y - portalAt.Y) * (a.At.Y - portalAt.Y));

    /// <summary>Why the race may not enter the artifact's corridor now (null: it may).</summary>
    public string? CorridorRefusal(AbyssArtifact artifact, int race) =>
        SiegeRunning && !artifact.ClaimedThisSiege ? "The Abyss Corridor opens once the siege for this artifact is decided."
        : artifact.Owner != race ? $"Only the faction holding the artifact of {artifact.Title} may enter its Abyss Corridor."
        : null;

    public AbyssPackets.EventInfoRow EventInfo(long now)
    {
        var light = Artifacts.Count(a => a.Owner == 1);
        var dark = Artifacts.Count(a => a.Owner == 2);
        return new AbyssPackets.EventInfoRow(1, (byte)(SiegeRunning ? 2 : Artifacts.Any(a => a.Owner != 0) ? 3 : 0), (byte)(light > dark ? 1 : dark > light ? 2 : 0),
            SiegeStartedAt, SiegeRunning ? SiegeEndsAt : NextSiege(now));
    }

    /// <summary>The next scheduled siege start (unix ms), 0 when none is scheduled.</summary>
    public long NextSiege(long now)
    {
        var o = Options.Siege;
        if (!o.Enabled || o.Days.Length == 0) return 0;
        var local = Local(now);
        for (var d = 0; d <= 7; d++)
        {
            var day = local.Date.AddDays(d);
            if (!o.Days.Any(n => AbyssOptions.Day(n, "") == day.DayOfWeek)) continue;
            var start = day.AddHours(o.Hour).AddMinutes(o.Minute);
            if (start > local) return new DateTimeOffset(start, Options.Zone.GetUtcOffset(start)).ToUnixTimeMilliseconds();
        }
        return 0;
    }

    // ---------- Spacetime Rifts ----------

    /// <summary>The home fields with rift portals: Verteron (Elyos, into Altgard) and Altgard (Asmodians, into Verteron).</summary>
    public static readonly IReadOnlyList<(int Home, int Target, byte Race, int Env)> RiftRoutes = [(1010, 1110, 1, 1100007), (1110, 1010, 2, 1100006)];

    public IReadOnlyList<RiftPortal> Portals => portals;
    readonly List<RiftPortal> portals = [];
    readonly Dictionary<GameSession, RiftVisit> visits = [];
    public IReadOnlyDictionary<GameSession, RiftVisit> Visits => visits;
    string lastRiftSlot = "";

    /// <summary>The rift portal spots of a home field (the client's rift portal objects there).</summary>
    public static IReadOnlyList<(int Index, Vector3d At)> RiftSpots(int map, int env) =>
        WorldObjects.ObjectsOn(map).Select((o, i) => (o, i)).Where(x => x.o.Env == env).Select(x => (x.i, x.o.At)).ToList();

    void RiftTick(long now, DateTime local)
    {
        var o = Options.Rifts;
        if (o.Enabled && (local.Hour - o.FirstHour + 24) % o.EveryHours == 0 && local.Minute < o.OpenMinutes)
        {
            var slot = local.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture);
            if (slot != lastRiftSlot)
            {
                lastRiftSlot = slot;
                OpenRifts(now, o.OpenMs - (local.Minute * 60_000L + local.Second * 1000L));
            }
        }
        portals.RemoveAll(p => now >= p.ClosesAt);
        foreach (var (session, visit) in visits.Where(v => now >= v.Value.Until).ToList())
        {
            visits.Remove(session);
            if (!session.InWorld) continue;
            session.WorldPlayer?.Send(GameSession.Notice("The rift's power fades: you are pulled back home."));
            _ = session.InvokeAsync(at => (true, session.InWorld ? session.Teleport(visit.HomeMap, visit.Back, at) : []));
        }
        foreach (var gone in visits.Keys.Where(s => !s.InWorld).ToList()) visits.Remove(gone);
    }

    /// <summary>Opens one portal per home field at a random spot for <paramref name="openMs"/> (the schedule, or //abyss rift open).</summary>
    public void OpenRifts(long now, long? openMs = null)
    {
        var rng = Random.Shared;
        foreach (var (home, target, race, env) in RiftRoutes)
        {
            var spots = RiftSpots(home, env);
            if (spots.Count == 0 || portals.Any(p => p.HomeMap == home)) continue;
            var spot = spots[rng.Next(spots.Count)];
            portals.Add(new RiftPortal(home, target, race, env, spot.At, now + Math.Max(60_000, openMs ?? Options.Rifts.OpenMs)));
            TellAll($"A Spacetime Rift has opened in {(home == 1010 ? "Verteron" : "Altgard")} near ({spot.At.X / 100:0}, {spot.At.Y / 100:0}). It leads into {(target == 1010 ? "Verteron" : "Altgard")}.",
                p => p.Session.Race == race);
        }
    }

    /// <summary>The open portal at this object (a rift portal object of the map at the portal's spot), or null.</summary>
    public RiftPortal? PortalAt(int map, int env, Vector3d at) =>
        portals.FirstOrDefault(p => p.HomeMap == map && p.Env == env && Math.Abs(p.At.X - at.X) < 1 && Math.Abs(p.At.Y - at.Y) < 1);

    /// <summary>Whether a rift portal object is hidden now (rift portal objects show only while their spot's portal is open).</summary>
    public bool RiftObjectHidden(int map, WorldObject o) =>
        RiftRoutes.Any(r => r.Home == map && r.Env == o.Env) && PortalAt(map, o.Env, o.At) is null;

    /// <summary>
    /// A player uses an open portal: the level, race and head count rules, then where they arrive (one of the enemy field's rift
    /// spots) and the visit (StayMinutes). Null and a reason when refused.
    /// </summary>
    public (Vector3d Arrival, string? Refusal) EnterRift(GameSession session, RiftPortal portal, Vector3d back, long now)
    {
        if (session.Race != portal.Race) return (default, "This rift does not answer to you.");
        if (session.Level < Options.Rifts.MinLevel) return (default, $"Rifts need level {Options.Rifts.MinLevel}.");
        if (portal.Entered >= Options.Rifts.MaxPerPortal) return (default, "The rift is full.");
        var route = RiftRoutes.First(r => r.Home == portal.HomeMap);
        var spots = RiftSpots(portal.TargetMap, RiftRoutes.First(r => r.Home == portal.TargetMap).Env);
        var arrival = spots.Count > 0 ? spots[Random.Shared.Next(spots.Count)].At : data.MapStart(portal.TargetMap)?.Position ?? back;
        portal.Entered++;
        visits[session] = new RiftVisit(route.Home, back, now + Options.Rifts.StayMs);
        return (arrival with { X = arrival.X + 300 }, null);
    }

    /// <summary>A visitor died, or left: the visit ends; the place to return to (the visitor revives at home).</summary>
    public RiftVisit? EndVisit(GameSession session) => visits.Remove(session, out var visit) ? visit : null;

    public bool Visiting(GameSession session) => visits.ContainsKey(session);

    // ---------- Battlefield ----------

    /// <summary>Whether the battlefield queue is open now (its windows in the schedule clock).</summary>
    public bool BattlefieldOpen(long now)
    {
        var o = Options.Battlefield;
        if (!o.Enabled) return false;
        if (o.Windows.Length == 0) return true;
        var time = Local(now).TimeOfDay;
        return o.Windows.Select(BattlefieldOptions.Window).Any(w => w.From <= w.To ? time >= w.From && time < w.To : time >= w.From || time < w.To);
    }

    /// <summary>The weekly reward counts by character (in memory: a restart forgets them, OURS).</summary>
    readonly Dictionary<(string Character, string Week), (int Joins, int Wins)> weekly = [];

    string Week(long now)
    {
        var o = Options.Battlefield;
        var local = Local(now).AddHours(-o.ResetHour);
        var back = ((int)local.DayOfWeek - (int)AbyssOptions.Day(o.ResetDay, "") + 7) % 7;
        return local.Date.AddDays(-back).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>The battlefield rewards of one player for a match, within the weekly caps.</summary>
    public List<(int ItemId, long Count)> BattlefieldRewards(GameSession session, bool won, long now)
    {
        var o = Options.Battlefield;
        var key = (session.CharacterId?.ToString() ?? session.CharacterName ?? session.PlayerKey.ToString(CultureInfo.InvariantCulture), Week(now));
        var (joins, wins) = weekly.GetValueOrDefault(key);
        var items = new List<(int ItemId, long Count)>();
        if (o.WeeklyJoinRewards == 0 || joins < o.WeeklyJoinRewards)
        {
            joins++;
            items.Add((GameSession.AbyssPointItemId, o.JoinPoints));
            if (o.JoinMedals > 0) items.Add((o.MedalItemId, o.JoinMedals));
        }
        if (won && (o.WeeklyWinRewards == 0 || wins < o.WeeklyWinRewards))
        {
            wins++;
            items.Add((GameSession.AbyssPointItemId, o.WinPoints));
            if (o.WinMedals > 0) items.Add((o.MedalItemId, o.WinMedals));
        }
        weekly[key] = (joins, wins);
        return items.Where(i => i.Count > 0).ToList();
    }

    /// <summary>OURS: how long past FillAfterSeconds a battlefield waits for its filler bots to finish logging in.</summary>
    public const int BotWaitSeconds = 30;

    void BattlefieldTick(long now, DateTime local)
    {
        if (world.Social.ArenaQueue.Count == 0) return;
        var o = Options.Battlefield;
        var waiting = world.Social.ArenaQueue.Where(q => q.MatchingId == o.MatchingId).OrderBy(q => q.SinceMs).ToList();
        if (waiting.Count == 0) return;
        if (!BattlefieldOpen(now))
        {
            foreach (var q in waiting)
            {
                world.Social.ArenaQueue.Remove(q);
                Tell(q.Player, "The battlefield has closed for now.");
                q.Player.Send(GameSession.MatchingStateFor(o.MatchingId, 0));
            }
            return;
        }
        var full = o.TeamSize * 2;
        List<WorldPlayer> players;
        if (waiting.Count >= full) players = waiting.Take(full).Select(q => q.Player).ToList();
        else if (now - waiting[0].SinceMs < o.FillAfterSeconds * 1000L) return;
        else
        {
            players = waiting.Select(q => q.Player).ToList();
            if (Bots is { } source)
            {
                // Bots spawned for the need log in over the next seconds: wait for them (up to BotWaitSeconds more), handing back the
                // ones already taken, so a match does not start one against one while the rest are still loading.
                var got = source.Take(new PvpNeed("battlefield", 0, full - players.Count, o.MinLevel), now);
                var usable = got.Where(b => b.WorldPlayer is { } bp && !players.Contains(bp)).ToList();
                foreach (var b in got.Except(usable)) source.Release(b);
                if (players.Count + usable.Count < full && now - waiting[0].SinceMs < (o.FillAfterSeconds + BotWaitSeconds) * 1000L)
                {
                    foreach (var b in usable) source.Release(b);
                    return;
                }
                players.AddRange(usable.Select(b => b.WorldPlayer!));
            }
            if (players.Count < o.MinPlayers) return;
        }
        foreach (var q in waiting.Where(q => players.Contains(q.Player))) world.Social.ArenaQueue.Remove(q);
        GameSession.OpenBattlefield(world, players, o, now);
    }
}

/// <summary>Where bots for organised PvP come from (the open-world bot pool): leaderless bots already in the world.</summary>
public interface IPvpBotSource
{
    /// <summary>Up to <paramref name="need"/>.Count idle bots for a PvP content (they get a <see cref="PvpGoal"/> from it), now in the world.</summary>
    IReadOnlyList<GameSession> Take(PvpNeed need, long now);
    /// <summary>A bot's PvP job is over (the match ended, the siege ended): back to its own life.</summary>
    void Release(GameSession bot);
    /// <summary>The bots busy with a siege or rift job now.</summary>
    IReadOnlyList<GameSession> OnJob(PvpJob job);
    /// <summary>Once a second with the world tick (idle bots leave).</summary>
    void Tick(long now);
}

/// <summary>A request for PvP bots: the content ("battlefield", "siege", "rift"), the race (0 any), how many, the lowest level.</summary>
public sealed record PvpNeed(string Content, int Race, int Count, int MinLevel);

/// <summary>The Abyss PvP messages (usmap DevPacketData_GameServer_AbyssArtifact*, opcodes CANDIDATE: see GameSession.AbyssWar.cs).</summary>
public static class AbyssPackets
{
    public const ushort EventStartNt = 0xe300, EventEndNt = 0xe301, StatusNt = 0xe302, UpdateNt = 0xe303, ProgressInfoNt = 0xe304,
        EventInfoNt = 0xe305, EventInfoRq = 0xe306, EventInfoRs = 0xe307, EventUpdateNt = 0xe308, TeleportRq = 0xe309, TeleportRs = 0xe30a;
    /// <summary>RiftOptionUpdate_RQ/RS/CharUpdate_NT: the 0x36 block, catalogue index + 0x360c, as EnvObjSpawn 0x3634 (CANDIDATE strong).</summary>
    public const ushort RiftOptionRq = 0x3652, RiftOptionRs = 0x3653, RiftOptionNt = 0x3654;

    public sealed record EventInfoRow(int EventId, byte State, byte Winner, long RewardBaseTime, long EndTime);

    static PacketWriter Artifact(PacketWriter w, AbyssArtifact a, long endTime) =>
        w.I32(a.Id).I32(0).U8(a.Owner).I64(a.Owner != 0 ? endTime : 0);

    static PacketWriter Event(PacketWriter w, EventInfoRow e) => w.I32(e.EventId).U8(e.State).U8(e.Winner).I64(e.RewardBaseTime).I64(e.EndTime);

    public static byte[] EventStart(int eventId, long endTime) => PacketWriter.Opcode(EventStartNt).I32(eventId).I64(endTime).ToArray();
    public static byte[] EventEnd(int eventId, int occupied, int point) => PacketWriter.Opcode(EventEndNt).I32(eventId).I32(occupied).I32(point).ToArray();

    public static byte[] Status(int eventId, long endTime, IReadOnlyList<AbyssArtifact> artifacts)
    {
        var w = PacketWriter.Opcode(StatusNt).I32(eventId).I64(endTime).Varint((uint)artifacts.Count);
        foreach (var a in artifacts) Artifact(w, a, endTime);
        return w.ToArray();
    }

    public static byte[] Update(AbyssArtifact a, long endTime) => Artifact(PacketWriter.Opcode(UpdateNt), a, endTime).ToArray();
    public static byte[] EventUpdate(EventInfoRow e) => Event(PacketWriter.Opcode(EventUpdateNt), e).ToArray();

    public static byte[] EventInfo(EventInfoRow e, IReadOnlyList<AbyssArtifact> artifacts, long endTime)
    {
        var w = PacketWriter.Opcode(EventInfoRs).U16(0).Varint(1);
        Event(w, e).Varint((uint)artifacts.Count);
        foreach (var a in artifacts) Artifact(w, a, endTime);
        return w.ToArray();
    }

    public static byte[] RiftOption(bool on, long setTime, byte reason, int result = 0) =>
        PacketWriter.Opcode(RiftOptionRs).U16(result).Bool(on).I64(setTime).U8(reason).ToArray();

    public static byte[] RiftOptionChar(int key, bool on) => PacketWriter.Opcode(RiftOptionNt).Varint(key).Bool(on).ToArray();
}

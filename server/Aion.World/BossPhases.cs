using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

// Boss phases and scripted waves (tools/data/boss-phases.json, OURS: written by tools/boss_phases.py from the client's own
// NPC data). The client data has no boss AI tables (its MapEvent scripts only spawn, talk and open doors), so the phases are
// hand-made per encounter role and mode. What is the client's own: the bosses, their skills and cast times (the long-cast
// specials such as KraoCave_Boss_1's 8 s skill 1800140 become the telegraphed slams), the adds (each dungeon's own
// monsters), and the hard modes' 4-minute frenzy (global's Expedition hard-mode rule since 2026-10-21).

/// <summary>A telegraphed area slam: a long wind-up (the skill's cast, or <see cref="WindUpMs"/>), then a hit on every player inside the radius.</summary>
public sealed record BossTelegraph(
    [property: JsonPropertyName("skill")] int Skill,
    [property: JsonPropertyName("every_ms")] int EveryMs,
    [property: JsonPropertyName("wind_up_ms")] int WindUpMs,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("damage")] double Damage = 1.0,
    [property: JsonPropertyName("warning")] string? Warning = null);

/// <summary>Adds summoned around the boss: copies of a monster of the same dungeon, sent at the boss's victim.</summary>
public sealed record BossAdds(
    [property: JsonPropertyName("npc")] int Npc,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("radius")] double Radius = 500);

/// <summary>A phase: from <see cref="At"/> percent HP down, the boss hits harder, attacks faster, calls adds, changes its slam.</summary>
public sealed record BossPhase(
    [property: JsonPropertyName("at")] int At,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("damage")] double Damage = 1.0,
    [property: JsonPropertyName("haste")] double Haste = 1.0,
    [property: JsonPropertyName("adds")] BossAdds? Adds = null,
    [property: JsonPropertyName("telegraph")] BossTelegraph? Telegraph = null,
    [property: JsonPropertyName("notice")] string? Notice = null);

/// <summary>The frenzy: this long into the fight the boss's hits and attack speed go up for good (global hard modes: 4 minutes).</summary>
public sealed record BossFrenzy(
    [property: JsonPropertyName("after_ms")] int AfterMs,
    [property: JsonPropertyName("damage")] double Damage,
    [property: JsonPropertyName("haste")] double Haste,
    [property: JsonPropertyName("notice")] string? Notice = null);

/// <summary>One boss's script: its slam from the pull, its phases (highest first) and its frenzy.</summary>
public sealed record BossScript(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dungeon")] string Dungeon,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("telegraph")] BossTelegraph? Telegraph = null,
    [property: JsonPropertyName("phases")] BossPhase[]? Phases = null,
    [property: JsonPropertyName("frenzy")] BossFrenzy? Frenzy = null)
{
    public IReadOnlyList<BossPhase> Ordered { get; } = (Phases ?? []).OrderByDescending(p => p.At).ToList();

    /// <summary>Every slam skill of the script (the pull's and the phases'): the boss does not also use them as ordinary specials.</summary>
    public IReadOnlySet<int> SlamSkills { get; } = new[] { Telegraph }.Concat((Phases ?? []).Select(p => p.Telegraph))
        .Where(t => t is not null).Select(t => t!.Skill).ToHashSet();
}

/// <summary>
/// A scripted wave the spawn data does not name (OURS): a MapEvent StartWave whose spawn entries are not called
/// "{wave}_{n}" (Fierce Horn Den's totem wave). Copies of <see cref="Npc"/> come around <see cref="Around"/> every
/// <see cref="EveryMs"/> while it lives, at most <see cref="Pulses"/> times; the wave is clear when it and they are dead.
/// </summary>
public sealed record SyntheticWave(
    [property: JsonPropertyName("map")] int Map,
    [property: JsonPropertyName("around")] string Around,
    [property: JsonPropertyName("npc")] int Npc,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("every_ms")] int EveryMs,
    [property: JsonPropertyName("pulses")] int Pulses,
    [property: JsonPropertyName("radius")] double Radius = 600);

/// <summary>Aion:Bosses (OURS): switches for the boss phases; all on by default.</summary>
public sealed record BossOptions
{
    /// <summary>Phases, adds and slams (off: every boss fights as before, its specials single-target).</summary>
    public bool Phases { get; init; } = true;
    /// <summary>The hard modes' frenzy (global: 4 minutes into the boss fight).</summary>
    public bool Frenzy { get; init; } = true;
    /// <summary>Override of every script's frenzy time in seconds (0: the script's, 240 on hard modes).</summary>
    public int FrenzySeconds { get; init; }
    /// <summary>Scales every slam's and phase's damage (1.0: as scripted). Lower it for a casual group.</summary>
    public double DamageScale { get; init; } = 1.0;
    public static BossOptions Default { get; } = new();

    public BossOptions Validated()
    {
        if (FrenzySeconds is < 0 or > 3600) throw new InvalidOperationException($"Aion:Bosses:FrenzySeconds must be between 0 and 3600 (got {FrenzySeconds}).");
        if (DamageScale is < 0.1 or > 10 || double.IsNaN(DamageScale)) throw new InvalidOperationException($"Aion:Bosses:DamageScale must be between 0.1 and 10 (got {DamageScale}).");
        return this;
    }

    public override string ToString() => !Phases ? "boss phases off"
        : $"boss phases on, frenzy {(Frenzy ? FrenzySeconds > 0 ? $"after {FrenzySeconds} s" : "as scripted" : "off")}, damage x{DamageScale.ToString("0.##", CultureInfo.InvariantCulture)}";
}

/// <summary>The embedded boss-phases.json: scripts by NpcData id and the synthetic waves by wave name.</summary>
public sealed class BossPhases
{
    sealed record File(
        [property: JsonPropertyName("bosses")] Dictionary<string, BossScript> Bosses,
        [property: JsonPropertyName("waves")] Dictionary<string, SyntheticWave>? Waves = null);

    readonly Dictionary<int, BossScript> bosses;
    readonly Dictionary<(int Map, string Wave), SyntheticWave> waves;

    public BossPhases(IReadOnlyDictionary<int, BossScript> bosses, IReadOnlyDictionary<string, SyntheticWave>? waves = null)
    {
        this.bosses = new(bosses);
        this.waves = (waves ?? new Dictionary<string, SyntheticWave>())
            .ToDictionary(p => (p.Value.Map, p.Key.Split('@')[0]), p => p.Value, new WaveKeyComparer());
    }

    sealed class WaveKeyComparer : IEqualityComparer<(int Map, string Wave)>
    {
        public bool Equals((int Map, string Wave) a, (int Map, string Wave) b) => a.Map == b.Map && string.Equals(a.Wave, b.Wave, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((int Map, string Wave) k) => HashCode.Combine(k.Map, StringComparer.OrdinalIgnoreCase.GetHashCode(k.Wave));
    }

    public static BossPhases Parse(string json)
    {
        var file = JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty boss-phases.json");
        return new(file.Bosses.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value), file.Waves);
    }

    static readonly Lazy<BossPhases> embedded = new(() =>
    {
        using var stream = typeof(BossPhases).Assembly.GetManifestResourceStream("boss-phases.json")
            ?? throw new InvalidOperationException("Missing embedded game data boss-phases.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static BossPhases Embedded => embedded.Value;
    public static BossPhases None { get; } = new(new Dictionary<int, BossScript>());

    public int Count => bosses.Count;
    public IEnumerable<(int NpcId, BossScript Script)> All => bosses.Select(p => (p.Key, p.Value));
    public BossScript? Of(int npcId) => bosses.GetValueOrDefault(npcId);
    /// <summary>The hand-made wave of this map's StartWave name (keys "{wave}@{map}" in the file), or null.</summary>
    public SyntheticWave? Wave(int map, string wave) => waves.GetValueOrDefault((map, wave));
    public int WaveCount => waves.Count;
}

/// <summary>A boss's running fight: phases reached, current multipliers, the next slam, the adds it called.</summary>
public sealed class BossFight(BossScript script, long startedMs)
{
    public BossScript Script { get; } = script;
    public long StartedMs { get; } = startedMs;
    /// <summary>Phases applied so far (of <see cref="BossScript.Ordered"/>, highest first).</summary>
    public int Reached { get; set; }
    public double Damage { get; set; } = 1.0;
    public double Haste { get; set; } = 1.0;
    public bool Frenzied { get; set; }
    public BossTelegraph? Telegraph { get; set; } = script.Telegraph;
    public long NextSlamMs { get; set; }
    /// <summary>The slam being wound up: its skill, the time it lands, and the action sequence of its skill start.</summary>
    public (BossTelegraph Slam, long DueMs, int Seq)? Winding { get; set; }
    public List<int> Adds { get; } = [];
    public int Slams { get; set; }
}

/// <summary>Monsters summoned during a fight (boss adds, scripted waves): new targets on the map, never respawned.</summary>
public static class Summons
{
    /// <summary>Keys of summoned monsters (clear of map NPCs 2000+, staff spawns 600000+, players 100000+ and Kibelisks 900000+).</summary>
    public const int FirstKey = 700_000, LastKey = 899_999;
    public static bool IsSummon(int key) => key is >= FirstKey and <= LastKey;
    /// <summary>Appended to a summon's name so map-event spawners (a wave's "{wave}_{n}", KillSpawner) never count or respawn it.</summary>
    public const string NameSuffix = "_Summon";

    /// <summary>
    /// Copies of the NPC (its template, the name of a placed spawn of it) in a ring around a point; their targets, already in the
    /// map's table. The caller sends their appears and sets them on a victim.
    /// </summary>
    public static List<Target> Spawn(GameData data, Dictionary<int, Target> targets, int mapId, int npcId, Vector3d centre, int count, double radius)
    {
        var placed = data.NpcPlaced(npcId, mapId);
        if (placed is null || count <= 0) return [];
        var made = new List<Target>();
        var next = Math.Max(FirstKey, targets.Keys.Where(IsSummon).DefaultIfEmpty(FirstKey - 1).Max() + 1);
        for (var i = 0; i < count && next <= LastKey; i++, next++)
        {
            var angle = i * 2 * Math.PI / Math.Max(1, count);
            var hp = Combat.HpFor(placed);
            var t = new Target
            {
                Key = next, Name = placed.Name + NameSuffix, Hp = hp, HpMax = hp,
                Position = centre with { X = centre.X + Math.Cos(angle) * radius, Y = centre.Y + Math.Sin(angle) * radius },
                NpcId = placed.NpcId, Level = placed.Level, Direction = (angle * 180 / Math.PI + 180) % 360, Relationship = placed.Relationship,
                Aggressive = true, WalkSpeed = placed.WalkSpeed, RunSpeed = placed.RunSpeed, Radius = placed.Radius, Function = placed.Function,
                Aggro = placed.Aggro, Skills = placed.Skills ?? [], NoRotation = placed.NoRotation, Air = placed.Air,
                AirMonster = placed.Air && placed.SubType.EndsWith("Monster", StringComparison.Ordinal),
                DamageScale = Combat.DamageScale(placed.SubType, placed.Grade), SubType = placed.SubType,
            };
            targets[t.Key] = t;
            made.Add(t);
        }
        return made;
    }
}

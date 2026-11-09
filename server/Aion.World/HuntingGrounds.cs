using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// A hunting ground: a cluster of ordinary hostile monsters on an open-world map (OURS, from the map's NPC spawns). World bots
/// (docs/WORLDBOTS.md) pick one that suits their level and hunt there.
/// </summary>
/// <param name="Map">The field map.</param>
/// <param name="Race">1 Light (Elyos) or 2 Dark (Asmodian): the race whose map it is; 0 a map both races share (the Abyss).</param>
/// <param name="Centre">The spawn nearest the cluster's middle (on the ground, where a bot can stand).</param>
/// <param name="Level">The median monster level.</param>
/// <param name="MinLevel">The lowest monster level.</param>
/// <param name="MaxLevel">The highest monster level.</param>
/// <param name="Monsters">How many monster spawns the cluster has.</param>
public sealed record HuntingGround(int Map, int Race, Vector3d Centre, int Level, int MinLevel, int MaxLevel, int Monsters)
{
    /// <summary>A bot hunts within this distance of the centre (cm).</summary>
    public const double Radius = 2500;
}

/// <summary>
/// Hunting grounds from the embedded spawns: on every map that is not an instance, the ordinary hostile monsters (no elites,
/// bosses or flyers) bucketed into <see cref="CellSize"/> squares; a square with at least <see cref="MinMonsters"/> of them is a
/// ground. A map is a race's map when its friendly guards and NPCs are that race's (NPC relationship 3 Light, 4 Dark), and shared
/// when both races' NPCs are there in numbers (the Abyss). All of it is OURS.
/// </summary>
public static class HuntingGrounds
{
    public const double CellSize = 4000;
    public const int MinMonsters = 4;
    /// <summary>NPC relationships of the races' friendly NPCs in the spawn data (OBSERVED: 3 on the Light maps, 4 on the Dark ones).</summary>
    public const int LightFriendly = 3, DarkFriendly = 4;

    static readonly Lazy<IReadOnlyList<HuntingGround>> embedded = new(() => Build(GameData.Embedded));

    /// <summary>The hunting grounds of the embedded data.</summary>
    public static IReadOnlyList<HuntingGround> Embedded => embedded.Value;

    /// <summary>The race whose map this is (1 or 2), 0 for a map both races share or one without race NPCs.</summary>
    public static int RaceOf(IReadOnlyList<NpcSpawn> spawns)
    {
        var light = spawns.Count(s => s.Relationship == LightFriendly);
        var dark = spawns.Count(s => s.Relationship == DarkFriendly);
        return light > 10 * Math.Max(1, dark) ? 1 : dark > 10 * Math.Max(1, light) ? 2 : 0;
    }

    public static IReadOnlyList<HuntingGround> Build(GameData data)
    {
        var found = new List<HuntingGround>();
        foreach (var map in data.SpawnMaps.Order())
        {
            if (InstanceTables.Embedded.Of(map) is not null || MapEventTables.Embedded.DungeonOfMap(map) is { Type: not "Abyss" }) continue;
            var spawns = data.Spawns(map);
            var race = RaceOf(spawns);
            var cells = new Dictionary<(long, long), List<NpcSpawn>>();
            foreach (var s in spawns)
            {
                if (s.SubType != "NormalMonster" || s.Air || !IsHostile(s.Relationship) || s.Position.Length < 3) continue;
                var cell = ((long)Math.Floor(s.Position[0] / CellSize), (long)Math.Floor(s.Position[1] / CellSize));
                if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = [];
                list.Add(s);
            }
            foreach (var (_, list) in cells.OrderBy(c => c.Key))
            {
                if (list.Count < MinMonsters) continue;
                var (mx, my) = (list.Average(s => s.Position[0]), list.Average(s => s.Position[1]));
                var middle = list.MinBy(s => (s.Position[0] - mx) * (s.Position[0] - mx) + (s.Position[1] - my) * (s.Position[1] - my))!;
                var levels = list.Select(s => s.Level).Order().ToList();
                found.Add(new HuntingGround(map, race, new Vector3d(middle.Position[0], middle.Position[1], middle.Position[2]),
                    levels[levels.Count / 2], levels[0], levels[^1], list.Count));
            }
        }
        return found;
    }

    /// <summary>The same relationships as <see cref="Target.Hostile"/>.</summary>
    public static bool IsHostile(int relationship) => relationship is 6 or 11 or 16 or 17 or 18 or 19 or 20 or 21;

    /// <summary>
    /// The grounds a bot of this race and level may hunt: its race's maps (and shared ones when <paramref name="shared"/>), within
    /// the wanted maps when any are named, whose median level is from <paramref name="level"/> - <paramref name="below"/> to
    /// <paramref name="level"/> + <paramref name="above"/>. When none fit, the nearest levels below (then above) are used.
    /// </summary>
    public static IReadOnlyList<HuntingGround> For(IReadOnlyList<HuntingGround> all, int race, int level, bool shared, IReadOnlyCollection<int>? maps,
        int below = 3, int above = 1)
    {
        var mine = all.Where(g => (g.Race == race || shared && g.Race == 0) && (maps is not { Count: > 0 } || maps.Contains(g.Map))).ToList();
        var fit = mine.Where(g => g.Level >= level - below && g.Level <= level + above).ToList();
        if (fit.Count > 0) return fit;
        var lower = mine.Where(g => g.Level < level).ToList();
        if (lower.Count > 0) { var best = lower.Max(g => g.Level); return lower.Where(g => g.Level >= best - 1).ToList(); }
        var higher = mine.Where(g => g.Level > level).ToList();
        if (higher.Count > 0) { var best = higher.Min(g => g.Level); return higher.Where(g => g.Level <= best + 1).ToList(); }
        return [];
    }
}

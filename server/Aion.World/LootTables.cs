using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Monster loot from tools/data/loot.json (tools/loot_data.py: RewardData, RewardItem, RewardPackage,
/// RewardPackageItem). An NPC (RewardData ID = NpcData ID) has reward tables; every table entry is an
/// independent chance of 1e8 (items with a count range, packages that pick one item by weight).
/// CANDIDATE roll reading (tables often sum above 1e8). Prologue monsters (Poeta) have no tables.
/// </summary>
public sealed class LootTables
{
    sealed record Table(
        [property: JsonPropertyName("race")] int Race,
        [property: JsonPropertyName("items")] long[][] Items,
        [property: JsonPropertyName("packages")] JsonElement[][] Packages);

    sealed record LootFile(
        [property: JsonPropertyName("npcs")] Dictionary<string, string[]> Npcs,
        [property: JsonPropertyName("tables")] Dictionary<string, Table> Tables,
        [property: JsonPropertyName("packages")] Dictionary<string, long[][]> Packages);

    readonly LootFile file;
    LootTables(LootFile file) => this.file = file;

    static readonly Lazy<LootTables> embedded = new(() =>
    {
        using var stream = typeof(LootTables).Assembly.GetManifestResourceStream("loot.json")
            ?? throw new InvalidOperationException("Missing embedded game data loot.json");
        return new LootTables(JsonSerializer.Deserialize<LootFile>(stream) ?? throw new InvalidOperationException("Empty loot.json"));
    });

    public static LootTables Embedded => embedded.Value;

    public bool HasLoot(int npcId) => file.Npcs.ContainsKey(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The items one kill drops for a character of a race (1 Light, 2 Dark), merged by item. Aion:Loot (OURS, LootRules) may
    /// pass <paramref name="pick"/> (re-draws a package pick for the looter's class, duplicates) and multiply the chance of the
    /// packages that hold equipment (<paramref name="equipmentChance"/>, bad-luck protection); without them the roll is unchanged.
    /// </summary>
    public List<(int ItemId, long Count)> Roll(int npcId, int race, Random rng, Func<Func<int>, int>? pick = null,
        double equipmentChance = 1, Func<int, bool>? isEquipment = null)
    {
        var drops = new Dictionary<int, long>();
        void Add(long item, long count) { if (count > 0) drops[(int)item] = drops.GetValueOrDefault((int)item) + count; }
        long Count(long min, long max) => min >= max ? min : min + (long)(rng.NextDouble() * (max - min + 1));
        bool Hit(long chance) => rng.NextDouble() * 100_000_000 < chance;
        bool HitScaled(long chance, double scale) => scale == 1 ? Hit(chance) : rng.NextDouble() * 100_000_000 < chance * scale;
        foreach (var table in TablesOf(npcId, race))
        {
            foreach (var item in table.Items)
                if (item.Length >= 4 && Hit(item[1])) Add(item[0], Count(item[2], item[3]));
            foreach (var package in table.Packages)
            {
                if (package.Length < 3) continue;
                var found = file.Packages.TryGetValue(package[0].GetString()!, out var entries);
                var scale = found && equipmentChance != 1 && isEquipment is not null && entries!.Any(e => isEquipment((int)e[0])) ? equipmentChance : 1;
                if (!HitScaled(package[1].GetInt64(), scale) || !found || entries!.Length == 0) continue;
                for (var n = 0; n < Math.Max(1, package[2].GetInt32()); n++)
                {
                    long[]? drawn = null;
                    int Draw()
                    {
                        drawn = Weighted(entries, rng);
                        return drawn is null ? 0 : (int)drawn[0];
                    }
                    var id = pick is null ? Draw() : pick(Draw);
                    if (id == 0) continue;
                    // A swapped item (LootRules) keeps the count range of the entry it replaced.
                    var entry = drawn is not null && drawn[0] == id ? drawn : entries.FirstOrDefault(e => e[0] == id) ?? drawn;
                    if (entry is not null) Add(id, Count(entry[2], entry[3]));
                }
            }
        }
        return drops.Select(d => (d.Key, d.Value)).ToList();
    }

    IEnumerable<Table> TablesOf(int npcId, int race)
    {
        foreach (var name in file.Npcs.GetValueOrDefault(npcId.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? [])
            if (file.Tables.TryGetValue(name, out var table) && (table.Race == 0 || table.Race == race)) yield return table;
    }

    /// <summary>One entry of a package by weight (null when rounding leaves none, as the roll always did).</summary>
    static long[]? Weighted(long[][] entries, Random rng)
    {
        var pick = rng.NextDouble() * entries.Sum(e => e[1]);
        foreach (var e in entries)
            if ((pick -= e[1]) < 0) return e;
        return null;
    }

    /// <summary>Whether a kill of this NPC can drop equipment for the race (a package of its tables holds some).</summary>
    public bool CanDropEquipment(int npcId, int race, Func<int, bool> isEquipment) =>
        TablesOf(npcId, race).Any(t => t.Packages.Any(p => p.Length >= 3 && file.Packages.TryGetValue(p[0].GetString()!, out var entries)
            && entries.Any(e => isEquipment((int)e[0]))));

    /// <summary>
    /// Bad-luck protection (LootRules, OURS): one equipment item from the NPC's tables, whatever the chances: a package that holds
    /// equipment chosen by its chance, then one of its equipment entries by weight (re-drawn by <paramref name="pick"/>). 0: none.
    /// </summary>
    public int GuaranteedEquipment(int npcId, int race, Random rng, Func<int, bool> isEquipment, Func<Func<int>, int>? pick = null)
    {
        var candidates = new List<(long[][] Entries, long Chance)>();
        foreach (var table in TablesOf(npcId, race))
            foreach (var package in table.Packages)
                if (package.Length >= 3 && file.Packages.TryGetValue(package[0].GetString()!, out var entries)
                    && entries.Where(e => isEquipment((int)e[0])).ToArray() is { Length: > 0 } gear)
                    candidates.Add((gear, Math.Max(1, package[1].GetInt64())));
        if (candidates.Count == 0) return 0;
        var roll = rng.NextDouble() * candidates.Sum(c => (double)c.Chance);
        var chosen = candidates[^1].Entries;
        foreach (var (gear, chance) in candidates)
            if ((roll -= chance) < 0) { chosen = gear; break; }
        int Draw() => Weighted(chosen, rng) is { } e ? (int)e[0] : (int)chosen[^1][0];
        return pick is null ? Draw() : pick(Draw);
    }
}

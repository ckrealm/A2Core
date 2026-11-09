using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Aion.World;

/// <summary>
/// Aion:Fun (OURS, docs/friends-server/FUN.md): friends-server extras on top of the retail rules. Field bosses keep
/// retail-style long respawns with server-wide notices and loot for everyone who fought; players get the //buff and
/// //hub chat commands; duels end with both duelists back at full HP and MP.
/// </summary>
public sealed record FunOptions
{
    /// <summary>Open-world boss and named-elite respawns, notices and shared loot (off: every monster uses Aion:Rules:RespawnSeconds).</summary>
    public bool FieldBosses { get; init; } = true;
    /// <summary>Field boss respawn window in minutes: each kill picks a random time inside it (retail runs hours; OURS default 60-90).</summary>
    public int BossRespawnMinMinutes { get; init; } = 60;
    public int BossRespawnMaxMinutes { get; init; } = 90;
    /// <summary>Named elites (the other open-world HeroMonsters): their respawn window in minutes.</summary>
    public int NamedRespawnMinMinutes { get; init; } = 10;
    public int NamedRespawnMaxMinutes { get; init; } = 15;
    /// <summary>Server-wide notices when a field boss appears again and when it is defeated.</summary>
    public bool BossAnnouncements { get; init; } = true;
    /// <summary>Everyone who damaged a field boss or named elite rolls its loot, not only the killer and the killer's party.</summary>
    public bool SharedBossLoot { get; init; } = true;
    /// <summary>"//buff": the food, drink and scroll buffs below at once.</summary>
    public bool BuffCommand { get; init; } = true;
    /// <summary>How long //buff buffs last, in minutes.</summary>
    public int BuffMinutes { get; init; } = 30;
    /// <summary>Seconds between two //buff uses (0: none).</summary>
    public int BuffCooldownSeconds { get; init; }
    /// <summary>Item skills //buff applies (one buff per SkillAbnormal group; a later one in the same group replaces an earlier one).</summary>
    public int[] BuffSkills { get; init; } = [.. DefaultBuffSkills];
    /// <summary>"//hub": teleports to the race's towns and the Abyss.</summary>
    public bool HubCommand { get; init; } = true;
    /// <summary>The level //hub needs for the Abyss (PvP).</summary>
    public int HubAbyssMinLevel { get; init; } = 45;
    /// <summary>Duels end with both duelists back at the HP and MP they had when the battle started (or more).</summary>
    public bool DuelRestore { get; init; } = true;
    /// <summary>Lucky drops: a monster's equipment drop may come one grade up and/or already enchanted.</summary>
    public bool LuckyDrops { get; init; } = true;
    /// <summary>Percent chance an equipment drop becomes the next grade (Common/Rare → Epic, Epic → Unique) by monster rank.</summary>
    public double UpgradeChanceNormal { get; init; } = 2;
    public double UpgradeChanceElite { get; init; } = 8;
    public double UpgradeChanceHero { get; init; } = 25;
    /// <summary>Percent chances a looted equipment piece arrives enchanted: +1..+3, +4..+6, +7..its max.</summary>
    public double EnchantLowChance { get; init; } = 25;
    public double EnchantMidChance { get; init; } = 5;
    public double EnchantHighChance { get; init; } = 1;

    /// <summary>
    /// The best of each buff group in the item data: drink (HP regen, abnormal resistance), food (damage +16, defense +160),
    /// running scroll (+15% speed), courage scroll (+15% attack speed), damage up/down scroll, max HP +15% scroll.
    /// </summary>
    public static readonly int[] DefaultBuffSkills = [2111603, 2161003, 2210104, 2210106, 2210401, 2210403];
    public static FunOptions Default { get; } = new();

    public FunOptions Validated()
    {
        static void Window(string name, int min, int max)
        {
            if (min < 1 || max < min || max > 24 * 60) throw new InvalidOperationException($"Aion:Fun:{name}RespawnMinMinutes/MaxMinutes must be 1..1440 with min <= max.");
        }
        Window("Boss", BossRespawnMinMinutes, BossRespawnMaxMinutes);
        Window("Named", NamedRespawnMinMinutes, NamedRespawnMaxMinutes);
        if (BuffMinutes is < 1 or > 24 * 60) throw new InvalidOperationException("Aion:Fun:BuffMinutes must be 1..1440.");
        if (BuffCooldownSeconds < 0) throw new InvalidOperationException("Aion:Fun:BuffCooldownSeconds must be 0 or more.");
        if (new[] { UpgradeChanceNormal, UpgradeChanceElite, UpgradeChanceHero, EnchantLowChance, EnchantMidChance, EnchantHighChance }.Any(c => c is < 0 or > 100)
            || EnchantLowChance + EnchantMidChance + EnchantHighChance > 100)
            throw new InvalidOperationException("Aion:Fun lucky drop chances are percents (0..100) and the three enchant chances add up to 100 at most.");
        return this;
    }

    public string Summary() =>
        $"field bosses {(FieldBosses ? $"on ({BossRespawnMinMinutes}-{BossRespawnMaxMinutes} min, named {NamedRespawnMinMinutes}-{NamedRespawnMaxMinutes} min, notices {(BossAnnouncements ? "on" : "off")}, shared loot {(SharedBossLoot ? "on" : "off")})" : "off")}, "
        + $"lucky drops {(LuckyDrops ? $"on (grade up {UpgradeChanceNormal}/{UpgradeChanceElite}/{UpgradeChanceHero}%, enchanted {EnchantLowChance}/{EnchantMidChance}/{EnchantHighChance}%)" : "off")}, "
        + $"//buff {(BuffCommand ? $"on ({BuffMinutes} min)" : "off")}, //hub {(HubCommand ? "on" : "off")}, duel restore {(DuelRestore ? "on" : "off")}";
}

/// <summary>An open-world monster's place in the field boss rules.</summary>
public enum FieldRank { None, Named, Boss }

/// <summary>The host's fun features: options and the server-wide notices the sessions queue (the host shows them to everyone).</summary>
public sealed class FunFeatures(FunOptions options)
{
    public FunOptions Options { get; } = options;

    /// <summary>Notices waiting for the host (due time in ms, text); the checks read them too.</summary>
    readonly ConcurrentQueue<(long DueMs, string Text)> notices = new();
    readonly List<(long DueMs, string Text)> later = [];
    readonly object laterLock = new();

    /// <summary>Queues a server-wide notice now, or at <paramref name="dueMs"/> (a boss's respawn).</summary>
    public void Announce(string text, long dueMs = 0)
    {
        if (!Options.BossAnnouncements) return;
        if (dueMs <= 0) { notices.Enqueue((0, text)); return; }
        lock (laterLock) later.Add((dueMs, text));
    }

    /// <summary>The notices due at <paramref name="nowMs"/>, oldest first; each is returned once.</summary>
    public List<string> TakeDue(long nowMs)
    {
        var due = new List<string>();
        while (notices.TryDequeue(out var n)) due.Add(n.Text);
        lock (laterLock)
        {
            foreach (var n in later.Where(n => n.DueMs <= nowMs).OrderBy(n => n.DueMs).ToList())
            {
                later.Remove(n);
                due.Add(n.Text);
            }
        }
        return due;
    }

    /// <summary>Notices still waiting for their time (boss respawns), for the checks and the startup log.</summary>
    public int Pending { get { lock (laterLock) return later.Count + notices.Count; } }

    /// <summary>
    /// A spawn's field rank: open-world HeroMonsters only (never in a dungeon or scripted layer). Bosses are the ones the data
    /// names a boss (FieldBoss_*, *Boss*) or of grade 6 and up; the rest are named elites.
    /// </summary>
    public static FieldRank Rank(Target target, bool inInstance)
    {
        if (inInstance || target.SubType != "HeroMonster" || !target.Hostile) return FieldRank.None;
        return target.Name.Contains("Boss", StringComparison.OrdinalIgnoreCase) || target.Grade >= 6 ? FieldRank.Boss : FieldRank.Named;
    }

    /// <summary>The respawn delay of a field-ranked monster: a random time in its window.</summary>
    public long RespawnMs(FieldRank rank, Random rng)
    {
        var (min, max) = rank == FieldRank.Boss
            ? (Options.BossRespawnMinMinutes, Options.BossRespawnMaxMinutes)
            : (Options.NamedRespawnMinMinutes, Options.NamedRespawnMaxMinutes);
        var minutes = min + rng.NextDouble() * (max - min);
        return (long)(minutes * 60_000);
    }

    static readonly Regex Noise = new(@"^(M|N|World|[LD]\d|\d+|V\d+|CV\d+|[A-Z]{1,3}|IoI|FieldBoss|Named|Single|EOR|A|B|C)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A readable name from a spawn name (the data has no display names): M_D1_FieldBoss_Kashapa_02_1 → Kashapa,
    /// M_D1_IoI_OdiumGartua_01_001 → Odium Gartua, World_L_A_Single_IslandOfEternity_Boss_1 → Island Of Eternity Boss.
    /// </summary>
    public static string DisplayName(string spawnName)
    {
        var words = spawnName.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => !Noise.IsMatch(w)).ToList();
        if (words.Count == 0) return spawnName;
        var text = new StringBuilder();
        foreach (var word in words)
        {
            // BeritraD → Beritra (a one-letter variant suffix), then split CamelCase.
            var w = word.Length > 2 && char.IsUpper(word[^1]) && char.IsLower(word[^2]) ? word[..^1] : word;
            for (var i = 0; i < w.Length; i++)
            {
                if (i > 0 && char.IsUpper(w[i]) && char.IsLower(w[i - 1])) text.Append(' ');
                text.Append(w[i]);
            }
            text.Append(' ');
        }
        return text.ToString().Trim();
    }

    /// <summary>The side of a base map for notices: 10xx Light, 11xx Dark, the rest the Abyss.</summary>
    public static string Region(int mapId) => (mapId / 100) switch { 10 => "Light lands", 11 => "Dark lands", _ => "the Abyss" };

    public const int AbyssMapId = 20;

    /// <summary>//hub destinations of a race: (label, map, map-points name). Light 1, Dark 2.</summary>
    public static IReadOnlyList<(string Label, int MapId, string Point)> Hubs(int race) => race == 2
        ? [("Altgard: Fugitive Village (Dark zone 1)", 1110, "Tel_FugitiveVillage_Home"),
           ("Dark zone 2: home point", 1111, "World_D_B_Tel_Home"),
           ("Abyss: Reshanta (PvP)", 20, "Abyss_Reshanta_A_Dark_StartPoint")]
        : [("Verteron: Dawn Legion Base (Light zone 1)", 1010, "DawnLegionBase_Teleport_Home_1"),
           ("Eltnen: Changarnerk Camp (Light zone 2)", 1011, "Tel_L2_ChangarnerkCamp_Home"),
           ("Abyss: Reshanta (PvP)", 20, "Abyss_Reshanta_A_Light_StartPoint")];
}

/// <summary>
/// Lucky drops (Aion:Fun:LuckyDrops, OURS): the next grade of an equipment piece (same category, race and the nearest item level
/// within 5) and a random enchant level for a looted piece. Grades from Enchant.dat (EnchantTables.Grade).
/// </summary>
public static class LootUpgrades
{
    static readonly Lazy<Dictionary<(string Category, int Race), List<(int Id, int Level, int Grade)>>> index = new(() =>
    {
        var byKind = new Dictionary<(string, int), List<(int, int, int)>>();
        foreach (var id in EnchantTables.Embedded.ItemIds)
        {
            if (GameData.Embedded.Item(id) is not { } info || EnchantTables.Embedded.Grade(id) < 0) continue;
            var key = (info.Category, info.Race);
            if (!byKind.TryGetValue(key, out var list)) byKind[key] = list = [];
            list.Add((id, info.Level, EnchantTables.Embedded.Grade(id)));
        }
        return byKind;
    });

    /// <summary>The next grade of an item, or null (top grade, not equipment, nothing close in level).</summary>
    public static int? NextGrade(int itemId, Random rng)
    {
        var grade = EnchantTables.Embedded.Grade(itemId);
        if (grade is < 0 or >= 3 || GameData.Embedded.Item(itemId) is not { } info) return null;
        var want = grade < 2 ? 2 : 3;   // Common and Rare → Epic (Legend), Epic → Unique
        var pool = new List<(int Id, int Level, int Grade)>();
        foreach (var race in info.Race == 0 ? new[] { 0 } : [info.Race, 0])
            if (index.Value.TryGetValue((info.Category, race), out var list)) pool.AddRange(list.Where(i => i.Grade == want && Math.Abs(i.Level - info.Level) <= 5));
        if (pool.Count == 0) return null;
        var best = pool.Min(i => Math.Abs(i.Level - info.Level));
        var closest = pool.Where(i => Math.Abs(i.Level - info.Level) == best).ToList();
        return closest[rng.Next(closest.Count)].Id;
    }

    /// <summary>Lucky upgrades over a kill's drops: each equipment drop rolls its rank's chance to become the next grade.</summary>
    public static List<(int ItemId, long Count)> Apply(List<(int ItemId, long Count)> items, string subType, FunOptions options, Random rng)
    {
        if (!options.LuckyDrops) return items;
        var chance = subType switch { "HeroMonster" => options.UpgradeChanceHero, "EliteMonster" => options.UpgradeChanceElite, _ => options.UpgradeChanceNormal };
        if (chance <= 0) return items;
        return items.Select(i => rng.NextDouble() * 100 < chance && NextGrade(i.ItemId, rng) is { } better ? (better, i.Count) : i).ToList();
    }

    /// <summary>A looted piece's random enchant level (0: none), capped at the item's max.</summary>
    public static int EnchantLevel(int itemId, FunOptions options, Random rng)
    {
        if (!options.LuckyDrops) return 0;
        var max = EnchantTables.Embedded.MaxLevel(itemId);
        if (max <= 0) return 0;
        var roll = rng.NextDouble() * 100;
        var level = roll < options.EnchantHighChance ? rng.Next(7, Math.Max(7, max) + 1)
            : roll < options.EnchantHighChance + options.EnchantMidChance ? rng.Next(4, 7)
            : roll < options.EnchantHighChance + options.EnchantMidChance + options.EnchantLowChance ? rng.Next(1, 4) : 0;
        return Math.Min(level, max);
    }
}

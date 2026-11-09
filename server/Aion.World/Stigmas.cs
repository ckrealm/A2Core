using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Stigmas (tools/data/stigmas.json, tools/stigmas.py, docs/STIGMAS.md): each class's stigma skills, the slots per character level,
/// the Stigma Shards a character has had at its level and what each stigma level costs. Global's rules, CANDIDATE until the client's
/// Exp.dat BonusStigmaPoint / StigmaSkillContextSlotMax and the SkillAcquireData Stigma rows are exported.
/// </summary>
public sealed class StigmaTable
{
    public sealed record CostBand([property: JsonPropertyName("from")] int From, [property: JsonPropertyName("to")] int To,
        [property: JsonPropertyName("shards")] int Shards);
    public sealed record LevelDamage([property: JsonPropertyName("per_level")] int PerLevel);

    sealed record Params(
        [property: JsonPropertyName("unlock_level")] int UnlockLevel,
        [property: JsonPropertyName("slot_levels")] int[] SlotLevels,
        [property: JsonPropertyName("max_skill_level")] int MaxSkillLevel,
        [property: JsonPropertyName("costs")] CostBand[] Costs,
        [property: JsonPropertyName("skill_level_damage")] LevelDamage SkillLevelDamage);

    sealed record Generated(
        [property: JsonPropertyName("classes")] Dictionary<string, int[]> Classes,
        [property: JsonPropertyName("shard_totals")] Dictionary<string, int> ShardTotals);

    sealed record File([property: JsonPropertyName("params")] Params Params, [property: JsonPropertyName("generated")] Generated Generated);

    readonly File file;
    readonly Dictionary<int, string> owner;

    StigmaTable(File file)
    {
        this.file = file;
        owner = file.Generated.Classes.SelectMany(c => c.Value.Select(id => (id, c.Key))).ToDictionary(p => p.id, p => p.Key);
    }

    /// <summary>The level the stigma tab opens at (22 on global).</summary>
    public int UnlockLevel => file.Params.UnlockLevel;

    /// <summary>The highest stigma level shards can buy (20 on global).</summary>
    public int MaxSkillLevel => file.Params.MaxSkillLevel;

    /// <summary>The most slots any setting can use (the length of slot_levels: 6, Korea's count).</summary>
    public int MaxSlots => file.Params.SlotLevels.Length;

    /// <summary>The class's stigma skills (empty for a class without any).</summary>
    public IReadOnlyList<int> ClassStigmas(string? className) =>
        className is not null && file.Generated.Classes.TryGetValue(className, out var ids) ? ids : [];

    public bool IsStigma(int skillId) => owner.ContainsKey(skillId);

    public bool IsStigmaOf(string? className, int skillId) => className is not null && owner.GetValueOrDefault(skillId) == className;

    /// <summary>The stigma slots open at a character level, of at most <paramref name="slots"/>.</summary>
    public int SlotsAt(int level, int slots) => file.Params.SlotLevels.Take(Math.Clamp(slots, 0, MaxSlots)).Count(l => level >= l);

    /// <summary>The Stigma Shards a character has had in all by its level (before the rate).</summary>
    public int ShardsAt(int level)
    {
        var key = Math.Max(1, level).ToString(CultureInfo.InvariantCulture);
        if (file.Generated.ShardTotals.TryGetValue(key, out var total)) return total;
        return file.Generated.ShardTotals.Count == 0 ? 0 : file.Generated.ShardTotals.Values.Max();
    }

    /// <summary>The shards one stigma level costs (level 1: learning it); null above the ceiling.</summary>
    public int? Cost(int stigmaLevel) =>
        file.Params.Costs.FirstOrDefault(b => stigmaLevel >= b.From && stigmaLevel <= b.To)?.Shards;

    /// <summary>The shards a stigma at this level has cost in all (0 when not learned).</summary>
    public int Spent(int stigmaLevel) => Enumerable.Range(1, Math.Max(0, stigmaLevel)).Sum(l => Cost(l) ?? 0);

    /// <summary>The damage/heal multiplier of a stigma level: 1 + per_level x (level - 1) / 10000.</summary>
    public double LevelScale(int stigmaLevel) => 1.0 + file.Params.SkillLevelDamage.PerLevel * Math.Max(0, stigmaLevel - 1) / 10_000.0;

    public static StigmaTable Parse(string json) =>
        new(JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty stigmas.json"));

    static readonly Lazy<StigmaTable> embedded = new(() =>
    {
        using var stream = typeof(StigmaTable).Assembly.GetManifestResourceStream("stigmas.json")
            ?? throw new InvalidOperationException("Missing embedded game data stigmas.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static StigmaTable Embedded => embedded.Value;
}

/// <summary>
/// Aion:Stigmas (OURS host switches, docs/STIGMAS.md). Enabled: stigma skills are bought with Stigma Shards and work only from a
/// stigma slot, as on global (off: the old behaviour, every kit skill unlocks by level). Slots: how many stigma slots open (4 =
/// global, 6 = Korea). ShardRate multiplies the shards from levelling (global's shops and achievements are not modelled).
/// MaxLevel caps the stigma level (at most the table's 20). RequireSlot: a stigma casts only while it is on a stigma slot; turn it
/// off if the client turns out to register stigmas some way the server does not see.
/// </summary>
public sealed record StigmaOptions
{
    public bool Enabled { get; init; } = true;
    public int Slots { get; init; } = 4;
    public double ShardRate { get; init; } = 1;
    public int MaxLevel { get; init; } = 20;
    public bool RequireSlot { get; init; } = true;

    public static StigmaOptions Default { get; } = new();

    public StigmaOptions Validated(StigmaTable? table = null)
    {
        var max = (table ?? StigmaTable.Embedded).MaxSlots;
        if (Slots < 0 || Slots > max) throw new InvalidOperationException($"Aion:Stigmas:Slots must be between 0 and {max} (got {Slots}).");
        if (ShardRate is < 0 or > 100 || double.IsNaN(ShardRate)) throw new InvalidOperationException($"Aion:Stigmas:ShardRate must be between 0 and 100 (got {ShardRate}).");
        var cap = (table ?? StigmaTable.Embedded).MaxSkillLevel;
        if (MaxLevel < 1 || MaxLevel > cap) throw new InvalidOperationException($"Aion:Stigmas:MaxLevel must be between 1 and {cap} (got {MaxLevel}).");
        return this;
    }

    public override string ToString() => Enabled
        ? string.Create(CultureInfo.InvariantCulture, $"stigmas on: {Slots} slots, shards x{ShardRate}, levels to {MaxLevel}, slot needed to cast {(RequireSlot ? "yes" : "no")}")
        : "stigmas off (every kit skill unlocks by level)";
}

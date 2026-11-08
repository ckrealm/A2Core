using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// One effect of a class skill beyond its Damage rows (tools/data/skill-effects.json, OURS until extracted):
/// heal, hot (heal over time), buff (stats on a player), debuff (an NPC's damage taken / speed), dot (damage over time
/// on an NPC), resurrect. Ranges are [min, max]; ratios in 1/10000.
/// </summary>
public sealed record SkillEffect(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("flat")] int[]? Flat = null,
    [property: JsonPropertyName("ratio")] int[]? Ratio = null,
    [property: JsonPropertyName("hp_ratio")] int[]? HpRatio = null,
    [property: JsonPropertyName("duration_ms")] int DurationMs = 0,
    [property: JsonPropertyName("interval_ms")] int IntervalMs = 0,
    [property: JsonPropertyName("stats")] Dictionary<string, int>? Stats = null,
    /// <summary>The SkillAbnormal the client shows for a buff; null keeps the buff server-side (no icon).</summary>
    [property: JsonPropertyName("abnormal_id")] int? AbnormalId = null,
    /// <summary>A debuff's extra damage the NPC takes from players' hits (1/10000).</summary>
    [property: JsonPropertyName("damage_taken")] int DamageTaken = 0,
    /// <summary>A debuff's NPC movement speed (1/10000 of its own; 5000 = half).</summary>
    [property: JsonPropertyName("speed")] int Speed = 0);

/// <summary>A skill's entry: who it lands on (self, friendly, enemy, dead_friendly) and its effects.</summary>
public sealed record SkillEffectSet(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("effects")] SkillEffect[] Effects)
{
    public bool Friendly => Target is "self" or "friendly" or "dead_friendly";
}

/// <summary>The skill effect table (embedded tools/data/skill-effects.json; the checks pass their own).</summary>
public sealed class SkillEffectTable
{
    sealed record File(
        [property: JsonPropertyName("heal_range")] double HealRange,
        [property: JsonPropertyName("skills")] Dictionary<string, SkillEffectSet> Skills);

    readonly File file;
    SkillEffectTable(File file) => this.file = file;

    /// <summary>How far a friendly skill reaches another player (units, OURS).</summary>
    public double HealRange => file.HealRange > 0 ? file.HealRange : 2500;

    public SkillEffectSet? Of(int skillId) => file.Skills.GetValueOrDefault(skillId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static SkillEffectTable Parse(string json) =>
        new(JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty skill-effects.json"));

    static readonly Lazy<SkillEffectTable> embedded = new(() =>
    {
        using var stream = typeof(SkillEffectTable).Assembly.GetManifestResourceStream("skill-effects.json")
            ?? throw new InvalidOperationException("Missing embedded game data skill-effects.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static SkillEffectTable Embedded => embedded.Value;
}

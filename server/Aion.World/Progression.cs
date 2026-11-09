using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Progression past the recovered tables (tools/data/progression.json, OURS; docs/PROGRESSION.md): skill unlocks after level 25,
/// fallback skill definitions and damage rows, skill levels for fallback skills, the skill-level damage scale, the MPMax curve
/// and the combat power weights. Every value is our choice; tools/progression.py writes the generated part.
/// </summary>
public sealed class ProgressionTable
{
    public sealed record UnlockParams([property: JsonPropertyName("first_level")] int FirstLevel);
    public sealed record SkillLevelDamage([property: JsonPropertyName("per_level")] int PerLevel);
    public sealed record MpCurve([property: JsonPropertyName("base")] int Base, [property: JsonPropertyName("per_level")] int PerLevel);
    public sealed record MpParams(
        [property: JsonPropertyName("default")] MpCurve Default,
        [property: JsonPropertyName("classes")] Dictionary<string, MpCurve>? Classes);
    public sealed record CombatPowerParams([property: JsonPropertyName("weights")] Dictionary<string, double> Weights);

    sealed record Params(
        [property: JsonPropertyName("unlock")] UnlockParams Unlock,
        [property: JsonPropertyName("skill_level_damage")] SkillLevelDamage SkillLevelDamage,
        [property: JsonPropertyName("mp_max")] MpParams MpMax,
        [property: JsonPropertyName("combat_power")] CombatPowerParams CombatPower);

    sealed record Generated(
        [property: JsonPropertyName("unlocks")] Dictionary<string, int[][]> Unlocks,
        [property: JsonPropertyName("skills")] Dictionary<string, SkillDefinition> Skills,
        [property: JsonPropertyName("levels")] Dictionary<string, int[][]> Levels,
        [property: JsonPropertyName("damage")] Dictionary<string, DamageRow[]> Damage);

    sealed record File(
        [property: JsonPropertyName("params")] Params Params,
        [property: JsonPropertyName("generated")] Generated Generated);

    readonly File file;
    ProgressionTable(File file) => this.file = file;

    static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The class's OURS unlocks after the recovered ones: [skill, need level, skill level].</summary>
    public IReadOnlyList<int[]> Unlocks(string className) => file.Generated.Unlocks.GetValueOrDefault(className) ?? [];

    /// <summary>The level the OURS unlocks start at (the recovered SkillAcquireData unlocks end just below it).</summary>
    public int FirstOursLevel => file.Params.Unlock.FirstLevel;

    /// <summary>An OURS definition for a skill with no recovered cast data, or null.</summary>
    public SkillDefinition? FallbackSkill(int skillId) => file.Generated.Skills.GetValueOrDefault(Key(skillId));

    /// <summary>OURS Damage rows for a recovered skill whose export carries none, or null.</summary>
    public DamageRow[]? FallbackDamage(int skillId) => file.Generated.Damage.GetValueOrDefault(Key(skillId));

    /// <summary>OURS skill-point levels of a fallback skill: [level, cost, need level], or null.</summary>
    public int[][]? Levels(int skillId) => file.Generated.Levels.GetValueOrDefault(Key(skillId));

    /// <summary>The damage/heal multiplier of a skill level: 1 + per_level x (level - 1) / 10000.</summary>
    public double SkillLevelScale(int skillLevel) => 1.0 + file.Params.SkillLevelDamage.PerLevel * Math.Max(0, skillLevel - 1) / 10_000.0;

    /// <summary>MPMax of a class at a level: base + per_level x (level - 1).</summary>
    public int MpMax(string? className, int level)
    {
        var curve = className is not null && file.Params.MpMax.Classes?.GetValueOrDefault(className) is { } own ? own : file.Params.MpMax.Default;
        return curve.Base + curve.PerLevel * Math.Max(0, level - 1);
    }

    /// <summary>Combat power: the sum of each stat times its weight (all weights positive, so gear never lowers it).</summary>
    public long CombatPower(IReadOnlyDictionary<string, double> stats) =>
        (long)Math.Round(file.Params.CombatPower.Weights.Sum(w => w.Value * stats.GetValueOrDefault(w.Key)));

    public static ProgressionTable Parse(string json) =>
        new(JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty progression.json"));

    static readonly Lazy<ProgressionTable> embedded = new(() =>
    {
        using var stream = typeof(ProgressionTable).Assembly.GetManifestResourceStream("progression.json")
            ?? throw new InvalidOperationException("Missing embedded game data progression.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static ProgressionTable Embedded => embedded.Value;
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>One boss's area attacks (tools/data/boss-area-skills.json, OURS): radius in units, the most players one hit reaches, the area skills (null: every special).</summary>
public sealed record BossAreaSkill(
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("max_targets")] int MaxTargets,
    [property: JsonPropertyName("skills")] int[]? Skills = null,
    [property: JsonPropertyName("dungeon")] string? Dungeon = null);

/// <summary>
/// Named bosses whose specials hit every player around them (OURS until the skills' target shapes are extracted; see the
/// file's "source"). An NPC not listed keeps the single-target hit.
/// </summary>
public sealed class BossAreaSkills
{
    sealed record File([property: JsonPropertyName("bosses")] Dictionary<string, BossAreaSkill> Bosses);

    readonly Dictionary<int, BossAreaSkill> bosses;

    public BossAreaSkills(IReadOnlyDictionary<int, BossAreaSkill> bosses) => this.bosses = new(bosses);

    public static BossAreaSkills Parse(string json) =>
        new((JsonSerializer.Deserialize<File>(json) ?? throw new InvalidOperationException("Empty boss-area-skills.json")).Bosses
            .ToDictionary(p => int.Parse(p.Key, System.Globalization.CultureInfo.InvariantCulture), p => p.Value));

    static readonly Lazy<BossAreaSkills> embedded = new(() =>
    {
        using var stream = typeof(BossAreaSkills).Assembly.GetManifestResourceStream("boss-area-skills.json")
            ?? throw new InvalidOperationException("Missing embedded game data boss-area-skills.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static BossAreaSkills Embedded => embedded.Value;
    public static BossAreaSkills None { get; } = new(new Dictionary<int, BossAreaSkill>());

    public int Count => bosses.Count;
    public BossAreaSkill? Of(int npcId) => bosses.GetValueOrDefault(npcId);

    /// <summary>The area of this skill of the NPC, or null (not a listed boss, the basic attack, or not one of its area skills).</summary>
    public BossAreaSkill? Area(int npcId, int skillId, int basicSkillId) =>
        Of(npcId) is { MaxTargets: > 1, Radius: > 0 } area && (area.Skills is { } listed ? listed.Contains(skillId) : skillId != basicSkillId) ? area : null;
}

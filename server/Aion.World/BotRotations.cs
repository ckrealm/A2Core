using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// One rotation entry's conditions (OURS, docs/PLAYERBOTS.md). <see cref="Target"/> "enemy" casts at the fight's target; "self"
/// at the bot, "leader" at its leader, "ally" at the living group member (the bot, its leader, their party) that most needs it,
/// and "dead_ally" at a dead one (resurrection). Percentages are 0-100 of the chosen target's HP; a missing condition holds.
/// <see cref="BuffMissing"/>: the target does not carry this skill's buff (or heal over time) now. <see cref="InCombat"/>: the
/// group is fighting (the bot has a fight, or a group member is in combat or is some monster's victim).
/// Enemy entries also take: <see cref="TargetCasting"/> (the monster winds up a special: interrupt it), <see cref="TargetStunned"/>
/// (held by a stun, or a boss's stagger), <see cref="TargetSlowed"/>, <see cref="TargetElite"/> / <see cref="TargetBoss"/> (its
/// NpcSubType), <see cref="TargetOnSelf"/> (the monster attacks the bot; false: someone else, for taunts),
/// <see cref="TargetEffectMissing"/> (this bot's debuff, dot or stun of this skill is not on it now), <see cref="TargetHas"/>
/// (this bot's effect of one of these skills is on it now: a follow-up that needs a debuff),
/// <see cref="TargetDistanceAbove"/> (cm, gap closers), <see cref="EnemiesAtLeast"/> (monsters fighting the group within the area
/// of the cast: around the bot for "self" areas, around the target otherwise), <see cref="After"/> (one of these skills was the
/// bot's last accepted cast within <see cref="AfterWithinMs"/>, 3000 by default: combo chains) and <see cref="SelfMpBelow"/>.
/// </summary>
public sealed record BotCondition(
    [property: JsonPropertyName("target")] string Target = "enemy",
    [property: JsonPropertyName("target_hp_below")] int? TargetHpBelow = null,
    [property: JsonPropertyName("target_hp_above")] int? TargetHpAbove = null,
    [property: JsonPropertyName("self_hp_below")] int? SelfHpBelow = null,
    [property: JsonPropertyName("self_mp_above")] int? SelfMpAbove = null,
    [property: JsonPropertyName("in_combat")] bool? InCombat = null,
    [property: JsonPropertyName("buff_missing")] bool? BuffMissing = null,
    [property: JsonPropertyName("self_mp_below")] int? SelfMpBelow = null,
    [property: JsonPropertyName("target_casting")] bool? TargetCasting = null,
    [property: JsonPropertyName("target_stunned")] bool? TargetStunned = null,
    [property: JsonPropertyName("target_slowed")] bool? TargetSlowed = null,
    [property: JsonPropertyName("target_elite")] bool? TargetElite = null,
    [property: JsonPropertyName("target_boss")] bool? TargetBoss = null,
    [property: JsonPropertyName("target_on_self")] bool? TargetOnSelf = null,
    [property: JsonPropertyName("target_effect_missing")] bool? TargetEffectMissing = null,
    [property: JsonPropertyName("target_has")] int[]? TargetHas = null,
    [property: JsonPropertyName("target_distance_above")] int? TargetDistanceAbove = null,
    [property: JsonPropertyName("enemies_at_least")] int? EnemiesAtLeast = null,
    [property: JsonPropertyName("after")] int[]? After = null,
    [property: JsonPropertyName("after_within_ms")] int? AfterWithinMs = null)
{
    public static BotCondition Enemy { get; } = new();
    public static readonly IReadOnlySet<string> SupportedTargets = new HashSet<string> { "enemy", "self", "leader", "ally", "dead_ally" };
    /// <summary>The entry casts at a player (the bot or a group member), not at the fight's target.</summary>
    public bool Friendly => Target is "self" or "leader" or "ally" or "dead_ally";
}

public sealed record BotRotationStep(
    [property: JsonPropertyName("skill")] int Skill,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("level")] int Level,
    [property: JsonPropertyName("when")] BotCondition? When = null,
    [property: JsonPropertyName("roles")] string[]? Roles = null,
    [property: JsonPropertyName("area")] string? Area = null)
{
    public BotCondition Condition => When ?? BotCondition.Enemy;
    /// <summary>"self" or "target": an area skill, cast at the bot's own key so it hits every monster around the location (GameSession.AreaRange).</summary>
    public bool IsArea => Area is "self" or "target";
}

/// <summary>
/// A class's rotation. <see cref="Position"/>: "front" (faces the monster), "behind" (melee that stands at the monster's back
/// unless it is the one being attacked) or "ranged". <see cref="RoleRanges"/> overrides <see cref="Range"/> per role (a healer
/// keeps its distance).
/// </summary>
public sealed record BotClassRotation(
    [property: JsonPropertyName("weapon_type")] string? WeaponType,
    [property: JsonPropertyName("range")] int Range,
    [property: JsonPropertyName("roles")] string[] Roles,
    [property: JsonPropertyName("rotation")] BotRotationStep[] Rotation,
    [property: JsonPropertyName("position")] string? Position = null,
    [property: JsonPropertyName("role_ranges")] Dictionary<string, int>? RoleRanges = null)
{
    public int RangeFor(string role) => RoleRanges?.GetValueOrDefault(role) is > 0 and var r ? r : Range;
}

/// <summary>Per-class bot rotations (tools/data/bot-rotations.json, built by tools/bot_rotations.py). All OURS.</summary>
public sealed class BotRotations
{
    [JsonPropertyName("global_cooldown_ms")] public int GlobalCooldownMs { get; init; } = 1000;
    /// <summary>A tank bot's damage counts this many times in a monster's hate table (NpcAi.Engage), so it pulls monsters off the others.</summary>
    [JsonPropertyName("tank_hate_multiplier")] public double TankHateMultiplier { get; init; } = 1;
    [JsonPropertyName("classes")] public Dictionary<string, BotClassRotation> Classes { get; init; } = [];

    static readonly Lazy<BotRotations> embedded = new(() =>
    {
        using var stream = typeof(BotRotations).Assembly.GetManifestResourceStream("bot-rotations.json")
            ?? throw new InvalidOperationException("Missing embedded bot-rotations.json");
        return Read(stream);
    });

    public static BotRotations Embedded => embedded.Value;

    public static BotRotations Read(Stream stream) =>
        JsonSerializer.Deserialize<BotRotations>(stream) ?? throw new InvalidOperationException("Empty bot rotations");

    public BotClassRotation? For(string? className) => className is null ? null : Classes.GetValueOrDefault(className);
}

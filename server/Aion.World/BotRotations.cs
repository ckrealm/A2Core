using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// One rotation entry's conditions (OURS, docs/PLAYERBOTS.md). <see cref="Target"/> "enemy" casts at the fight's target; "self"
/// at the bot, "leader" at its leader, "ally" at the living group member (the bot, its leader, their party) that most needs it,
/// and "dead_ally" at a dead one (resurrection). Percentages are 0-100 of the chosen target's HP; a missing condition holds.
/// <see cref="BuffMissing"/>: the target does not carry this skill's buff (or heal over time) now. <see cref="InCombat"/>: the
/// group is fighting (the bot has a fight, or a group member is in combat or is some monster's victim).
/// </summary>
public sealed record BotCondition(
    [property: JsonPropertyName("target")] string Target = "enemy",
    [property: JsonPropertyName("target_hp_below")] int? TargetHpBelow = null,
    [property: JsonPropertyName("target_hp_above")] int? TargetHpAbove = null,
    [property: JsonPropertyName("self_hp_below")] int? SelfHpBelow = null,
    [property: JsonPropertyName("self_mp_above")] int? SelfMpAbove = null,
    [property: JsonPropertyName("in_combat")] bool? InCombat = null,
    [property: JsonPropertyName("buff_missing")] bool? BuffMissing = null)
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
    [property: JsonPropertyName("roles")] string[]? Roles = null)
{
    public BotCondition Condition => When ?? BotCondition.Enemy;
}

public sealed record BotClassRotation(
    [property: JsonPropertyName("weapon_type")] string? WeaponType,
    [property: JsonPropertyName("range")] int Range,
    [property: JsonPropertyName("roles")] string[] Roles,
    [property: JsonPropertyName("rotation")] BotRotationStep[] Rotation);

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

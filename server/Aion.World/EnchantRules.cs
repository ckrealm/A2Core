namespace Aion.World;

/// <summary>
/// Aion:Enchant (OURS: a server owner's choice, nothing observed): enchanting odds on top of the client's Enchant table
/// (tools/data/enchant.json). The official progression stays (same levels, costs and per-level chances); these only make
/// it faster. <see cref="Official"/> (1 / 1 / false / false) reproduces the table exactly; <see cref="Default"/> is the
/// friends-server setting: chances and the failure correction doubled, no level lost on a failure, materials spent.
/// The client's enchant window may still show the official chance: it reads its own copy of the table.
/// </summary>
public sealed record EnchantRules
{
    /// <summary>The table's SuccessProb times this, capped at 100% (10000).</summary>
    public double SuccessMultiplier { get; init; } = 2.0;
    /// <summary>The table's FailCorrectionProb (the chance a failure adds to the next attempt) times this.</summary>
    public double PityMultiplier { get; init; } = 2.0;
    /// <summary>A failure never lowers the level (the table's FailPenalty, Rune accessories, is ignored).</summary>
    public bool NoDowngrade { get; init; } = true;
    /// <summary>A failure spends no Kina or materials (only a success pays).</summary>
    public bool KeepMaterialsOnFail { get; init; }

    public const int Certain = 10_000;
    public const double MaxMultiplier = 100;
    public static EnchantRules Default { get; } = new();
    public static EnchantRules Official { get; } = new() { SuccessMultiplier = 1, PityMultiplier = 1, NoDowngrade = false, KeepMaterialsOnFail = false };

    /// <summary>The chance of an attempt out of 10000: SuccessProb x multiplier plus the correction built up, at most 10000.</summary>
    public int Chance(int success, int bonus) =>
        (int)Math.Clamp(Math.Round(success * SuccessMultiplier, MidpointRounding.AwayFromZero) + bonus, 0, Certain);

    /// <summary>The correction after a failure: what was built up plus FailCorrectionProb x multiplier (uncapped, as the table).</summary>
    public int BonusAfterFail(int bonus, int failCorrection) =>
        (int)Math.Min(int.MaxValue / 2, bonus + Math.Round(failCorrection * PityMultiplier, MidpointRounding.AwayFromZero));

    /// <summary>
    /// The level after a failure. FailPenalty is stored as a level change (-1 on Rune_PvE_Unique +1..+9, CANDIDATE: read
    /// as one level lost, its size taken whatever the sign); never below 0.
    /// </summary>
    public int LevelAfterFail(int level, int failPenalty) => NoDowngrade ? level : Math.Max(0, level - Math.Abs(failPenalty));

    public EnchantRules Validated()
    {
        if (!(SuccessMultiplier is > 0 and <= MaxMultiplier))
            throw new InvalidOperationException($"Aion:Enchant:SuccessMultiplier must be above 0 and at most {MaxMultiplier} (got {SuccessMultiplier}).");
        if (!(PityMultiplier is >= 0 and <= MaxMultiplier))
            throw new InvalidOperationException($"Aion:Enchant:PityMultiplier must be between 0 and {MaxMultiplier} (got {PityMultiplier}).");
        return this;
    }

    public override string ToString() => this == Official
        ? "official odds"
        : $"chance x{SuccessMultiplier}, failure correction x{PityMultiplier}, {(NoDowngrade ? "no level lost on failure" : "official level loss")}, "
          + (KeepMaterialsOnFail ? "materials kept on failure" : "materials spent on failure");
}

namespace Aion.World;

/// <summary>
/// Aion:Friends (OURS: the friends server's choices, nothing observed). Each switch answers a common complaint about the
/// official game while keeping its progression:
/// <list type="bullet">
/// <item><see cref="FreePremiumPass"/>: the paid battle-pass track's level rewards can be claimed without a purchase, and a
/// purchase request succeeds without charging anything.</item>
/// <item><see cref="BothFactions"/>: an account may hold Elyos and Asmodian characters on this server (official: one race per
/// server; each race's world lists only its own characters, with its own lobby slots, as migration 042 allows).</item>
/// <item><see cref="FlightMultiplier"/>: the flight gauge's maximum (FP, officially 250,000: 25 s of flight) times this;
/// drain and refill per second stay official, so flights last longer and the gauge takes longer to refill.</item>
/// </list>
/// <see cref="Official"/> (false, false, 1) is the official behaviour.
/// </summary>
public sealed record FriendsOptions
{
    public bool FreePremiumPass { get; init; } = true;
    public bool BothFactions { get; init; } = true;
    public double FlightMultiplier { get; init; } = DefaultFlightMultiplier;

    /// <summary>OURS default: twice the official flight time (50 s instead of 25 s).</summary>
    public const double DefaultFlightMultiplier = 2.0, MaxFlightMultiplier = 10.0;
    public static FriendsOptions Default { get; } = new();
    public static FriendsOptions Official { get; } = new() { FreePremiumPass = false, BothFactions = false, FlightMultiplier = 1.0 };

    /// <summary>The flight gauge's maximum for an official maximum.</summary>
    public int FlightMax(int officialMax) => (int)Math.Min(int.MaxValue, Math.Round(officialMax * FlightMultiplier));

    public FriendsOptions Validated()
    {
        if (double.IsNaN(FlightMultiplier) || FlightMultiplier is < 1.0 or > MaxFlightMultiplier)
            throw new InvalidOperationException($"Aion:Friends:FlightMultiplier must be between 1 and {MaxFlightMultiplier} (got {FlightMultiplier}).");
        return this;
    }

    public string Describe() =>
        $"premium battle pass {(FreePremiumPass ? "free" : "locked")}, {(BothFactions ? "both races" : "one race")} per account, flight x{FlightMultiplier:0.##}";
}

namespace Aion.World;

/// <summary>
/// Aion:TimeGates (OURS: a server owner's choice). Every switch defaults to no time gate, for a small friends server; turn one
/// on to get the official-like behaviour back. Dungeon daily entry limits are Aion:Dungeons:EntryLimits (also off by default).
/// <list type="bullet">
/// <item>ShopLimits: merchants' limited goods (daily, weekly, monthly and one-time purchase caps). Off: no cap is applied or
/// counted, and the shop opens reporting nothing bought.</item>
/// <item>DungeonTimeLimits: timed dungeons (expeditions, seals, awakening, daily, boss challenge, raids, field events) fail
/// when their timer runs out. Off: no deadline; the client is sent no end time.</item>
/// <item>CollectionRotation: the season's daily and weekly collections open one day or week at a time with ten picked lists.
/// Off: every collection of the season is open all season with all its lists.</item>
/// <item>GatherRespawnSeconds: how long a gathered node stays used up for that player (the previous fixed value was 60).</item>
/// </list>
/// </summary>
public sealed record TimeGateOptions
{
    public bool ShopLimits { get; init; }
    public bool DungeonTimeLimits { get; init; }
    public bool CollectionRotation { get; init; }
    public int GatherRespawnSeconds { get; init; } = DefaultGatherRespawnSeconds;

    public const int DefaultGatherRespawnSeconds = 10, OfficialGatherRespawnSeconds = 60, MaxGatherRespawnSeconds = 3600;
    public static TimeGateOptions Default { get; } = new();
    /// <summary>Everything as before these switches existed (the checks of the official-like behaviour use it).</summary>
    public static TimeGateOptions Official { get; } = new()
    {
        ShopLimits = true, DungeonTimeLimits = true, CollectionRotation = true, GatherRespawnSeconds = OfficialGatherRespawnSeconds,
    };

    public long GatherRespawnMs => Math.Clamp(GatherRespawnSeconds, 0, MaxGatherRespawnSeconds) * 1000L;

    public TimeGateOptions Validated()
    {
        if (GatherRespawnSeconds is < 0 or > MaxGatherRespawnSeconds)
            throw new InvalidOperationException($"Aion:TimeGates:GatherRespawnSeconds must be between 0 and {MaxGatherRespawnSeconds} (got {GatherRespawnSeconds}).");
        return this;
    }

    public override string ToString() =>
        $"shop limits {(ShopLimits ? "on" : "off")}, dungeon timers {(DungeonTimeLimits ? "on" : "off")}, collection rotation {(CollectionRotation ? "on" : "off")}, gather nodes back after {GatherRespawnSeconds} s";
}

/// <summary>
/// Aion:Unlocks (OURS): alt-friendly account perks. Off by default. On, every character gets them at its next world entry
/// (new and existing characters alike; once per character and perk, remembered in its settings, so a skin or pet given
/// later is not taken back when a switch is turned off).
/// <list type="bullet">
/// <item>AllSkins: every skin of Skin.dat added to the character's collection.</item>
/// <item>AllPets: every pet/mount (VehicleList) acquired at its top level, and every creature board raised to its top level.</item>
/// </list>
/// Aion:Unlocks also holds the profession and pet cost switches: CraftAlwaysSucceeds (default on: a craft never fails and
/// eats its components) and PetSoulCost (multiplies the souls a pet costs to acquire and level up; default 0.5, at least 1).
/// </summary>
public sealed record UnlockOptions
{
    public bool AllSkins { get; init; }
    public bool AllPets { get; init; }
    public bool CraftAlwaysSucceeds { get; init; } = true;
    public double PetSoulCost { get; init; } = DefaultPetSoulCost;

    public const double DefaultPetSoulCost = 0.5;
    /// <summary>The setting names that remember a perk was given (per character).</summary>
    public const string SkinsGivenSetting = "unlocks_all_skins", PetsGivenSetting = "unlocks_all_pets";
    public static UnlockOptions Default { get; } = new();
    public static UnlockOptions Official { get; } = new() { CraftAlwaysSucceeds = false, PetSoulCost = 1 };

    /// <summary>A pet soul cost after <see cref="PetSoulCost"/>: at least 1 when anything was due.</summary>
    public int SoulCost(int cost) => cost <= 0 || PetSoulCost == 1 ? cost : Math.Max(1, (int)Math.Ceiling(cost * PetSoulCost));

    public UnlockOptions Validated()
    {
        if (!double.IsFinite(PetSoulCost) || PetSoulCost is < 0 or > 10)
            throw new InvalidOperationException($"Aion:Unlocks:PetSoulCost must be between 0 and 10 (got {PetSoulCost}).");
        return this;
    }

    public override string ToString() =>
        $"all skins {(AllSkins ? "on" : "off")}, all pets maxed {(AllPets ? "on" : "off")}, crafts {(CraftAlwaysSucceeds ? "always succeed" : "can fail")}, pet soul cost x{PetSoulCost}";
}

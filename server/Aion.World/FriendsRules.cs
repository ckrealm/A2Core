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
/// <item>ReviveSeconds: how long a dead player waits before the server revives it at the map's respawn point (the client's
/// own respawn request is not decoded yet, so this is the wait after every death; the previous fixed value was 60).</item>
/// </list>
/// </summary>
public sealed record TimeGateOptions
{
    public bool ShopLimits { get; init; }
    public bool DungeonTimeLimits { get; init; }
    public bool CollectionRotation { get; init; }
    public int GatherRespawnSeconds { get; init; } = DefaultGatherRespawnSeconds;
    public int ReviveSeconds { get; init; } = DefaultReviveSeconds;

    public const int DefaultGatherRespawnSeconds = 10, OfficialGatherRespawnSeconds = 60, MaxGatherRespawnSeconds = 3600;
    public const int DefaultReviveSeconds = 5, OfficialReviveSeconds = 60, MaxReviveSeconds = 600;
    public static TimeGateOptions Default { get; } = new();
    /// <summary>Everything as before these switches existed (the checks of the official-like behaviour use it).</summary>
    public static TimeGateOptions Official { get; } = new()
    {
        ShopLimits = true, DungeonTimeLimits = true, CollectionRotation = true, GatherRespawnSeconds = OfficialGatherRespawnSeconds,
        ReviveSeconds = OfficialReviveSeconds,
    };

    public long GatherRespawnMs => Math.Clamp(GatherRespawnSeconds, 0, MaxGatherRespawnSeconds) * 1000L;
    public long ReviveMs => Math.Clamp(ReviveSeconds, 1, MaxReviveSeconds) * 1000L;

    public TimeGateOptions Validated()
    {
        if (GatherRespawnSeconds is < 0 or > MaxGatherRespawnSeconds)
            throw new InvalidOperationException($"Aion:TimeGates:GatherRespawnSeconds must be between 0 and {MaxGatherRespawnSeconds} (got {GatherRespawnSeconds}).");
        if (ReviveSeconds is < 1 or > MaxReviveSeconds)
            throw new InvalidOperationException($"Aion:TimeGates:ReviveSeconds must be between 1 and {MaxReviveSeconds} (got {ReviveSeconds}).");
        return this;
    }

    public override string ToString() =>
        $"revive after {ReviveSeconds} s, shop limits {(ShopLimits ? "on" : "off")}, dungeon timers {(DungeonTimeLimits ? "on" : "off")}, collection rotation {(CollectionRotation ? "on" : "off")}, gather nodes back after {GatherRespawnSeconds} s";
}

/// <summary>
/// Aion:Unlocks (OURS): alt-friendly account perks. AllPets and AllWings are on by default, AllSkins off. On, every character gets them at its next world entry
/// (new and existing characters alike; once per character and perk, remembered in its settings, so a skin or pet given
/// later is not taken back when a switch is turned off).
/// <list type="bullet">
/// <item>AllSkins: every skin of Skin.dat added to the character's collection.</item>
/// <item>AllPets: every pet/mount (VehicleList) acquired at its top level, and every creature board raised to its top level.</item>
/// <item>AllWings: every wing (feather) of the character's race registered in its wing collection.</item>
/// </list>
/// Aion:Unlocks also holds the profession and pet cost switches: CraftAlwaysSucceeds (default on: a craft never fails and
/// eats its components) and PetSoulCost (multiplies the souls a pet costs to acquire and level up; default 0.5, at least 1).
/// </summary>
public sealed record UnlockOptions
{
    public bool AllSkins { get; init; }
    public bool AllPets { get; init; } = true;
    public bool AllWings { get; init; } = true;
    public bool CraftAlwaysSucceeds { get; init; } = true;
    public double PetSoulCost { get; init; } = DefaultPetSoulCost;

    public const double DefaultPetSoulCost = 0.5;
    /// <summary>The setting names that remember a perk was given (per character).</summary>
    public const string SkinsGivenSetting = "unlocks_all_skins", PetsGivenSetting = "unlocks_all_pets", WingsGivenSetting = "unlocks_all_wings";
    public static UnlockOptions Default { get; } = new();
    public static UnlockOptions Official { get; } = new() { AllPets = false, AllWings = false, CraftAlwaysSucceeds = false, PetSoulCost = 1 };

    /// <summary>A pet soul cost after <see cref="PetSoulCost"/>: at least 1 when anything was due.</summary>
    public int SoulCost(int cost) => cost <= 0 || PetSoulCost == 1 ? cost : Math.Max(1, (int)Math.Ceiling(cost * PetSoulCost));

    public UnlockOptions Validated()
    {
        if (!double.IsFinite(PetSoulCost) || PetSoulCost is < 0 or > 10)
            throw new InvalidOperationException($"Aion:Unlocks:PetSoulCost must be between 0 and 10 (got {PetSoulCost}).");
        return this;
    }

    public override string ToString() =>
        $"all skins {(AllSkins ? "on" : "off")}, all pets maxed {(AllPets ? "on" : "off")}, all wings {(AllWings ? "on" : "off")}, crafts {(CraftAlwaysSucceeds ? "always succeed" : "can fail")}, pet soul cost x{PetSoulCost}";
}

/// <summary>
/// Aion:Boost (OURS): an alt boost bought in game with "//boost" once any character of the account has reached
/// <see cref="RequiredLevel"/>. The character it is used on (below <see cref="Level"/>) pays <see cref="Price"/> Kinah and
/// gets: level <see cref="Level"/>; every released main story (Hero), regional (District) and exploration quest of its race,
/// sealed dungeons included, up to recommended level <see cref="QuestLevel"/> marked done (no quest rewards paid); a gear set
/// of the best level-45 items (<see cref="Gear"/>: about 1,400 item level) in its bag; the whole world map revealed.
/// </summary>
public sealed record BoostOptions
{
    public bool Enabled { get; init; } = true;
    public int Level { get; init; } = 45;
    public int RequiredLevel { get; init; } = 45;
    public long Price { get; init; } = 500_000;
    public int QuestLevel { get; init; } = 46;
    public bool Gear { get; init; } = true;

    public static BoostOptions Default { get; } = new();
    /// <summary>Off for a session without host options (the checks turn it on themselves).</summary>
    public static BoostOptions Off { get; } = new() { Enabled = false };

    public BoostOptions Validated(int maxLevel = 100)
    {
        if (Level < 2 || Level > maxLevel) throw new InvalidOperationException($"Aion:Boost:Level must be between 2 and {maxLevel} (got {Level}).");
        if (RequiredLevel < 1 || RequiredLevel > maxLevel) throw new InvalidOperationException($"Aion:Boost:RequiredLevel must be between 1 and {maxLevel} (got {RequiredLevel}).");
        if (Price < 0 || Price > 10_000_000_000) throw new InvalidOperationException($"Aion:Boost:Price must be between 0 and 10000000000 (got {Price}).");
        if (QuestLevel < 0 || QuestLevel > 100) throw new InvalidOperationException($"Aion:Boost:QuestLevel must be between 0 and 100 (got {QuestLevel}).");
        return this;
    }

    public override string ToString() => !Enabled ? "boost off"
        : $"//boost to level {Level} for {Price:N0} Kinah once an account has level {RequiredLevel}, quests to {QuestLevel}{(Gear ? ", gear set" : "")}";
}

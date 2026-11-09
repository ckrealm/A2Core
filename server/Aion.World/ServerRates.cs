using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Rates as configured (OURS: a server owner's choice, nothing observed). A profile presets the rates ("official": all
/// 1.0; "accelerated": experience 5, Kinah 3, drops 3; "friends", the host default: experience 5, Kinah 10, Kinah drop
/// chance x2, drops 4, gathering/crafting/pet experience 10); any rate set explicitly overrides the preset. Unset
/// QuestExperience follows the resolved Experience. <see cref="Resolve"/> checks the values and gives the rates the game uses.
/// </summary>
public sealed record RatesOptions
{
    public string Profile { get; init; } = ServerRates.FriendsProfile;
    public double? Experience { get; init; }
    public double? Kinah { get; init; }
    public double? Drop { get; init; }
    public double? QuestExperience { get; init; }
    /// <summary>Multiplies a monster's chance to drop Kinah (the chance is capped at 100%).</summary>
    public double? KinahChance { get; init; }
    /// <summary>Gathering mastery experience per gather.</summary>
    public double? GatherExperience { get; init; }
    /// <summary>Crafting mastery experience per craft.</summary>
    public double? CraftExperience { get; init; }
    /// <summary>Pet (creature board) experience.</summary>
    public double? PetExperience { get; init; }

    /// <summary>The effective base rates: the profile's preset with the explicit values over it. Invalid values throw.</summary>
    public ServerRates Resolve()
    {
        var profile = (Profile ?? "").Trim().ToLowerInvariant();
        var preset = profile switch
        {
            ServerRates.OfficialProfile => ServerRates.Official,
            ServerRates.AcceleratedProfile => ServerRates.Accelerated,
            "" or ServerRates.FriendsProfile => ServerRates.Friends,
            _ => throw new InvalidOperationException($"Aion:Rates:Profile must be \"{ServerRates.OfficialProfile}\", \"{ServerRates.AcceleratedProfile}\" or \"{ServerRates.FriendsProfile}\" (got \"{Profile}\")."),
        };
        var experience = Checked(Experience ?? preset.Experience, nameof(Experience));
        return new ServerRates(
            preset.Profile,
            experience,
            Checked(Kinah ?? preset.Kinah, nameof(Kinah)),
            Checked(Drop ?? preset.Drop, nameof(Drop)),
            Checked(QuestExperience ?? experience, nameof(QuestExperience)))
        {
            KinahChance = Checked(KinahChance ?? preset.KinahChance, nameof(KinahChance)),
            GatherExperience = Checked(GatherExperience ?? preset.GatherExperience, nameof(GatherExperience)),
            CraftExperience = Checked(CraftExperience ?? preset.CraftExperience, nameof(CraftExperience)),
            PetExperience = Checked(PetExperience ?? preset.PetExperience, nameof(PetExperience)),
        };
    }

    static double Checked(double value, string name) =>
        double.IsFinite(value) && value >= 0 && value <= ServerRates.MaxRate ? value
            : throw new InvalidOperationException($"Aion:Rates:{name} must be between 0 and {ServerRates.MaxRate} (got {value.ToString(CultureInfo.InvariantCulture)}).");
}

/// <summary>
/// The host's base rates (OURS). Each multiplies with the scheduled event boosts (<see cref="SharedWorld.ExpRate"/>,
/// <see cref="SharedWorld.DropRate"/>, each clamped 0.1-10 on its own); the product is not clamped, so the effective
/// multiplier reaches at most <see cref="MaxRate"/> x 10 (loot rolls are further capped at <see cref="MaxLootRolls"/>).
/// </summary>
public sealed record ServerRates(string Profile, double Experience, double Kinah, double Drop, double QuestExperience)
{
    public const string OfficialProfile = "official", AcceleratedProfile = "accelerated", FriendsProfile = "friends";
    /// <summary>OURS: the largest base rate accepted at startup.</summary>
    public const double MaxRate = 100;
    /// <summary>OURS: at most this many loot-table rolls per kill, whatever the rates (a guard on drop counts and message size).</summary>
    public const int MaxLootRolls = 100;

    public static ServerRates Official { get; } = new(OfficialProfile, 1, 1, 1, 1);
    public static ServerRates Accelerated { get; } = new(AcceleratedProfile, 5, 3, 3, 5);
    /// <summary>OURS: the host default for a small friends server (fast leveling, plenty of Kinah, quick professions and pets).</summary>
    public static ServerRates Friends { get; } = new(FriendsProfile, 5, 10, 4, 5)
    {
        KinahChance = 2, GatherExperience = 10, CraftExperience = 10, PetExperience = 10,
    };

    /// <summary>Multiplies the chance of a monster's Kinah drop (capped at certain).</summary>
    public double KinahChance { get; init; } = 1;
    /// <summary>Multiplies gathering mastery experience.</summary>
    public double GatherExperience { get; init; } = 1;
    /// <summary>Multiplies crafting mastery experience.</summary>
    public double CraftExperience { get; init; } = 1;
    /// <summary>Multiplies pet (creature board) experience.</summary>
    public double PetExperience { get; init; } = 1;

    /// <summary>A profession or pet experience gain after its rate: at least 1 when anything was due and the rate is not 0.</summary>
    public static long Scaled(long amount, double rate) =>
        amount <= 0 || rate == 1 ? amount : rate == 0 ? 0 : Math.Max(1, (long)Math.Min(long.MaxValue / 4, Math.Round(amount * rate)));

    /// <summary>Kill experience after the base rate and the event boost.</summary>
    public long KillExperience(long experience, double eventRate) => Math.Max(0, (long)Math.Round(experience * Experience * eventRate));

    /// <summary>Quest experience after the base quest rate (scheduled events boost kills only, as before).</summary>
    public long QuestReward(long experience) => experience <= 0 ? experience : (long)Math.Round(experience * QuestExperience);

    /// <summary>A Kinah amount after the base rate: at least 1 when anything was due and the rate is not 0.</summary>
    public long KinahAmount(long kinah) =>
        kinah <= 0 || Kinah == 1 ? kinah : Kinah == 0 ? 0 : Math.Max(1, (long)Math.Round(kinah * Kinah));

    /// <summary>A reward list with the Kinah item's counts scaled (other items unchanged; a Kinah line scaled to 0 is dropped).</summary>
    public List<(int ItemId, long Count)> WithKinah(IEnumerable<(int ItemId, long Count)> items)
    {
        var scaled = new List<(int ItemId, long Count)>();
        foreach (var (item, count) in items)
        {
            var amount = item == GameSession.KinaItemId ? KinahAmount(count) : count;
            if (item != GameSession.KinaItemId || amount > 0) scaled.Add((item, amount));
        }
        return scaled;
    }

    /// <summary>Loot-table rolls for one kill: the whole part of base x event, plus one more with the fraction's chance.</summary>
    public int LootRolls(double eventRate, Random rng)
    {
        var rate = Drop * eventRate;
        var whole = Math.Floor(rate);
        var rolls = (int)Math.Min(MaxLootRolls, whole) + (rate - whole > 0 && rng.NextDouble() < rate - whole ? 1 : 0);
        return Math.Min(MaxLootRolls, rolls);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"profile {Profile}: experience x{Experience}, quest experience x{QuestExperience}, Kinah x{Kinah}, Kinah drop chance x{KinahChance}, drops x{Drop}, gathering x{GatherExperience}, crafting x{CraftExperience}, pets x{PetExperience}");
}

/// <summary>
/// Aion:Rules (OURS): host switches a small private server may want. Pvp off refuses duel challenges and arena queues with
/// their existing failure replies and stops player-vs-player damage (duels, arenas, the Abyss). RespawnSeconds is the
/// monster respawn after a kill (default 30, the previous fixed value; at least 5 so the corpse is removed first).
/// PartyLoot shares a party member's kill among the members nearby (<see cref="PartyLootRule"/>; default roundrobin);
/// FallbackLoot gives monsters without a reward table the generic level table (default on; <see cref="MonsterRewards"/>).
/// </summary>
public sealed record WorldRules
{
    public bool Pvp { get; init; } = true;
    public int RespawnSeconds { get; init; } = (int)(GameSession.RespawnMs / 1000);
    /// <summary>freeforall | roundrobin | killer (see <see cref="PartyLootRule"/>).</summary>
    public string PartyLoot { get; init; } = "roundrobin";
    public bool FallbackLoot { get; init; } = true;
    /// <summary>
    /// The model the client is told a Brawler (class Fighter, docs/friends-server/BRAWLER.md) has: "gladiator" (default,
    /// safe: the Gladiator body, pc_id 5-8, with Brawler skills) or "own" (the CANDIDATE Fighter pc_id 45-48 from
    /// starter-skills.json, unconfirmed on the 2.0.6.0 client). Applied through <see cref="GameData.BrawlerOwnModel"/>.
    /// </summary>
    public string BrawlerModel { get; init; } = "gladiator";

    public bool BrawlerOwnModel => BrawlerModel.Trim().Equals("own", StringComparison.OrdinalIgnoreCase);

    public const int MinRespawnSeconds = 5, MaxRespawnSeconds = 3600;
    public static WorldRules Default { get; } = new();

    public long RespawnMs => Math.Clamp(RespawnSeconds, MinRespawnSeconds, MaxRespawnSeconds) * 1000L;

    /// <summary>The PartyLoot setting read (case and spaces ignored), or null when it is not one of the three.</summary>
    public static PartyLootRule? ParseLootRule(string? text) => (text ?? "").Trim().ToLowerInvariant().Replace("-", "").Replace("_", "") switch
    {
        "freeforall" => PartyLootRule.FreeForAll,
        "roundrobin" => PartyLootRule.RoundRobin,
        "killer" => PartyLootRule.Killer,
        _ => null,
    };

    /// <summary>The party loot rule in force (an unknown setting is refused by <see cref="Validated"/>; round robin if bypassed).</summary>
    public PartyLootRule LootRule => ParseLootRule(PartyLoot) ?? PartyLootRule.RoundRobin;

    public WorldRules Validated()
    {
        if (RespawnSeconds is < MinRespawnSeconds or > MaxRespawnSeconds)
            throw new InvalidOperationException($"Aion:Rules:RespawnSeconds must be between {MinRespawnSeconds} and {MaxRespawnSeconds} (got {RespawnSeconds}).");
        if (ParseLootRule(PartyLoot) is null)
            throw new InvalidOperationException($"Aion:Rules:PartyLoot must be \"freeforall\", \"roundrobin\" or \"killer\" (got \"{PartyLoot}\").");
        if (BrawlerModel.Trim().ToLowerInvariant() is not ("gladiator" or "own"))
            throw new InvalidOperationException($"Aion:Rules:BrawlerModel must be \"gladiator\" or \"own\" (got \"{BrawlerModel}\").");
        return this;
    }

    public override string ToString() =>
        $"PvP {(Pvp ? "on" : "off")}, monster respawn {RespawnMs / 1000} s, party loot {LootRule.ToString().ToLowerInvariant()}, fallback loot {(FallbackLoot ? "on" : "off")}, Brawler model {(BrawlerOwnModel ? "own (pc_id 45-48)" : "gladiator")}";
}

using System.Collections.Concurrent;
using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Loot (OURS: a server owner's choice for "official progression, just accelerated"; nothing observed). It changes which
/// equipment a kill gives, never how often the official tables roll, except for bad-luck protection. All switches off gives
/// exactly the loot of before (the same picks from the same random numbers).
/// <list type="bullet">
/// <item><see cref="ClassAware"/>: an equipment pick the looter's class or race cannot use is drawn again from the same
/// package or level band (same weights) up to <see cref="ClassRerolls"/> times; if every draw is still unusable, the weapon
/// becomes the same-grade weapon of the looter's class (<see cref="LootLooter.Swap"/>). Item.dat has no class field: as
/// tools/equipment_rules.py, only weapons are class-bound (class-skills.json weapon_type); Guarders, armour and accessories
/// suit every class. ItemRace is checked too.</item>
/// <item><see cref="AvoidDuplicates"/>: an equipment pick the looter already has (bag, storage, equipped, on the ground, or
/// picked earlier in the same kill) is drawn again the same way; a duplicate is kept when no draw finds anything new.</item>
/// <item><see cref="BadLuckProtection"/>: per character and per monster (NpcData id) of the ranks in
/// <see cref="PityMonsters"/>, kills that gave the character no equipment ("dry" kills) are counted. Each dry kill multiplies
/// the chance of the monster's equipment packages by 1 + <see cref="DryKillBonus"/> x dry kills; after
/// <see cref="PityKills"/> dry kills the next kill gives one equipment item for the class whatever the roll. The counts are
/// kept in memory (<see cref="Book"/>, one per host): a relog keeps them, a host restart clears them.</item>
/// </list>
/// In a party (Aion:Rules:PartyLoot) the kill is rolled for the killer; a member who receives a weapon of another class gets
/// the same-grade weapon of its own class (swap only: the roll is over), and each member nearby counts its own dry kills and
/// gets its own guaranteed item.
/// </summary>
public sealed record LootRules
{
    public bool ClassAware { get; init; } = true;
    public int ClassRerolls { get; init; } = DefaultRerolls;
    public bool AvoidDuplicates { get; init; } = true;
    public bool BadLuckProtection { get; init; } = true;
    /// <summary>The equipment chance grows by this share per dry kill (0.25: x1.25 after one, x3 after eight).</summary>
    public double DryKillBonus { get; init; } = DefaultDryKillBonus;
    public int PityKills { get; init; } = DefaultPityKills;
    /// <summary>NpcSubTypes whose kills bad-luck protection counts (comma separated); trash (NormalMonster) is left out by default.</summary>
    public string PityMonsters { get; init; } = "EliteMonster,HeroMonster";

    /// <summary>The dry-kill counts (not bound from configuration).</summary>
    internal LootPityBook Book { get; init; } = new();

    /// <summary>OURS defaults: 3 re-draws, +25 % per dry kill, an item guaranteed after 8 dry kills.</summary>
    public const int DefaultRerolls = 3, DefaultPityKills = 8, MaxRerolls = 20, MaxPityKills = 1000;
    /// <summary>Re-draws for a guaranteed item (at least this many, so it nearly always suits the class and is new).</summary>
    public const int DefaultPityRerolls = 8;
    public const double DefaultDryKillBonus = 0.25, MaxDryKillBonus = 10;
    public static LootRules Default { get; } = new();
    /// <summary>Every switch off: the loot of before.</summary>
    public static LootRules Off => new() { ClassAware = false, AvoidDuplicates = false, BadLuckProtection = false };

    /// <summary>Whether picks are re-drawn at all (otherwise the tables roll exactly as before).</summary>
    public bool Picks => ClassAware || AvoidDuplicates;

    public IReadOnlySet<string> PitySubTypes => new HashSet<string>(
        PityMonsters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether bad-luck protection counts a kill of a monster of this rank.</summary>
    public bool Counts(string subType) => BadLuckProtection && PitySubTypes.Contains(subType);

    /// <summary>The equipment chance multiplier after this many dry kills.</summary>
    public double ChanceAfter(int dryKills) => BadLuckProtection && dryKills > 0 ? 1 + DryKillBonus * dryKills : 1;

    public LootRules Validated()
    {
        if (ClassRerolls is < 0 or > MaxRerolls) throw new InvalidOperationException($"Aion:Loot:ClassRerolls must be between 0 and {MaxRerolls} (got {ClassRerolls}).");
        if (PityKills is < 1 or > MaxPityKills) throw new InvalidOperationException($"Aion:Loot:PityKills must be between 1 and {MaxPityKills} (got {PityKills}).");
        if (double.IsNaN(DryKillBonus) || DryKillBonus is < 0 or > MaxDryKillBonus)
            throw new InvalidOperationException($"Aion:Loot:DryKillBonus must be between 0 and {MaxDryKillBonus} (got {DryKillBonus}).");
        if (BadLuckProtection && PitySubTypes.Count == 0) throw new InvalidOperationException("Aion:Loot:PityMonsters must name at least one NpcSubType (e.g. EliteMonster,HeroMonster).");
        return this;
    }

    public override string ToString()
    {
        var parts = new List<string>
        {
            ClassAware ? $"class-aware ({ClassRerolls} re-draws, then a same-grade swap)" : "class-aware off",
            AvoidDuplicates ? "duplicates avoided" : "duplicates allowed",
            BadLuckProtection
                ? $"bad-luck protection: +{DryKillBonus.ToString("0.##", CultureInfo.InvariantCulture)}x per dry kill, guaranteed after {PityKills} ({PityMonsters})"
                : "bad-luck protection off",
        };
        return string.Join(", ", parts);
    }
}

/// <summary>Dry kills per (character, monster), in memory (thread safe: party members count on their own loops).</summary>
public sealed class LootPityBook
{
    readonly ConcurrentDictionary<(Guid Character, int NpcId), int> dry = new();

    public int Dry(Guid character, int npcId) => dry.GetValueOrDefault((character, npcId));

    /// <summary>Records a kill: whether it gave equipment. Returns the dry count before it.</summary>
    public int Record(Guid character, int npcId, bool gotEquipment)
    {
        var before = Dry(character, npcId);
        if (gotEquipment) dry.TryRemove((character, npcId), out _);
        else dry[(character, npcId)] = before + 1;
        return before;
    }
}

/// <summary>
/// One looter's view for Aion:Loot: its class and race, the items it already has, and the pick rule that re-draws a package
/// or band pick (<see cref="Pick"/>, passed to LootTables.Roll and MonsterRewards.Fallback).
/// </summary>
public sealed class LootLooter(GameData data, LootRules rules, string? className, int race, ISet<int> owned)
{
    readonly (string WeaponType, int StarterWeapon)? weapon = data.ClassWeapon(className);

    /// <summary>The weapon EquipCategories (tools/equipment_rules.py WEAPONS): the only class-bound equipment.</summary>
    public static readonly IReadOnlySet<string> Weapons = new HashSet<string>(StringComparer.Ordinal)
        { "Greatsword", "Sword", "Dagger", "Bow", "Magicbook", "Orb", "Mace", "Staff", "Gauntlet" };

    /// <summary>Equipment: an item of items.json (Item.dat ItemType Equip).</summary>
    public bool IsEquipment(int itemId) => data.Item(itemId) is not null;

    /// <summary>Whether the looter's class and race can use the item (anything not equipment: yes; an unknown class: yes).</summary>
    public bool Usable(int itemId)
    {
        if (data.Item(itemId) is not { } item) return true;
        if (item.Race != 0 && race is 1 or 2 && item.Race != race) return false;
        return weapon is not { } w || !Weapons.Contains(item.Category) || item.Category == w.WeaponType;
    }

    public bool Owned(int itemId) => owned.Contains(itemId);

    /// <summary>
    /// The looter's class weapon of the same grade as another class's weapon, or null. Item.dat numbers weapons 11CTxxxxx with the
    /// weapon type in the fourth digit (1 Greatsword ... 8 Staff, the digit of the class's starter weapons); the sibling with the
    /// class's digit has the same ItemLevel, PermitLevelMin and ItemRace for every weapon of items.json (checked; CANDIDATE
    /// reading of the numbering). The sibling is used only when items.json confirms all of that.
    /// </summary>
    public int? Swap(int itemId)
    {
        if (weapon is not { } w || data.Item(itemId) is not { } item || !Weapons.Contains(item.Category) || item.Category == w.WeaponType) return null;
        var text = itemId.ToString(CultureInfo.InvariantCulture);
        var digit = w.StarterWeapon.ToString(CultureInfo.InvariantCulture);
        if (text.Length != digit.Length || text.Length < 5) return null;
        var sibling = int.Parse(text[..3] + digit[3] + text[4..], CultureInfo.InvariantCulture);
        return data.Item(sibling) is { } other && other.Category == w.WeaponType && other.Level == item.Level && other.PermitLevel == item.PermitLevel
            && other.Race == item.Race && Usable(sibling) ? sibling : null;
    }

    /// <summary>
    /// An item for this looter from a pick (<paramref name="draw"/> draws one item id from the package or band): equipment the class
    /// cannot use or already has is drawn again up to ClassRerolls times; then an unusable weapon is swapped for the class's;
    /// then a usable duplicate is kept; failing everything the first draw. A non-equipment draw is always taken.
    /// </summary>
    public int Pick(Func<int> draw)
    {
        var first = draw();
        var candidate = first;
        int? usableDuplicate = null;
        var unusable = new List<int>();
        for (var attempt = 0; ; attempt++)
        {
            if (candidate == 0 || !IsEquipment(candidate)) return candidate;
            var usable = !rules.ClassAware || Usable(candidate);
            var fresh = !rules.AvoidDuplicates || !Owned(candidate);
            if (usable && fresh) return Take(candidate);
            if (usable) usableDuplicate ??= candidate;
            else unusable.Add(candidate);
            if (attempt >= rules.ClassRerolls) break;
            candidate = draw();
        }
        int? duplicateSwap = null;
        foreach (var bad in unusable)
            if (Swap(bad) is { } swapped)
            {
                if (!rules.AvoidDuplicates || !Owned(swapped)) return Take(swapped);
                duplicateSwap ??= swapped;
            }
        return Take(usableDuplicate ?? duplicateSwap ?? first);
    }

    /// <summary>A kill's item that went to this looter (party share): an unusable weapon becomes its class's (no re-draw: the roll is over).</summary>
    public int Adjust(int itemId) => rules.ClassAware && !Usable(itemId) && Swap(itemId) is { } swapped ? Take(swapped) : Take(itemId);

    int Take(int itemId)
    {
        if (IsEquipment(itemId)) owned.Add(itemId);
        return itemId;
    }
}

/// <summary>A kill as bad-luck protection sees it: the monster, its rank and level (a party member's own guaranteed item).</summary>
public sealed record LootKill(int NpcId, string SubType, int Level, bool Generic);

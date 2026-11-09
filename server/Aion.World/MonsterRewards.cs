using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Aion:Rules:PartyLoot (OURS: how the official server shares loot is not observed). PartyInfo already carries the client's
/// EPartyDistributionType (PartyInfo.Distribution, always Sequence; CANDIDATE layout, SocialPackets.cs) and
/// PartyInfoUpdate_NT can change it; neither follows this rule yet: the rule is applied on the server alone.
/// FreeForAll: every item goes to a random member nearby. RoundRobin: items go to the members nearby in turn (join order,
/// the turn kept by the party). Killer: everything to the killer (the behaviour before parties shared loot).
/// </summary>
public enum PartyLootRule { FreeForAll, RoundRobin, Killer }

/// <summary>
/// Our generic monster rewards (OURS), tools/fallback_loot.py: tools/data/fallback-loot.json (a level-banded item table for
/// monsters without a RewardData table; ids from items.json only) and tools/data/kinah-drops.json (Kinah per kill by level
/// band and NpcSubType). NPCs whose NpcSubType is not in a table's chances (citizens, the training dummy) get nothing.
/// </summary>
public sealed class MonsterRewards
{
    sealed record ItemBand([property: JsonPropertyName("min")] int Min, [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("items")] int[] Items);
    sealed record KinahBand([property: JsonPropertyName("min")] int Min, [property: JsonPropertyName("max")] int Max,
        [property: JsonPropertyName("kinah")] long[] Kinah);
    sealed record FallbackFile([property: JsonPropertyName("chance")] Dictionary<string, long> Chance,
        [property: JsonPropertyName("bands")] ItemBand[] Bands);
    sealed record KinahFile([property: JsonPropertyName("chance")] Dictionary<string, long> Chance,
        [property: JsonPropertyName("multiplier")] Dictionary<string, long> Multiplier,
        [property: JsonPropertyName("bands")] KinahBand[] Bands);

    /// <summary>Chances are out of 1e8, as loot.json.</summary>
    const double ChanceScale = 100_000_000;
    readonly FallbackFile fallback;
    readonly KinahFile kinah;

    MonsterRewards(FallbackFile fallback, KinahFile kinah) => (this.fallback, this.kinah) = (fallback, kinah);

    static T Load<T>(string name)
    {
        using var stream = typeof(MonsterRewards).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded game data {name}");
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidOperationException($"Empty {name}");
    }

    static readonly Lazy<MonsterRewards> embedded = new(() => new MonsterRewards(Load<FallbackFile>("fallback-loot.json"), Load<KinahFile>("kinah-drops.json")));
    public static MonsterRewards Embedded => embedded.Value;

    /// <summary>Every item id the fallback table can give (for checks).</summary>
    public IEnumerable<int> FallbackItems => fallback.Bands.SelectMany(b => b.Items).Distinct();

    static T? Band<T>(T[] bands, int level, Func<T, int> min, Func<T, int> max) where T : class =>
        bands.Length == 0 ? null : bands.FirstOrDefault(b => level >= min(b) && level <= max(b)) ?? (level < min(bands[0]) ? bands[0] : bands[^1]);

    /// <summary>The fallback items of a level (its band's list), or none.</summary>
    public IReadOnlyList<int> FallbackBand(int level) => Band(fallback.Bands, level, b => b.Min, b => b.Max)?.Items ?? [];

    /// <summary>One fallback roll for a monster of this rank and level: one item of its band by the rank's chance, or nothing.</summary>
    /// Aion:Loot (OURS, LootRules) may re-draw the pick (<paramref name="pick"/>) and multiply the chance (bad-luck protection).
    public List<(int ItemId, long Count)> Fallback(string subType, int level, Random rng, Func<Func<int>, int>? pick = null, double chanceScale = 1)
    {
        if (!fallback.Chance.TryGetValue(subType, out var chance)) return [];
        var roll = rng.NextDouble() * ChanceScale;
        if (chanceScale == 1 ? roll >= chance : roll >= chance * chanceScale) return [];
        var items = FallbackBand(level);
        if (items.Count == 0) return [];
        int Draw() => items[rng.Next(items.Count)];
        return [(pick is null ? Draw() : pick(Draw), 1)];
    }

    /// <summary>Whether a monster of this rank rolls the fallback table at all.</summary>
    public bool HasFallback(string subType, int level) => fallback.Chance.ContainsKey(subType) && FallbackBand(level).Count > 0;

    /// <summary>Bad-luck protection (LootRules, OURS): one item of the level's band, whatever the chance; 0 when the band is empty.</summary>
    public int GuaranteedFallback(int level, Random rng, Func<Func<int>, int>? pick = null)
    {
        var items = FallbackBand(level);
        if (items.Count == 0) return 0;
        int Draw() => items[rng.Next(items.Count)];
        return pick is null ? Draw() : pick(Draw);
    }

    /// <summary>The (min, max) Kinah of a kill before rates, or (0, 0) for an NPC that pays none.</summary>
    public (long Min, long Max) KinahRange(string subType, int level)
    {
        if (!kinah.Chance.ContainsKey(subType) || Band(kinah.Bands, level, b => b.Min, b => b.Max) is not { Kinah: [var low, var high] }) return (0, 0);
        var times = kinah.Multiplier.GetValueOrDefault(subType, 1);
        return (low * times, high * times);
    }

    /// <summary>The Kinah of one kill before rates: by the rank's chance, an amount in the band's range; 0 otherwise.</summary>
    public long Kinah(string subType, int level, Random rng)
    {
        var (low, high) = KinahRange(subType, level);
        if (high <= 0 || rng.NextDouble() * ChanceScale >= kinah.Chance[subType]) return 0;
        return low + (long)(rng.NextDouble() * (high - low + 1));
    }

    /// <summary>An amount split evenly in <paramref name="parts"/> shares; the remainder goes one each to the first shares (the killer first).</summary>
    public static long[] Split(long amount, int parts)
    {
        var shares = new long[Math.Max(1, parts)];
        if (amount <= 0) return shares;
        var each = amount / shares.Length;
        var rest = amount % shares.Length;
        for (var i = 0; i < shares.Length; i++) shares[i] = each + (i < rest ? 1 : 0);
        return shares;
    }
}

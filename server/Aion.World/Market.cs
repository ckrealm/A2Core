using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Market (OURS, docs/MARKET.md): the "//ah" auction house and its market bot, in the spirit of the WoW private server
/// AH bots: a seller that keeps the market stocked at sensible prices and a buyer that buys players' fairly priced listings.
/// Nothing here is observed from the official Trade Post; every number is a server owner's choice.
/// </summary>
public sealed record MarketOptions
{
    /// <summary>The "//ah" commands and the market itself.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>How long a player listing stays up before its item goes back to the seller (hours).</summary>
    public double ListingHours { get; init; } = 48;
    /// <summary>Listings one character may have at once (0: no cap).</summary>
    public int MaxListingsPerCharacter { get; init; } = 50;
    /// <summary>The market's cut of a sale, taken from the seller's payout (percent, 0-100). The bot's sales pay no one.</summary>
    public double SaleTaxPercent { get; init; } = 5;
    /// <summary>Highest price each a listing may ask (Kinah).</summary>
    public long MaxPriceEach { get; init; } = 2_000_000_000;
    /// <summary>Players of every world (Light and Dark) see and buy each other's listings. Off: own world plus the bot's.</summary>
    public bool CrossWorld { get; init; }
    /// <summary>How often expired listings are returned (seconds).</summary>
    public int ExpireCheckSeconds { get; init; } = 60;
    public MarketSellerOptions Seller { get; init; } = new();
    public MarketBuyerOptions Buyer { get; init; } = new();

    public TimeSpan ListingDuration => TimeSpan.FromHours(ListingHours);

    public MarketOptions Validated()
    {
        if (!double.IsFinite(ListingHours) || ListingHours is < 1 or > 24 * 30) throw Bad("ListingHours must be between 1 and 720");
        if (MaxListingsPerCharacter < 0) throw Bad("MaxListingsPerCharacter must be 0 or more");
        if (!double.IsFinite(SaleTaxPercent) || SaleTaxPercent is < 0 or > 100) throw Bad("SaleTaxPercent must be between 0 and 100");
        if (MaxPriceEach < 1) throw Bad("MaxPriceEach must be at least 1");
        if (ExpireCheckSeconds is < 5 or > 3600) throw Bad("ExpireCheckSeconds must be between 5 and 3600");
        Seller.Validate();
        Buyer.Validate();
        return this;
    }

    internal static InvalidOperationException Bad(string what) => new($"Aion:Market:{what}.");

    public override string ToString() => !Enabled ? "market off" : string.Create(CultureInfo.InvariantCulture,
        $"market on (listings {ListingHours} h, tax {SaleTaxPercent}%, {(CrossWorld ? "cross-world" : "per world")}); seller bot {(Seller.Enabled ? $"on ({Seller.TargetListings} listings, x{Seller.PriceMultiplier} vendor value)" : "off")}; buyer bot {(Buyer.Enabled ? $"on (up to x{Buyer.MaxPriceMultiplier} vendor value)" : "off")}");
}

/// <summary>Aion:Market:Seller: the bot that keeps listings up (AH bot seller).</summary>
public sealed record MarketSellerOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>The seller name players see on the bot's listings.</summary>
    public string Name { get; init; } = "Market Trader";
    /// <summary>Listings the bot keeps up in total.</summary>
    public int TargetListings { get; init; } = 250;
    /// <summary>New listings per cycle at most (restocks gradually).</summary>
    public int ListingsPerCycle { get; init; } = 25;
    /// <summary>Listings of one item at once at most.</summary>
    public int MaxListingsPerItem { get; init; } = 2;
    public int CycleSeconds { get; init; } = 60;
    /// <summary>How long a bot listing stays up (hours); expired ones are replaced by new picks.</summary>
    public double ListingHours { get; init; } = 12;
    /// <summary>Price each = the item's vendor value (Item.dat sell price, or its crafting materials) x this.</summary>
    public double PriceMultiplier { get; init; } = 3;
    /// <summary>Each listing's price varies up or down by at most this much (percent).</summary>
    public double PriceVariancePercent { get; init; } = 20;
    /// <summary>Lowest price each the bot asks (Kinah).</summary>
    public long MinPriceEach { get; init; } = 10;
    /// <summary>Stackable items are listed in stacks of 1 to this many.</summary>
    public int MaxStack { get; init; } = 20;
    /// <summary>Pools: crafting materials (recipe components), crafted products, and all equipment in Item.dat.</summary>
    public bool Materials { get; init; } = true;
    public bool Crafted { get; init; } = true;
    public bool Equipment { get; init; }
    /// <summary>Equipment level range the bot sells (Item.dat ItemLevel; 0: no bound). Materials have no level.</summary>
    public int MinItemLevel { get; init; }
    public int MaxItemLevel { get; init; }
    /// <summary>Extra item IDs to sell, and item IDs never to sell.</summary>
    public int[] Include { get; init; } = [];
    public int[] Exclude { get; init; } = [];
    /// <summary>Fixed prices each by item ID (as text keys, e.g. Aion__Market__Seller__Prices__610710006=250).</summary>
    public Dictionary<string, long> Prices { get; init; } = [];

    public TimeSpan ListingDuration => TimeSpan.FromHours(ListingHours);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 32) throw MarketOptions.Bad("Seller:Name must be 1 to 32 characters");
        if (TargetListings is < 0 or > 10_000) throw MarketOptions.Bad("Seller:TargetListings must be between 0 and 10000");
        if (ListingsPerCycle is < 1 or > 1000) throw MarketOptions.Bad("Seller:ListingsPerCycle must be between 1 and 1000");
        if (MaxListingsPerItem < 1) throw MarketOptions.Bad("Seller:MaxListingsPerItem must be at least 1");
        if (CycleSeconds is < 5 or > 86_400) throw MarketOptions.Bad("Seller:CycleSeconds must be between 5 and 86400");
        if (!double.IsFinite(ListingHours) || ListingHours is < 0.1 or > 24 * 30) throw MarketOptions.Bad("Seller:ListingHours must be between 0.1 and 720");
        if (!double.IsFinite(PriceMultiplier) || PriceMultiplier is <= 0 or > 1000) throw MarketOptions.Bad("Seller:PriceMultiplier must be above 0 and at most 1000");
        if (!double.IsFinite(PriceVariancePercent) || PriceVariancePercent is < 0 or > 90) throw MarketOptions.Bad("Seller:PriceVariancePercent must be between 0 and 90");
        if (MinPriceEach < 1) throw MarketOptions.Bad("Seller:MinPriceEach must be at least 1");
        if (MaxStack < 1) throw MarketOptions.Bad("Seller:MaxStack must be at least 1");
        if (MinItemLevel < 0 || MaxItemLevel < 0) throw MarketOptions.Bad("Seller:MinItemLevel and MaxItemLevel must be 0 or more");
        foreach (var (key, price) in Prices)
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _) || price < 1)
                throw MarketOptions.Bad($"Seller:Prices:{key} must be an item ID with a price of at least 1");
    }

    public long? FixedPrice(int itemId) => Prices.TryGetValue(itemId.ToString(CultureInfo.InvariantCulture), out var p) ? p : null;
}

/// <summary>Aion:Market:Buyer: the bot that buys players' listings (AH bot buyer), so selling works with few players.</summary>
public sealed record MarketBuyerOptions
{
    public bool Enabled { get; init; } = true;
    public int CycleSeconds { get; init; } = 120;
    /// <summary>A listing must have been up this long before the bot considers it (minutes), so players see it first.</summary>
    public double MinListingAgeMinutes { get; init; } = 10;
    /// <summary>The bot buys a listing priced at most the item's vendor value x this (each).</summary>
    public double MaxPriceMultiplier { get; init; } = 3;
    /// <summary>Chance per cycle that the bot buys a listing it would pay for (percent).</summary>
    public double BuyChancePercent { get; init; } = 35;
    public int MaxPurchasesPerCycle { get; init; } = 5;
    /// <summary>Kinah the bot spends a day at most (0: no cap). Its Kinah comes from nowhere: the cap limits money creation.</summary>
    public long KinahPerDay { get; init; }
    /// <summary>Items without a vendor value: the bot pays at most this much each (0: it leaves them alone).</summary>
    public long UnpricedMaxPriceEach { get; init; }

    internal void Validate()
    {
        if (CycleSeconds is < 5 or > 86_400) throw MarketOptions.Bad("Buyer:CycleSeconds must be between 5 and 86400");
        if (!double.IsFinite(MinListingAgeMinutes) || MinListingAgeMinutes is < 0 or > 10_080) throw MarketOptions.Bad("Buyer:MinListingAgeMinutes must be between 0 and 10080");
        if (!double.IsFinite(MaxPriceMultiplier) || MaxPriceMultiplier is <= 0 or > 1000) throw MarketOptions.Bad("Buyer:MaxPriceMultiplier must be above 0 and at most 1000");
        if (!double.IsFinite(BuyChancePercent) || BuyChancePercent is < 0 or > 100) throw MarketOptions.Bad("Buyer:BuyChancePercent must be between 0 and 100");
        if (MaxPurchasesPerCycle < 0) throw MarketOptions.Bad("Buyer:MaxPurchasesPerCycle must be 0 or more");
        if (KinahPerDay < 0 || UnpricedMaxPriceEach < 0) throw MarketOptions.Bad("Buyer:KinahPerDay and UnpricedMaxPriceEach must be 0 or more");
    }
}

/// <summary>
/// Item values for the market bot (OURS): the Item.dat sell price merchants pay (shops.json), or for a crafted item what its
/// materials are worth plus the crafting fee per product, whichever is higher. Items with neither have no value.
/// </summary>
public sealed class MarketPricing
{
    readonly Func<int, long?> vendorPrice;
    readonly Dictionary<int, CraftTables.Recipe> recipeOf = [];
    readonly Dictionary<int, long?> memo = [];
    readonly Dictionary<int, ItemInfo> equipment;
    readonly Lock gate = new();

    public IReadOnlyList<int> MaterialIds { get; }
    public IReadOnlyList<int> CraftedIds { get; }
    public IReadOnlyList<(int Id, ItemInfo Info)> EquipmentItems { get; }

    public MarketPricing(Func<int, long?> vendorPrice, IEnumerable<CraftTables.Recipe> recipes, IEnumerable<(int Id, ItemInfo Info)> equipment)
    {
        this.vendorPrice = vendorPrice;
        var all = recipes.ToList();
        foreach (var r in all.OrderBy(r => r.Id)) recipeOf.TryAdd(r.Product, r);
        EquipmentItems = equipment.OrderBy(e => e.Id).ToList();
        this.equipment = EquipmentItems.ToDictionary(e => e.Id, e => e.Info);
        var equipIds = EquipmentItems.Select(e => e.Id).ToHashSet();
        MaterialIds = all.SelectMany(r => r.Components.Select(c => c.Item)).Where(i => !equipIds.Contains(i)).Distinct().Order().ToList();
        CraftedIds = recipeOf.Keys.Order().ToList();
    }

    static readonly Lazy<MarketPricing> embedded = new(() =>
        new MarketPricing(ShopTables.Embedded.SellPrice, CraftTables.Embedded.All, GameData.Embedded.Items()));
    public static MarketPricing Embedded => embedded.Value;

    /// <summary>Whether the item is equipment (never stacks).</summary>
    public bool IsEquipment(int itemId) => equipment.ContainsKey(itemId);
    public ItemInfo? Equipment(int itemId) => equipment.GetValueOrDefault(itemId);

    /// <summary>The item's value in Kinah each, or null when nothing prices it.</summary>
    public long? Value(int itemId)
    {
        lock (gate) return Value(itemId, 0);
    }

    long? Value(int itemId, int depth)
    {
        if (memo.TryGetValue(itemId, out var known)) return known;
        long? vendor = vendorPrice(itemId) is > 0 and var v ? v : null;
        long? crafted = null;
        if (depth < 4 && recipeOf.TryGetValue(itemId, out var recipe) && recipe.ProductCount > 0)
        {
            long sum = recipe.Fee;
            var complete = true;
            foreach (var (item, count) in recipe.Components)
            {
                if (Value(item, depth + 1) is not { } each) { complete = false; break; }
                sum += each * count;
            }
            if (complete && sum > 0) crafted = Math.Max(1, sum / recipe.ProductCount);
        }
        var value = vendor is null && crafted is null ? null : (long?)Math.Max(vendor ?? 0, crafted ?? 0);
        if (depth == 0 || value is not null) memo[itemId] = value;
        return value;
    }

    /// <summary>The bot seller's asking price each: a fixed price, or the value x multiplier, varied, at least the minimum.</summary>
    public long? SellerPrice(int itemId, MarketSellerOptions seller, Random rng)
    {
        if (seller.FixedPrice(itemId) is { } fixedPrice) return fixedPrice;
        if (Value(itemId) is not { } value) return null;
        var variance = seller.PriceVariancePercent / 100.0;
        var factor = seller.PriceMultiplier * (1 + (rng.NextDouble() * 2 - 1) * variance);
        return Math.Max(seller.MinPriceEach, (long)Math.Round(value * factor));
    }

    /// <summary>The most the bot buyer pays each for the item, or null when it does not buy it.</summary>
    public long? BuyerLimit(int itemId, MarketBuyerOptions buyer, MarketSellerOptions seller)
    {
        if (seller.FixedPrice(itemId) is { } fixedPrice) return fixedPrice;
        if (Value(itemId) is { } value) return (long)Math.Floor(value * buyer.MaxPriceMultiplier);
        return buyer.UnpricedMaxPriceEach > 0 ? buyer.UnpricedMaxPriceEach : null;
    }

    /// <summary>The items the bot seller picks from: the enabled pools within the level range, plus Include, minus Exclude, priced.</summary>
    public IReadOnlyList<int> SellerPool(MarketSellerOptions seller)
    {
        var pool = new HashSet<int>();
        if (seller.Materials) pool.UnionWith(MaterialIds);
        if (seller.Crafted) pool.UnionWith(CraftedIds);
        if (seller.Equipment) pool.UnionWith(EquipmentItems.Select(e => e.Id));
        pool.RemoveWhere(id => Equipment(id) is { } info && (seller.MinItemLevel > 0 && info.Level < seller.MinItemLevel || seller.MaxItemLevel > 0 && info.Level > seller.MaxItemLevel));
        pool.UnionWith(seller.Include);
        pool.ExceptWith(seller.Exclude);
        pool.Remove(GameSession.KinaItemId);
        return pool.Where(id => seller.FixedPrice(id) is not null || Value(id) is not null).Order().ToList();
    }
}

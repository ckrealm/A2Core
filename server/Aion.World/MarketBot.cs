using Aion.Application.Gameplay;
using Aion.Application.Market;

namespace Aion.World;

/// <summary>
/// The market bot (Aion:Market, OURS; docs/MARKET.md), like the AH bots of WoW private servers. Each tick it returns expired
/// listings; every Seller:CycleSeconds the seller tops the bot's listings up to Seller:TargetListings with items from its pool
/// at their vendor value x Seller:PriceMultiplier; every Buyer:CycleSeconds the buyer buys players' listings priced at most
/// their vendor value x Buyer:MaxPriceMultiplier (by chance, up to a few a cycle, within Buyer:KinahPerDay) and the seller is
/// paid by mail. <paramref name="mailed"/> tells an online seller about the payout mail.
/// </summary>
public sealed class MarketBot(IMarketStore store, MarketOptions options, MarketPricing pricing, Random? random = null,
    Action<Guid, PostRecord>? mailed = null, Action<string>? log = null,
    (Dictionary<int, long> ById, Dictionary<string, long> ByName)? references = null)
{
    readonly Random rng = random ?? new Random();
    readonly SemaphoreSlim busy = new(1, 1);
    DateTimeOffset nextExpire = DateTimeOffset.MinValue, nextSell = DateTimeOffset.MinValue, nextBuy = DateTimeOffset.MinValue;
    IReadOnlyList<int>? pool;
    bool referencesResolved = references is null;

    public MarketOptions Options => options;
    public IReadOnlyList<int> Pool => pool ??= pricing.SellerPool(options.Seller);

    /// <summary>One tick: whatever is due (expiry, seller, buyer). Ticks do not overlap.</summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!options.Enabled || !await busy.WaitAsync(0, ct)) return;
        try
        {
            if (!referencesResolved) await ResolveReferencesAsync(ct);
            if (now >= nextExpire)
            {
                nextExpire = now.AddSeconds(options.ExpireCheckSeconds);
                var expired = await store.ExpireAsync(ct);
                var returned = expired.Count(e => !e.Listing.ByBot);
                if (returned > 0) log?.Invoke($"Market: {returned} expired listing(s) returned to their sellers.");
            }
            if (options.Seller.Enabled && now >= nextSell)
            {
                nextSell = now.AddSeconds(options.Seller.CycleSeconds);
                await SellAsync(ct);
            }
            if (options.Buyer.Enabled && now >= nextBuy)
            {
                nextBuy = now.AddSeconds(options.Buyer.CycleSeconds);
                await BuyAsync(now, ct);
            }
        }
        finally { busy.Release(); }
    }

    /// <summary>The reference price table into the pricing: item IDs as they are, names matched exactly against the catalogue.</summary>
    public async Task ResolveReferencesAsync(CancellationToken ct = default)
    {
        if (references is not { } table) return;
        var prices = new Dictionary<int, long>(table.ById);
        var unknown = new List<string>();
        foreach (var (name, price) in table.ByName)
        {
            var found = (await store.FindItemsAsync(name, 50, ct)).Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0) unknown.Add(name);
            foreach (var (id, _) in found) prices[id] = price;
        }
        pricing.SetReferences(prices);
        pool = null;
        referencesResolved = true;
        log?.Invoke($"Market: {prices.Count} reference prices loaded" + (unknown.Count > 0 ? $"; no catalogue item named {string.Join(", ", unknown.Take(20))}{(unknown.Count > 20 ? ", ..." : "")}" : "") + ".");
    }

    /// <summary>The seller's cycle: new bot listings up to the target (at most ListingsPerCycle, MaxListingsPerItem per item). Returns how many.</summary>
    public async Task<int> SellAsync(CancellationToken ct = default)
    {
        var seller = options.Seller;
        var items = Pool;
        if (items.Count == 0) return 0;
        var mine = await store.ListingsOfAsync(null, ct);
        var perItem = mine.GroupBy(l => l.TemplateId).ToDictionary(g => g.Key, g => g.Count());
        var wanted = Math.Min(seller.ListingsPerCycle, seller.TargetListings - mine.Count);
        var listed = 0;
        for (var attempt = 0; listed < wanted && attempt < wanted * 4; attempt++)
        {
            var item = items[rng.Next(items.Count)];
            if (perItem.GetValueOrDefault(item) >= seller.MaxListingsPerItem) continue;
            if (pricing.SellerPrice(item, seller, rng) is not { } price) continue;
            var quantity = pricing.IsEquipment(item) ? 1 : rng.Next(1, seller.MaxStack + 1);
            if (await store.ListForBotAsync(seller.Name, item, quantity, Math.Min(price, options.MaxPriceEach), seller.ListingDuration, ct) is null) continue;
            perItem[item] = perItem.GetValueOrDefault(item) + 1;
            listed++;
        }
        return listed;
    }

    /// <summary>The buyer's cycle: players' listings old enough and priced within the limit, bought by chance. Returns the purchases.</summary>
    public async Task<IReadOnlyList<MarketPurchase>> BuyAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var buyer = options.Buyer;
        var bought = new List<MarketPurchase>();
        if (buyer.MaxPurchasesPerCycle == 0) return bought;
        var budget = buyer.KinahPerDay > 0 ? buyer.KinahPerDay - await store.BotSpentSinceAsync(now.AddDays(-1), ct) : long.MaxValue;
        var candidates = (await store.PlayerListingsAsync(now - TimeSpan.FromMinutes(buyer.MinListingAgeMinutes), 500, ct))
            .OrderBy(_ => rng.Next()).ToList();
        foreach (var listing in candidates)
        {
            if (bought.Count >= buyer.MaxPurchasesPerCycle) break;
            if (pricing.BuyerLimit(listing.TemplateId, buyer, options.Seller) is not { } limit || listing.PriceEach > limit) continue;
            long total;
            try { total = checked(listing.PriceEach * listing.Quantity); }
            catch (OverflowException) { continue; }
            if (total > budget || rng.NextDouble() * 100 >= buyer.BuyChancePercent) continue;
            var purchase = await store.BotBuyAsync(listing.Id, options.SaleTaxPercent, ct);
            if (!purchase.Ok) continue;
            budget -= purchase.Paid;
            bought.Add(purchase);
            if (listing.SellerCharacter is { } seller && purchase.SellerPost is { } post) mailed?.Invoke(seller, post);
            log?.Invoke($"Market: the buyer bot bought {listing.ItemName} x{purchase.Quantity} from {listing.SellerName} for {purchase.Paid} Kinah.");
        }
        return bought;
    }
}

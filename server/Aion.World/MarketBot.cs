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
    Action<Guid, PostRecord>? mailed = null, Action<string>? log = null)
{
    readonly Random rng = random ?? new Random();
    readonly SemaphoreSlim busy = new(1, 1);
    DateTimeOffset nextExpire = DateTimeOffset.MinValue, nextSell = DateTimeOffset.MinValue, nextBuy = DateTimeOffset.MinValue;
    IReadOnlyList<int>? pool;

    public MarketOptions Options => options;
    public IReadOnlyList<int> Pool => pool ??= pricing.SellerPool(options.Seller);

    /// <summary>One tick: whatever is due (expiry, seller, buyer). Ticks do not overlap.</summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!options.Enabled || !await busy.WaitAsync(0, ct)) return;
        try
        {
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
        var candidates = (await store.SearchAsync(null, null, null, 0, 200, ct))
            .Where(l => !l.ByBot && now - l.ListedAt >= TimeSpan.FromMinutes(buyer.MinListingAgeMinutes))
            .OrderBy(_ => rng.Next()).ToList();
        foreach (var listing in candidates)
        {
            if (bought.Count >= buyer.MaxPurchasesPerCycle) break;
            if (pricing.BuyerLimit(listing.TemplateId, buyer, options.Seller) is not { } limit || listing.PriceEach > limit) continue;
            var total = listing.PriceEach * listing.Quantity;
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

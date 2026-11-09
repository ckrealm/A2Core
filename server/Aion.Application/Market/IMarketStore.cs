using Aion.Application.Gameplay;
using Aion.Core.Accounts;

namespace Aion.Application.Market;

/// <summary>
/// One market listing (migration 040, OURS). <see cref="SellerCharacter"/> null: listed by the market bot. <see cref="Enchanted"/>:
/// the escrowed item carries upgrades (enchant, rolled stats), so the buyer gets that very item.
/// </summary>
public sealed record MarketListing(long Id, Guid? SellerCharacter, string SellerName, int? WorldId, int TemplateId, string ItemName,
    long Quantity, long PriceEach, DateTimeOffset ListedAt, DateTimeOffset ExpiresAt, bool Enchanted = false)
{
    public bool ByBot => SellerCharacter is null;
}

/// <summary>
/// A purchase's outcome: "ok", or the reason it did not happen ("not_found", "own_listing", "not_enough_kinah", "invalid",
/// "other_world"). On "ok": what was bought, the Kinah paid, and the seller's payout mail (null for a bot listing).
/// </summary>
public sealed record MarketPurchase(string Outcome, MarketListing? Listing = null, long Quantity = 0, long Paid = 0, long SellerGets = 0,
    PostRecord? SellerPost = null)
{
    public bool Ok => Outcome == "ok";
}

/// <summary>A listing's outcome: "ok" with the listing, or the reason ("not_found", "soul_bound", "too_many", "invalid").</summary>
public sealed record MarketListingResult(string Outcome, MarketListing? Listing = null)
{
    public bool Ok => Outcome == "ok";
}

/// <summary>An expired listing taken off the market: a player's item went back to the seller's bag.</summary>
public sealed record MarketExpiry(MarketListing Listing);

/// <summary>Recent sales of one item: how many units sold and their average price each.</summary>
public sealed record MarketPriceHistory(int TemplateId, long Units, long AveragePrice, long LowestListed);

/// <summary>
/// The market's storage (OURS). Player operations take the verified account and check that it owns the character
/// (as <see cref="IGameplayStore"/> does); bot operations are server-side only. Inventory changes notify
/// pg_notify('aion_inventory', character) so a seller or buyer in the world sees them at once.
/// </summary>
public interface IMarketStore
{
    /// <summary>Listings whose item name contains <paramref name="text"/> (or of one template), cheapest first, for a world (null: all).</summary>
    Task<IReadOnlyList<MarketListing>> SearchAsync(string? text, int? templateId, int? worldId, int offset, int limit, CancellationToken ct = default);

    /// <summary>A character's own listings (null: the market bot's).</summary>
    Task<IReadOnlyList<MarketListing>> ListingsOfAsync(Guid? seller, CancellationToken ct = default);

    Task<MarketListing?> ListingAsync(long id, CancellationToken ct = default);

    /// <summary>Puts <paramref name="quantity"/> of a bag item (by instance) up for sale; the item waits in escrow.</summary>
    Task<MarketListingResult> ListAsync(VerifiedAccount account, Guid character, Guid instance, long quantity, long priceEach,
        TimeSpan duration, int maxListings, CancellationToken ct = default);

    /// <summary>A market bot listing (no item row; granted on purchase). Null when the template is not in the catalogue.</summary>
    Task<MarketListing?> ListForBotAsync(string sellerName, int templateId, long quantity, long priceEach, TimeSpan duration, CancellationToken ct = default);

    /// <summary>
    /// A player buys <paramref name="quantity"/> (0: all) of a listing: Kinah from the bag, the item to the bag, the seller's
    /// share (after <paramref name="taxPercent"/>) by mail. <paramref name="worldId"/> null: any world's listing.
    /// </summary>
    Task<MarketPurchase> BuyAsync(VerifiedAccount account, Guid character, long listingId, long quantity, double taxPercent, int? worldId,
        CancellationToken ct = default);

    /// <summary>The market bot buys a whole player listing (the item leaves the game; the seller is paid by mail).</summary>
    Task<MarketPurchase> BotBuyAsync(long listingId, double taxPercent, CancellationToken ct = default);

    /// <summary>A seller takes a listing back: its item returns to the bag. "ok", "not_found".</summary>
    Task<string> CancelAsync(VerifiedAccount account, Guid character, long listingId, CancellationToken ct = default);

    /// <summary>Removes listings past their expiry: player items go back to their sellers' bags, bot listings are dropped.</summary>
    Task<IReadOnlyList<MarketExpiry>> ExpireAsync(CancellationToken ct = default);

    /// <summary>Catalogue items whose name contains <paramref name="text"/> (or the template id), for "//ah" lookups.</summary>
    Task<IReadOnlyList<(int TemplateId, string Name)>> FindItemsAsync(string text, int limit, CancellationToken ct = default);

    /// <summary>Units sold and their average price over the window, and the cheapest current listing (0 when none).</summary>
    Task<MarketPriceHistory> PriceHistoryAsync(int templateId, TimeSpan window, CancellationToken ct = default);

    /// <summary>Kinah the market bot spent buying since <paramref name="since"/>.</summary>
    Task<long> BotSpentSinceAsync(DateTimeOffset since, CancellationToken ct = default);
}

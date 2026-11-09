using System.Collections.Concurrent;
using System.Globalization;
using Aion.Application.Gameplay;
using Aion.Application.Market;
using Aion.Core.Accounts;

namespace Aion.World;

/// <summary>
/// "//ah" in chat (Aion:Market, OURS; docs/MARKET.md): the auction house for every player with a stored character. The
/// client's Trade Post window needs a browse protocol nobody has captured yet, so the market is driven from chat:
/// search, more, buy, sell, mine, cancel, price. Inventory changes reach the client at once (the session's own diff).
/// </summary>
public sealed class MarketCommands(IMarketStore store, MarketOptions options, MarketPricing pricing, SharedWorld? world = null)
{
    public const int PageSize = 6;
    readonly ConcurrentDictionary<Guid, (string Text, int Page)> searches = new();

    public MarketOptions Options => options;

    public const string Help = "//ah search <item> | more | buy <#listing> [count] | sell <item> [count] [price|auto] | mine | cancel <#listing> | price <item>";

    public async Task<string> HandleAsync(GameSession actor, string args, CancellationToken ct)
    {
        if (!options.Enabled) return "The market is off on this server (Aion:Market:Enabled).";
        if (actor.Bot) return "Bots do not trade.";
        if (actor.Store is not { } session || actor.CharacterId is not { } character) return "The market needs a signed-in character (account logins).";
        var account = new VerifiedAccount(session.AccountId);
        int? worldId = options.CrossWorld ? null : session.WorldId;
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = words.Length > 0 ? words[0].ToLowerInvariant() : "help";
        var rest = string.Join(' ', words.Skip(1));
        try
        {
            switch (verb)
            {
                case "search" or "find" or "s" when rest.Length > 0:
                    searches[character] = (rest, 0);
                    return await PageAsync(rest, 0, worldId, ct);
                case "list" or "browse" when rest.Length == 0:
                    searches[character] = ("", 0);
                    return await PageAsync("", 0, worldId, ct);
                case "more" or "next":
                {
                    if (!searches.TryGetValue(character, out var last)) return "Search first: //ah search <item>.";
                    searches[character] = (last.Text, last.Page + 1);
                    return await PageAsync(last.Text, last.Page + 1, worldId, ct);
                }
                case "buy" or "b" when words.Length is 2 or 3:
                    return await BuyAsync(actor, account, character, words[1], words.Length == 3 ? words[2] : null, worldId, ct);
                case "sell" when rest.Length > 0:
                    return await SellAsync(actor, account, character, words[1..], ct);
                case "mine" or "my" or "listings":
                    return Mine(await store.ListingsOfAsync(character, ct));
                case "cancel" or "c" when words.Length == 2:
                {
                    if (ListingId(words[1]) is not { } id) return "Which listing? //ah cancel <#listing> (see //ah mine).";
                    if (await store.CancelAsync(account, character, id, ct) != "ok") return $"You have no listing #{id}.";
                    await RefreshAsync(actor, ct);
                    return $"Listing #{id} cancelled; the item is back in your bag.";
                }
                case "price" or "value" when rest.Length > 0:
                    return await PriceAsync(rest, ct);
                default:
                    return Help;
            }
        }
        catch (TimeoutException)
        {
            return "The game connection did not respond.";
        }
        catch (Exception error) when (error is System.Data.Common.DbException or InvalidOperationException or OverflowException or GameplayException)
        {
            return "The market could not do that right now. Try again in a moment.";
        }
    }

    static string Kinah(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture);

    static string Line(MarketListing l) =>
        $"#{l.Id} {l.ItemName}{(l.Enchanted ? " (enchanted)" : "")} x{l.Quantity} @ {Kinah(l.PriceEach)}{(l.Quantity > 1 ? $" ({Kinah(l.PriceEach * l.Quantity)})" : "")} by {l.SellerName}";

    async Task<string> PageAsync(string text, int page, int? worldId, CancellationToken ct)
    {
        // An item ID searches by template; anything else by name.
        var byId = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
        var rows = await store.SearchAsync(byId || text.Length == 0 ? null : text, byId ? id : null, worldId, page * PageSize, PageSize + 1, ct);
        if (rows.Count == 0) return page == 0 ? $"Nothing on the market matches \"{text}\"." : "No more results.";
        var shown = rows.Take(PageSize).Select(Line);
        return string.Join(" | ", shown) + (rows.Count > PageSize ? " | //ah more for the next page" : "") + " — //ah buy <#> [count]";
    }

    static long? ListingId(string text) =>
        long.TryParse(text.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    async Task<string> BuyAsync(GameSession actor, VerifiedAccount account, Guid character, string listingText, string? countText, int? worldId, CancellationToken ct)
    {
        if (ListingId(listingText) is not { } id) return "Which listing? //ah buy <#listing> [count] (see //ah search).";
        long count = 0;
        if (countText is not null && (!long.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1)) return "The count must be a whole number.";
        var purchase = await store.BuyAsync(account, character, id, count, options.SaleTaxPercent, worldId, ct);
        switch (purchase.Outcome)
        {
            case "ok":
                await RefreshAsync(actor, ct);
                if (purchase.Listing?.SellerCharacter is { } seller && purchase.SellerPost is { } post) Notify(seller, post);
                return $"Bought {purchase.Listing!.ItemName} x{purchase.Quantity} for {Kinah(purchase.Paid)} Kinah.";
            case "not_enough_kinah":
                return $"Not enough Kinah: that costs {Kinah((count == 0 ? purchase.Listing!.Quantity : count) * purchase.Listing!.PriceEach)}.";
            case "own_listing": return "That is your own listing (//ah cancel to take it back).";
            case "other_world": return "That listing is in the other world.";
            case "invalid": return $"Listing #{id} has only {purchase.Listing?.Quantity} left.";
            default: return $"Listing #{id} is gone (sold or expired).";
        }
    }

    void Notify(Guid seller, PostRecord post) => world?.FindOnline(seller)?.Send(GameSession.PostReceived(post));

    static async Task RefreshAsync(GameSession actor, CancellationToken ct)
    {
        try { await actor.InvokeAsync(async _ => (true, await actor.InventoryChangesAsync(ct))).WaitAsync(TimeSpan.FromSeconds(10), ct); }
        catch (TimeoutException) { }   // the inventory notification catches up
    }

    /// <summary>"150", "1.5k", "2m": Kinah; null when not a price.</summary>
    public static long? ParseKinah(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace(",", "");
        var scale = t.EndsWith('k') ? 1_000m : t.EndsWith('m') ? 1_000_000m : t.EndsWith('b') ? 1_000_000_000m : 1m;
        if (scale != 1m) t = t[..^1];
        return decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) && n > 0 && n * scale <= long.MaxValue
            && decimal.Truncate(n * scale) == n * scale ? (long)(n * scale) : null;
    }

    /// <summary>
    /// "sell" arguments read from the end: [item words...] [count] [price each|auto]. One number is the count ("2k" alone is a
    /// price); no price or "auto" asks for the suggested price. Null when no item words are left.
    /// </summary>
    public static (string Item, long? Count, long? Price)? ParseSell(IReadOnlyList<string> words)
    {
        var list = words.ToList();
        var auto = list.Count > 1 && list[^1].Equals("auto", StringComparison.OrdinalIgnoreCase);
        if (auto) list.RemoveAt(list.Count - 1);
        var tail = new List<string>();
        while (list.Count > 1 && tail.Count < (auto ? 1 : 2) && ParseKinah(list[^1]) is not null) { tail.Insert(0, list[^1]); list.RemoveAt(list.Count - 1); }
        static long? Whole(string t) => long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        long? count = null, price = null;
        if (tail.Count == 2)
        {
            if (Whole(tail[0]) is not { } c) return null;
            (count, price) = (c, ParseKinah(tail[1]));
        }
        else if (tail.Count == 1)
        {
            if (Whole(tail[0]) is { } c) count = c;
            else if (auto) return null;
            else price = ParseKinah(tail[0]);
        }
        return list.Count == 0 ? null : (string.Join(' ', list), count, price);
    }

    async Task<string> SellAsync(GameSession actor, VerifiedAccount account, Guid character, string[] words, CancellationToken ct)
    {
        if (ParseSell(words) is not { } request) return "//ah sell <item> [count] [price each|auto], e.g. //ah sell Iron Ore 20 150";
        var bag = await actor.InvokeAsync(_ => (actor.BagItems(), new List<byte[]>())).WaitAsync(TimeSpan.FromSeconds(10), ct);
        var templates = bag.Select(i => i.TemplateId).Where(i => i != GameSession.KinaItemId).ToHashSet();
        var matches = (await store.FindItemsAsync(request.Item, 50, ct)).Where(m => templates.Contains(m.TemplateId)).ToList();
        if (matches.Count == 0) return $"You have no \"{request.Item}\" in your bag.";
        var exact = matches.Where(m => m.Name.Equals(request.Item, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) matches = exact;
        if (matches.Select(m => m.TemplateId).Distinct().Count() > 1)
            return "Which one? " + string.Join(", ", matches.Take(6).Select(m => $"{m.Name} ({m.TemplateId})")) + " — you can sell by item ID too.";
        var (template, name) = matches[0];
        var stacks = bag.Where(i => i.TemplateId == template && !IsSoulBound(i)).OrderByDescending(i => i.Quantity).ToList();
        if (stacks.Count == 0) return $"Your {name} is soul-bound and cannot be sold.";
        var stack = stacks[0];
        var count = request.Count ?? stack.Quantity;
        if (count < 1) return "The count must be at least 1.";
        if (count > stack.Quantity)
            return stacks.Count > 1 ? $"Your largest stack of {name} has {stack.Quantity}; list one stack at a time." : $"You have only {stack.Quantity} {name}.";
        var price = request.Price;
        if (price is null)
        {
            var suggested = await SuggestedPriceAsync(template, ct);
            if (suggested is null) return $"No price is known for {name}: give one, e.g. //ah sell {request.Item} {count} 1000";
            price = suggested;
        }
        if (price > options.MaxPriceEach) return $"The highest price each is {Kinah(options.MaxPriceEach)} Kinah.";
        var result = await store.ListAsync(account, character, stack.InstanceId, count, price.Value, options.ListingDuration, options.MaxListingsPerCharacter, ct);
        switch (result.Outcome)
        {
            case "ok":
                await RefreshAsync(actor, ct);
                var tax = options.SaleTaxPercent > 0 ? string.Create(CultureInfo.InvariantCulture, $", {options.SaleTaxPercent}% market tax on sale") : "";
                return $"Listed #{result.Listing!.Id}: {name} x{count} @ {Kinah(price.Value)} each for {options.ListingHours:0.#} hours{tax}. Payment arrives by mail.";
            case "too_many": return $"You already have {options.MaxListingsPerCharacter} listings (//ah mine).";
            case "soul_bound": return $"Your {name} is soul-bound and cannot be sold.";
            default: return "That item could not be listed (it may have moved). Try again.";
        }
    }

    static bool IsSoulBound(ItemState item) =>
        item.Upgrades.ValueKind == System.Text.Json.JsonValueKind.Object && item.Upgrades.TryGetProperty("soul_bind", out _);

    /// <summary>
    /// A price each for "auto": just under the cheapest listing, else the recent average, else the bot's price; never below
    /// the item's vendor value (a troll listing at 2 Kinah does not drag everyone's auto price down). Null when nothing prices it.
    /// </summary>
    async Task<long?> SuggestedPriceAsync(int template, CancellationToken ct)
    {
        var history = await store.PriceHistoryAsync(template, TimeSpan.FromDays(7), ct);
        var floor = pricing.Value(template) ?? 1;
        long? price = history.LowestListed > 1 ? history.LowestListed - 1
            : history.Units > 0 && history.AveragePrice > 0 ? history.AveragePrice
            : options.Seller.FixedPrice(template) is { } fixedPrice ? fixedPrice
            : pricing.Value(template) is { } value ? (long)Math.Round(value * options.Seller.PriceMultiplier) : null;
        return price is { } p ? Math.Max(p, floor) : null;
    }

    async Task<string> PriceAsync(string text, CancellationToken ct)
    {
        var matches = await store.FindItemsAsync(text, 6, ct);
        if (matches.Count == 0) return $"No item matches \"{text}\".";
        var exact = matches.Where(m => m.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        var (template, name) = (exact.Count > 0 ? exact : matches)[0];
        var history = await store.PriceHistoryAsync(template, TimeSpan.FromDays(7), ct);
        var parts = new List<string> { $"{name} ({template})" };
        if (pricing.Value(template) is { } value) parts.Add($"vendor value {Kinah(value)}");
        if (pricing.BuyerLimit(template, options.Buyer, options.Seller) is { } limit && options.Buyer.Enabled) parts.Add($"the market bot buys at up to {Kinah(limit)} each");
        parts.Add(history.LowestListed > 0 ? $"cheapest listing {Kinah(history.LowestListed)}" : "none listed");
        parts.Add(history.Units > 0 ? $"{history.Units} sold this week at {Kinah(history.AveragePrice)} on average" : "no sales this week");
        if (matches.Count > 1 && exact.Count == 0) parts.Add("also: " + string.Join(", ", matches.Skip(1).Take(3).Select(m => m.Name)));
        return string.Join("; ", parts) + ".";
    }

    static string Mine(IReadOnlyList<MarketListing> listings) =>
        listings.Count == 0 ? "You have no listings. //ah sell <item> [count] [price] puts one up."
            : $"Your listings ({listings.Count}): " + string.Join(" | ", listings.Take(12).Select(l =>
                $"#{l.Id} {l.ItemName} x{l.Quantity} @ {Kinah(l.PriceEach)}, {Math.Max(0, (int)(l.ExpiresAt - DateTimeOffset.UtcNow).TotalHours)} h left"))
              + (listings.Count > 12 ? $" | and {listings.Count - 12} more" : "");
}

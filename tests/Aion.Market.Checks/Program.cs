using Aion.Application.Gameplay;
using Aion.Application.Market;
using Aion.Core.Accounts;
using Aion.Infrastructure.Postgres;
using Aion.World;
using Npgsql;

// Market checks (docs/MARKET.md). The pure part (pricing, parsing, options, the bot against an in-memory store) always runs;
// the storage part runs against a disposable database from tools/check_market.py (AION_TEST_DATABASE).
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("Market check failed: " + name);
    checks++;
}

// --- Parsing ---
Check(MarketCommands.ParseKinah("150") == 150 && MarketCommands.ParseKinah("1.5k") == 1500 && MarketCommands.ParseKinah("2m") == 2_000_000
      && MarketCommands.ParseKinah("1,000") == 1000 && MarketCommands.ParseKinah("0") is null && MarketCommands.ParseKinah("ore") is null
      && MarketCommands.ParseKinah("1.0005k") is null, "Kinah amounts");
Check(MarketCommands.ParseSell(["Iron", "Ore", "20", "150"]) == ("Iron Ore", 20L, 150L), "sell: item, count, price");
Check(MarketCommands.ParseSell(["Iron", "Ore", "20"]) == ("Iron Ore", 20L, null), "sell: one number is the count");
Check(MarketCommands.ParseSell(["Iron", "Ore", "2k"]) == ("Iron Ore", null, 2000L), "sell: a priced number alone is the price");
Check(MarketCommands.ParseSell(["Iron", "Ore", "20", "auto"]) == ("Iron Ore", 20L, null), "sell: auto price");
Check(MarketCommands.ParseSell(["Iron", "Ore"]) == ("Iron Ore", null, null), "sell: the whole stack at the suggested price");
Check(MarketCommands.ParseSell(["610710006", "5"]) == ("610710006", 5L, null), "sell: by item ID");
Check(MarketCommands.ParseSell(["Ore", "1.5", "100"]) == ("Ore 1.5", 100L, null) && MarketCommands.ParseSell(["Ore", "1.5k", "100"]) is null, "sell: a count must be whole");

// --- Options ---
var defaults = new MarketOptions().Validated();
Check(defaults.Enabled && defaults.Seller.Enabled && defaults.Buyer.Enabled, "market and both bots on by default");
foreach (var bad in new[]
{
    new MarketOptions { SaleTaxPercent = 101 }, new MarketOptions { ListingHours = 0 },
    new MarketOptions { Seller = new() { PriceMultiplier = 0 } }, new MarketOptions { Seller = new() { Prices = new() { ["ore"] = 5 } } },
    new MarketOptions { Buyer = new() { BuyChancePercent = 120 } }, new MarketOptions { Seller = new() { Name = "" } },
})
{
    try { bad.Validated(); throw new Exception("Market check failed: invalid options accepted"); }
    catch (InvalidOperationException error) { Check(error.Message.StartsWith("Aion:Market:", StringComparison.Ordinal), "invalid options refused: " + error.Message); }
}

// --- Pricing (synthetic) ---
// 501 ore (vendor 10), 502 dust (vendor 4), 700 crafted bar = 3 ore + 2 dust + fee 6 per 2 bars = 22 > vendor 5; 601 sword (equipment, vendor 300).
var vendor = new Dictionary<int, long> { [501] = 10, [502] = 4, [700] = 5, [601] = 300 };
var recipes = new[] { new CraftTables.Recipe(1, "Blacksmith", 1, 1, true, 700, 2, [(501, 3), (502, 2)], 6, 0) };
var sword = new ItemInfo("Sword", 30, 30, 0, []);
var pricing = new MarketPricing(id => vendor.TryGetValue(id, out var v) ? v : null, recipes, [(601, sword), (602, new ItemInfo("Sword", 80, 80, 0, []))]);
Check(pricing.Value(501) == 10 && pricing.Value(700) == 22 && pricing.Value(999) is null, "values: vendor price, crafted from materials, unknown");
Check(pricing.MaterialIds.SequenceEqual([501, 502]) && pricing.CraftedIds.SequenceEqual([700]) && pricing.IsEquipment(601), "pools");
var seller = new MarketSellerOptions { PriceMultiplier = 3, PriceVariancePercent = 0, Equipment = true, MaxItemLevel = 50, Exclude = [502], Prices = new() { ["501"] = 77 } };
Check(pricing.SellerPool(seller).SequenceEqual([501, 601, 700]), "seller pool: level range, exclude, priced items only");
Check(pricing.SellerPrice(700, seller, new Random(1)) == 66 && pricing.SellerPrice(501, seller, new Random(1)) == 77, "seller price: value x multiplier, fixed price");
var varied = Enumerable.Range(0, 200).Select(i => pricing.SellerPrice(700, seller with { PriceVariancePercent = 20 }, new Random(i))!.Value).ToList();
Check(varied.Min() >= 52 && varied.Max() <= 80 && varied.Distinct().Count() > 5, "seller price varies within the variance");
Check(pricing.BuyerLimit(700, new MarketBuyerOptions { MaxPriceMultiplier = 2 }, seller) == 44
      && pricing.BuyerLimit(999, new MarketBuyerOptions(), seller) is null
      && pricing.BuyerLimit(999, new MarketBuyerOptions { UnpricedMaxPriceEach = 9 }, seller) == 9
      && pricing.BuyerLimit(501, new MarketBuyerOptions(), seller) == 76, "buyer limits");
// No resale loop: the buyer always pays less than the seller bot's lowest price and an NPC merchant's Kinah price.
var shopPricing = new MarketPricing(id => vendor.TryGetValue(id, out var v) ? v : null, recipes, [], new Dictionary<int, long> { [700] = 30 }, new HashSet<int> { 501 });
var greedy = new MarketBuyerOptions { MaxPriceMultiplier = 100 };
var spread = new MarketSellerOptions { PriceMultiplier = 3, PriceVariancePercent = 20 };
Check(pricing.BuyerLimit(700, greedy, spread) == 51, "buyer stays below the seller bot's lowest price");
Check(shopPricing.BuyerLimit(700, greedy, spread) == 29 && shopPricing.BuyerLimit(501, greedy, spread) is null, "buyer stays below merchant prices; no merchant-currency items");
var embeddedPricing = MarketPricing.Embedded;
Check(embeddedPricing.ShopPrice(630840002) == 5000 && embeddedPricing.BuyerLimit(630840002, new MarketBuyerOptions { MaxPriceMultiplier = 100 }, new MarketSellerOptions()) is null or < 5000,
      "the reported merchant item cannot be resold to the buyer at a profit");

// --- Reference prices (Aion:Market:PriceFile) ---
var csv = MarketReferencePrices.Parse("# NA East Siel, noted by hand\nitem,price\n501,40\nTest Ore Dust, 1.5k\n", json: false);
Check(csv.ById[501] == 40 && csv.ByName["test ore dust"] == 1500, "reference CSV: IDs, names, k suffix, header and comments");
var jsonRefs = MarketReferencePrices.Parse("""{"source": "NA East Siel", "prices": {"700": 90, "Test Sword": "2m"}}""", json: true);
Check(jsonRefs.ById[700] == 90 && jsonRefs.ByName["Test Sword"] == 2_000_000, "reference JSON");
try { MarketReferencePrices.Parse("item,price\n501,0\n", json: false); throw new Exception("Market check failed: zero price accepted"); }
catch (InvalidOperationException) { Check(true, "reference prices must be at least 1"); }
var refPricing = new MarketPricing(id => vendor.TryGetValue(id, out var v) ? v : null, recipes, []);
refPricing.SetReferences(new Dictionary<int, long> { [700] = 100, [888] = 500 });
var refSeller = new MarketSellerOptions { PriceVariancePercent = 10 };
var refPrices = Enumerable.Range(0, 100).Select(i => refPricing.SellerPrice(700, refSeller, new Random(i))!.Value).ToList();
Check(refPrices.Min() >= 90 && refPrices.Max() <= 110, "seller bot lists around the reference price");
Check(refPricing.BuyerLimit(700, new MarketBuyerOptions { ReferenceFraction = 0.8 }, refSeller) == 80
      && refPricing.BuyerLimit(700, new MarketBuyerOptions { ReferenceFraction = 1 }, refSeller) == 89, "buyer pays at most 80% of reference and below the seller's lowest");
Check(refPricing.SellerPool(new MarketSellerOptions()).Contains(888) && !refPricing.SellerPool(new MarketSellerOptions { ReferenceItems = false }).Contains(888),
      "reference items join the seller pool");

var bundled = MarketReferencePrices.Embedded();
Check(bundled.ByName.Count >= 50 && bundled.ByName["Ruby Powder"] == 1645 && bundled.ById.Count == 0, "bundled reference table");

// --- Pricing (embedded game data) ---
var embedded = MarketPricing.Embedded;
var pool = embedded.SellerPool(defaults.Seller);
Check(pool.Count > 500 && !pool.Contains(GameSession.KinaItemId), $"embedded seller pool ({pool.Count} items)");
Check(pool.All(id => embedded.SellerPrice(id, defaults.Seller, new Random(id)) is > 0), "every pool item has a price");
Check(embedded.MaterialIds.Count > 100 && embedded.MaterialIds.All(id => !embedded.IsEquipment(id)), "materials are not equipment");

// --- The bot against an in-memory store ---
var memory = new MemoryMarket();
var player = Guid.NewGuid();
var now = DateTimeOffset.UtcNow;
var options = new MarketOptions
{
    Seller = new() { TargetListings = 10, ListingsPerCycle = 4, MaxListingsPerItem = 1, PriceVariancePercent = 0, Equipment = true, Exclude = [502] },
    Buyer = new() { BuyChancePercent = 100, MaxPriceMultiplier = 3, MinListingAgeMinutes = 10, MaxPurchasesPerCycle = 5, KinahPerDay = 500 },
};
var mails = new List<(Guid, PostRecord)>();
var bot = new MarketBot(memory, options, pricing, new Random(7), (s, p) => mails.Add((s, p)));
Check(await bot.SellAsync() == 3 && memory.Listings.Count == 3, "seller: lists each pool item once (cap 1 per item)");
Check(await bot.SellAsync() == 0, "seller: no item over its per-item cap");
Check(memory.Listings.All(l => l.ByBot && l.SellerName == "Market Trader" && (l.TemplateId != 601 || l.Quantity == 1)), "seller: bot listings, equipment one at a time");
memory.Add(new MarketListing(100, player, "Alice", 1204, 700, "Bar", 2, 60, now.AddMinutes(-30), now.AddDays(1)));   // 60 <= 22 x 3: bought
memory.Add(new MarketListing(101, player, "Alice", 1204, 700, "Bar", 1, 66, now.AddMinutes(-30), now.AddDays(1)));   // 66 = the seller bot's price: too dear
memory.Add(new MarketListing(102, player, "Alice", 1204, 700, "Bar", 1, 10, now.AddMinutes(-1), now.AddDays(1)));    // too new
memory.Add(new MarketListing(103, player, "Alice", 1204, 999, "Junk", 1, 1, now.AddMinutes(-30), now.AddDays(1)));   // unpriced
memory.Add(new MarketListing(104, player, "Alice", 1204, 501, "Ore", 50, 30, now.AddMinutes(-30), now.AddDays(1)));  // 1500 over the day's budget
var bought = await bot.BuyAsync(now);
Check(bought.Select(b => b.Listing!.Id).SequenceEqual([100L]) && mails.Count == 1 && mails[0].Item1 == player, "buyer: fair, old enough, priced, within budget; seller notified");
memory.Spent = 480;
memory.Add(new MarketListing(105, player, "Alice", 1204, 501, "Ore", 3, 10, now.AddMinutes(-30), now.AddDays(1)));
Check((await bot.BuyAsync(now)).Count == 0, "buyer: the daily budget holds");
var noChance = new MarketBot(memory, options with { Buyer = options.Buyer with { BuyChancePercent = 0 } }, pricing, new Random(1));
memory.Spent = 0;
Check((await noChance.BuyAsync(now)).Count == 0, "buyer: 0% chance buys nothing");
memory.Add(new MarketListing(106, player, "Alice", 1204, 501, "Ore", long.MaxValue / 2, 5, now.AddMinutes(-30), now.AddDays(1)));
var unbudgeted = new MarketBot(memory, options with { Buyer = options.Buyer with { KinahPerDay = 0 } }, pricing, new Random(1));
Check((await unbudgeted.BuyAsync(now)).All(b => b.Listing!.Id != 106), "buyer: a total that overflows is skipped");
Console.WriteLine($"{checks} market logic checks passed.");

// --- Storage (PostgreSQL) ---
var connectionString = Environment.GetEnvironmentVariable("AION_TEST_DATABASE");
if (connectionString is null)
{
    Console.WriteLine("Storage checks skipped: run tools/check_market.py for the database part.");
    return;
}
if (!new NpgsqlConnectionStringBuilder(connectionString).Database!.StartsWith("aion_repository_test_", StringComparison.Ordinal))
    throw new InvalidOperationException("Refusing to run outside a disposable test database.");
var accounts = Environment.GetEnvironmentVariable("AION_TEST_ACCOUNTS")!.Split(',').Select(a => new VerifiedAccount(Guid.Parse(a))).ToArray();
var characters = Environment.GetEnvironmentVariable("AION_TEST_CHARACTERS")!.Split(',').Select(Guid.Parse).ToArray();
var (sellerAcc, buyerAcc, poorAcc, farAcc) = (accounts[0], accounts[1], accounts[2], accounts[3]);
var (sellerId, buyerId, poorId, farId) = (characters[0], characters[1], characters[2], characters[3]);
const int Kinah = GameSession.KinaItemId, Ore = 501, Dust = 502, Sword = 601;
var storeChecks = checks;
await using var source = NpgsqlDataSource.Create(connectionString);
var store = new PostgresMarketStore(source);

async Task<Guid> Give(Guid character, int template, long quantity, string upgrades = "{}")
{
    await using var c = await source.OpenConnectionAsync();
    await using var q = new NpgsqlCommand("INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,upgrades) VALUES($1,'market-test',$2,$3,$4::jsonb) RETURNING instance_id", c);
    q.Parameters.AddWithValue(character); q.Parameters.AddWithValue(template); q.Parameters.AddWithValue(quantity); q.Parameters.AddWithValue(upgrades);
    return (Guid)(await q.ExecuteScalarAsync())!;
}
async Task<long> Held(Guid character, int template, string location = "bag")
{
    await using var c = await source.OpenConnectionAsync();
    await using var q = new NpgsqlCommand("SELECT COALESCE(sum(quantity),0)::bigint FROM game.item_instances WHERE character_id=$1 AND template_id=$2 AND location=$3", c);
    q.Parameters.AddWithValue(character); q.Parameters.AddWithValue(template); q.Parameters.AddWithValue(location);
    return (long)(await q.ExecuteScalarAsync())!;
}
async Task<List<string>> Mail(Guid character)
{
    await using var c = await source.OpenConnectionAsync();
    await using var q = new NpgsqlCommand("SELECT msg_params||' '||attachments::text FROM game.character_posts WHERE character_id=$1 ORDER BY post_id", c);
    q.Parameters.AddWithValue(character);
    await using var r = await q.ExecuteReaderAsync();
    var rows = new List<string>();
    while (await r.ReadAsync()) rows.Add(r.GetString(0));
    return rows;
}
var hours = TimeSpan.FromHours(48);
var ore = await Give(sellerId, Ore, 50);
var swordItem = await Give(sellerId, Sword, 1, """{"enchant": 5}""");
var bound = await Give(sellerId, Dust, 3, """{"soul_bind": 1}""");
var sellerKinah = await Give(sellerId, Kinah, 1000);
await Give(buyerId, Kinah, 60_000);
await Give(buyerId, Kinah, 40_000);   // two stacks: payment takes from both
await Give(poorId, Kinah, 10);
await Give(farId, Kinah, 100_000);

var listed = await store.ListAsync(sellerAcc, sellerId, ore, 20, 150, hours, 0);
Check(listed.Ok && listed.Listing is { Quantity: 20, PriceEach: 150, SellerName: "Seller", WorldId: 1204, ItemName: "Test Iron Ore", Enchanted: false }, "list part of a stack");
Check(await Held(sellerId, Ore) == 30 && await Held(sellerId, Ore, "market") == 20, "listed units wait in escrow");
Check((await store.ListAsync(buyerAcc, sellerId, ore, 1, 1, hours, 0)).Outcome == "not_found", "another account cannot list a seller's item");
Check((await store.ListAsync(sellerAcc, sellerId, bound, 1, 1, hours, 0)).Outcome == "soul_bound", "soul-bound items stay");
Check((await store.ListAsync(sellerAcc, sellerId, sellerKinah, 10, 1, hours, 0)).Outcome == "invalid", "Kinah is not for sale");
Check((await store.ListAsync(sellerAcc, sellerId, ore, 31, 1, hours, 0)).Outcome == "invalid", "no more than held");
Check((await store.ListAsync(sellerAcc, sellerId, ore, 1, 1, hours, 1)).Outcome == "too_many", "listing cap");
var found = await store.SearchAsync("iron", null, 1204, 0, 10);
Check(found.Count == 1 && found[0].Id == listed.Listing!.Id && (await store.SearchAsync("iron", null, 1205, 0, 10)).Count == 0, "search by name, per world");
Check((await store.SearchAsync(null, Ore, null, 0, 10)).Count == 1 && (await store.SearchAsync("100%", null, null, 0, 10)).Count == 0, "search by template; wildcards are literal");

var five = await store.BuyAsync(buyerAcc, buyerId, listed.Listing!.Id, 5, 5, 1204);
Check(five is { Ok: true, Quantity: 5, Paid: 750, SellerGets: 712 } && five.SellerPost is { } payout && payout.Attachments.Single() == (Kinah, 712), "buy part: paid, 5% tax, payout mail");
Check(await Held(buyerId, Ore) == 5 && await Held(buyerId, Kinah) == 99_250 && await Held(sellerId, Ore, "market") == 15, "buy part: item and Kinah moved");
Check((await store.ListingAsync(listed.Listing.Id))!.Quantity == 15, "buy part: listing reduced");
Check((await Mail(sellerId)).Single().Contains("Test Iron Ore x5 sold for 750", StringComparison.Ordinal), "payout mail text");
Check((await store.BuyAsync(sellerAcc, sellerId, listed.Listing.Id, 1, 5, 1204)).Outcome == "own_listing", "no buying your own listing");
Check((await store.BuyAsync(poorAcc, poorId, listed.Listing.Id, 1, 5, 1204)).Outcome == "not_enough_kinah" && await Held(poorId, Kinah) == 10, "not enough Kinah: nothing taken");
Check((await store.BuyAsync(farAcc, farId, listed.Listing.Id, 1, 5, 1205)).Outcome == "other_world", "other world refused");
Check((await store.BuyAsync(farAcc, farId, listed.Listing.Id, 1, 5, null)).Ok, "cross-world when the market allows it");
Check((await store.BuyAsync(buyerAcc, buyerId, listed.Listing.Id, 99, 5, 1204)).Outcome == "invalid", "no more than listed");
Check((await store.BuyAsync(buyerAcc, buyerId, listed.Listing.Id, 1, 5, 1204, default)).Ok && await Held(buyerId, Ore) == 6, "buy one more");
Check((await store.ListingsOfAsync(sellerId)).Single().Quantity == 13, "my listings");
Check(await store.CancelAsync(buyerAcc, buyerId, listed.Listing.Id) == "not_found", "only the seller cancels");
Check(await store.CancelAsync(sellerAcc, sellerId, listed.Listing.Id) == "ok" && await Held(sellerId, Ore) == 43 && await Held(sellerId, Ore, "market") == 0,
      "cancel: the rest back in the bag");

var swordListing = await store.ListAsync(sellerAcc, sellerId, swordItem, 1, 5000, hours, 0);
Check(swordListing.Ok && swordListing.Listing!.Enchanted, "enchanted item listed as itself");
var swordBuy = await store.BuyAsync(buyerAcc, buyerId, swordListing.Listing!.Id, 0, 0, 1204);
await using (var c = await source.OpenConnectionAsync())
await using (var q = new NpgsqlCommand("SELECT character_id,location,upgrades->>'enchant' FROM game.item_instances WHERE instance_id=$1", c))
{
    q.Parameters.AddWithValue(swordItem);
    await using var r = await q.ExecuteReaderAsync();
    Check(swordBuy is { Ok: true, SellerGets: 5000 } && await r.ReadAsync() && r.GetGuid(0) == buyerId && r.GetString(1) == "bag" && r.GetString(2) == "5",
          "the buyer gets that very sword (enchant kept); no tax at 0%");
}

var botListing = await store.ListForBotAsync("Market Trader", Dust, 10, 100, hours);
Check(botListing is { ByBot: true, WorldId: null, SellerName: "Market Trader" } && await store.ListForBotAsync("Market Trader", 4242, 1, 1, hours) is null, "bot listing (catalogue items only)");
var fromBot = await store.BuyAsync(farAcc, farId, botListing!.Id, 4, 5, 1205);
Check(fromBot is { Ok: true, Paid: 400, SellerPost: null } && await Held(farId, Dust) == 4, "bot listings show in every world; no payout");
Check((await store.BotBuyAsync(botListing.Id, 5)).Outcome == "own_listing", "the bot never buys its own listing");

var forBot = await store.ListAsync(sellerAcc, sellerId, ore, 10, 40, hours, 0);
var spentBefore = await store.BotSpentSinceAsync(DateTimeOffset.UtcNow.AddHours(-1));
var botBuy = await store.BotBuyAsync(forBot.Listing!.Id, 5);
Check(botBuy is { Ok: true, Paid: 400, SellerGets: 380 } && await Held(sellerId, Ore, "market") == 0 && await Held(sellerId, Ore) == 33, "bot buyer: item leaves the game");
Check(await store.BotSpentSinceAsync(DateTimeOffset.UtcNow.AddHours(-1)) == spentBefore + 400, "bot spending recorded");
var history = await store.PriceHistoryAsync(Ore, TimeSpan.FromDays(7));
Check(history.Units == 17 && history.AveragePrice == (750 + 150 + 150 + 400) / 17, "price history");

var expiring = await store.ListAsync(sellerAcc, sellerId, ore, 3, 10, TimeSpan.FromMilliseconds(1), 0);
var botExpiring = await store.ListForBotAsync("Market Trader", Ore, 1, 1, TimeSpan.FromMilliseconds(1));
await Task.Delay(50);
Check((await store.BuyAsync(buyerAcc, buyerId, expiring.Listing!.Id, 1, 5, 1204)).Outcome == "not_found", "expired listings cannot be bought");
var expired = await store.ExpireAsync();
Check(expired.Select(e => e.Listing.Id).Order().SequenceEqual(new[] { expiring.Listing.Id, botExpiring!.Id }.Order()) && await Held(sellerId, Ore) == 33,
      "expiry: player item back, bot listing dropped");
Check((await store.FindItemsAsync("ore", 10)).Select(i => i.TemplateId).SequenceEqual([Ore, Dust]) && (await store.FindItemsAsync("601", 10)).Single().Name == "Test Sword",
      "catalogue lookups by name and ID");

// The bot end to end on the database: the seller stocks, the buyer buys a fair player listing.
var dbPricing = new MarketPricing(id => id switch { Ore => 10, Dust => 4, _ => null }, [], []);
var dbOptions = new MarketOptions { Seller = new() { TargetListings = 2, Include = [Ore, Dust], Materials = false, Crafted = false },
    Buyer = new() { BuyChancePercent = 100, MinListingAgeMinutes = 0 } };
var dbBot = new MarketBot(store, dbOptions, dbPricing, new Random(3));
var fair = await store.ListAsync(sellerAcc, sellerId, ore, 2, 20, hours, 0);   // value 10: seller bot asks 24+, buyer pays up to 20
await dbBot.TickAsync(DateTimeOffset.UtcNow);
Check((await store.ListingsOfAsync(null)).Count == 2, "bot seller stocked the database market");
Check(await store.ListingAsync(fair.Listing!.Id) is null && (await Mail(sellerId)).Count == 6, "bot buyer bought the fair listing and mailed the seller");
var namedRefs = new MarketPricing(_ => null, [], []);
var refBot = new MarketBot(store, new MarketOptions(), namedRefs, references: (new Dictionary<int, long> { [Sword] = 7000 }, new(StringComparer.OrdinalIgnoreCase) { ["test iron ore"] = 300, ["No Such Thing"] = 5 }));
await refBot.ResolveReferencesAsync();
Check(namedRefs.Reference(Ore) == 300 && namedRefs.Reference(Sword) == 7000 && namedRefs.ReferenceCount == 2, "reference names matched against the catalogue");
var players = await store.PlayerListingsAsync(DateTimeOffset.UtcNow, 10);
Check(players.All(l => !l.ByBot), "the buyer bot's query skips the bot's own listings");
await using (var c = await source.OpenConnectionAsync())
await using (var q = new NpgsqlCommand("SELECT min(expire_at) > now() + interval '9 years' FROM game.character_posts WHERE msg_params LIKE 'Market sale|%'", c))
    Check((bool)(await q.ExecuteScalarAsync())!, "payout mail does not expire with the Kinah unclaimed");
Console.WriteLine($"{checks - storeChecks} market storage checks passed.");

/// <summary>An in-memory market for the bot checks.</summary>
sealed class MemoryMarket : IMarketStore
{
    public List<MarketListing> Listings { get; } = [];
    public long Spent { get; set; }
    long next = 1;
    public void Add(MarketListing listing) => Listings.Add(listing);

    public Task<IReadOnlyList<MarketListing>> SearchAsync(string? text, int? templateId, int? worldId, int offset, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarketListing>>(Listings.Skip(offset).Take(limit).ToList());
    public Task<IReadOnlyList<MarketListing>> PlayerListingsAsync(DateTimeOffset listedBefore, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarketListing>>(Listings.Where(l => !l.ByBot && l.ListedAt <= listedBefore).Take(limit).ToList());
    public Task<IReadOnlyList<MarketListing>> ListingsOfAsync(Guid? seller, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarketListing>>(Listings.Where(l => l.SellerCharacter == seller).ToList());
    public Task<MarketListing?> ListingAsync(long id, CancellationToken ct = default) => Task.FromResult(Listings.FirstOrDefault(l => l.Id == id));
    public Task<MarketListingResult> ListAsync(VerifiedAccount account, Guid character, Guid instance, long quantity, long priceEach, TimeSpan duration, int maxListings, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<MarketListing?> ListForBotAsync(string sellerName, int templateId, long quantity, long priceEach, TimeSpan duration, CancellationToken ct = default)
    {
        var listing = new MarketListing(next++, null, sellerName, null, templateId, templateId.ToString(System.Globalization.CultureInfo.InvariantCulture), quantity, priceEach,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + duration);
        Listings.Add(listing);
        return Task.FromResult<MarketListing?>(listing);
    }
    public Task<MarketPurchase> BuyAsync(VerifiedAccount account, Guid character, long listingId, long quantity, double taxPercent, int? worldId, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<MarketPurchase> BotBuyAsync(long listingId, double taxPercent, CancellationToken ct = default)
    {
        if (Listings.FirstOrDefault(l => l.Id == listingId) is not { ByBot: false } l) return Task.FromResult(new MarketPurchase("not_found"));
        Listings.Remove(l);
        var total = l.Quantity * l.PriceEach;
        Spent += total;
        var share = PostgresMarketStore.SellerShare(total, taxPercent);
        var post = new PostRecord(1, 1, 2, 106, "Market sale|sold", false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), [(GameSession.KinaItemId, share)], 0);
        return Task.FromResult(new MarketPurchase("ok", l, l.Quantity, total, share, post));
    }
    public Task<string> CancelAsync(VerifiedAccount account, Guid character, long listingId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<MarketExpiry>> ExpireAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MarketExpiry>>([]);
    public Task<IReadOnlyList<(int TemplateId, string Name)>> FindItemsAsync(string text, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(int, string)>>([]);
    public Task<MarketPriceHistory> PriceHistoryAsync(int templateId, TimeSpan window, CancellationToken ct = default) => Task.FromResult(new MarketPriceHistory(templateId, 0, 0, 0));
    public Task<long> BotSpentSinceAsync(DateTimeOffset since, CancellationToken ct = default) => Task.FromResult(Spent);
}

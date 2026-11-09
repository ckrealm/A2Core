# Market and market bot (`//ah`)

An auction house for a small server, in the spirit of the AH bots of WoW private servers (AzerothCore `mod-ah-bot`):

- **Players** list items for Kinah and buy each other's listings with `//ah` in chat.
- **The seller bot** ("Market Trader") keeps the market stocked: crafting materials and crafted items, optionally equipment,
  at a multiple of what a merchant pays for them.
- **The buyer bot** buys players' fairly priced listings after a while, so selling works even when only a few friends play.

Everything here is our own design (OURS); nothing is observed from the official Trade Post. The client's Trade Post window
(`TradeMain`, `TradeSell_RQ` 0x3B69 and friends) needs a browse/search protocol that nobody has captured, so the market is
driven from chat for now. Wiring the native window is future work once the browse messages are recorded.

## Commands

| Command | What it does |
|---|---|
| `//ah search <item>` | Listings whose item name contains the text (or an item ID), cheapest first, 6 per page |
| `//ah list` | Everything on the market |
| `//ah more` | Next page of the last search |
| `//ah buy <#listing> [count]` | Buys the listing (all of it, or `count` units). Kinah leaves your bag, the item arrives at once |
| `//ah sell <item> [count] [price each\|auto]` | Lists from your largest stack of that item. No price or `auto`: just under the cheapest listing, else the recent average, else the bot's price, never below the item's vendor value. Prices take `k`/`m` (`1.5k`) |
| `//ah mine` | Your listings and the hours they have left |
| `//ah cancel <#listing>` | Takes a listing back; the item returns to your bag |
| `//ah price <item>` | Vendor value, what the bot buyer pays at most, cheapest listing, this week's sales |

Examples: `//ah search ore`, `//ah buy 42 5`, `//ah sell Iron Ore 20 150`, `//ah sell 610710006 10 auto`.

How it works:

- A listed item waits in escrow (it leaves the bag). Enchanted or rolled items keep their enchant and go to the buyer as they are.
- The seller is paid **by mail** (Kinah attached, minus `SaleTaxPercent`), online or not. An online seller gets the mail at once. Payout mail is kept 10 years, not the usual 30 days.
- Unsold listings go back to the seller's bag after `ListingHours`.
- Soul-bound items and Kinah cannot be listed. You cannot buy your own listings.
- Light and Dark players see only their own world's listings unless `CrossWorld=true`; the bot's listings show in both.
- The market needs account logins (stored characters). Playerbots do not trade.

## Prices

The bots value an item at what a merchant pays for it (Item.dat sell price, `tools/data/shops.json`), or for a crafted item,
its recipe's materials plus the crafting fee per product, whichever is higher (`tools/data/crafts.json`). Items with
neither have no value: the seller bot never lists them and the buyer bot leaves them alone (unless `UnpricedMaxPriceEach`).

- Seller bot price each = value × `Seller:PriceMultiplier` (default 3), ± `Seller:PriceVariancePercent` (default 20%).
- Buyer bot buys a listing priced at most value × `Buyer:MaxPriceMultiplier` (default 2) each.
- `Seller:Prices:<item id>` fixes an item's price (the buyer bot then pays at most 1 less).

No resale loop: whatever the settings, the buyer bot pays less than the seller bot's lowest possible price
(value × `PriceMultiplier` × (1 − `PriceVariancePercent`)) and less than any NPC merchant's Kinah price for the item, and
never buys items merchants sell for other currencies (Abyss points, medals, coins).

The buyer bot creates Kinah when it buys and the seller bot removes Kinah when players buy from it. `Buyer:KinahPerDay`
(default 1,000,000; 0 = no cap) limits how much Kinah the buyer can bring in per day.

## Configuration (`Aion:Market`, environment `Aion__Market__...`)

Defaults are on: the market, the seller bot and the buyer bot all run when the host has a game database and a game port.

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | The market and `//ah` |
| `ListingHours` | 48 | How long a player listing stays up |
| `MaxListingsPerCharacter` | 50 | Listings per character at once (0: no cap) |
| `SaleTaxPercent` | 5 | Market cut taken from the seller's payout |
| `MaxPriceEach` | 2000000000 | Highest price each |
| `CrossWorld` | false | Light and Dark share one market |
| `ExpireCheckSeconds` | 60 | How often expired listings are returned |
| `Seller:Enabled` | true | The seller bot |
| `Seller:Name` | Market Trader | Name on its listings |
| `Seller:TargetListings` | 250 | Listings it keeps up |
| `Seller:ListingsPerCycle` | 25 | New listings per cycle at most |
| `Seller:MaxListingsPerItem` | 2 | Listings of one item at once |
| `Seller:CycleSeconds` | 60 | How often it restocks |
| `Seller:ListingHours` | 12 | How long its listings stay up (then it picks new ones) |
| `Seller:PriceMultiplier` | 3 | × item value |
| `Seller:PriceVariancePercent` | 20 | Random spread of its prices |
| `Seller:MinPriceEach` | 10 | Lowest price it asks |
| `Seller:MaxStack` | 20 | Stackable items are listed 1 to this many |
| `Seller:Materials` / `Crafted` / `Equipment` | true / true / false | Item pools: recipe materials, crafted products, all equipment |
| `Seller:MinItemLevel` / `MaxItemLevel` | 0 / 0 | Equipment level range (0: no bound) |
| `Seller:Include` / `Exclude` | empty | Extra item IDs to sell / never sell (`Seller__Include__0=...`) |
| `Seller:Prices:<id>` | none | Fixed price each for an item |
| `Buyer:Enabled` | true | The buyer bot |
| `Buyer:CycleSeconds` | 120 | How often it shops |
| `Buyer:MinListingAgeMinutes` | 10 | Listings younger than this are left for players |
| `Buyer:MaxPriceMultiplier` | 2 | Buys at most value × this each (always below the seller bot's and merchants' prices) |
| `Buyer:BuyChancePercent` | 35 | Chance per cycle to buy a listing it would pay for |
| `Buyer:MaxPurchasesPerCycle` | 5 | Purchases per cycle at most |
| `Buyer:KinahPerDay` | 1000000 | Daily spending cap (0: none) |
| `Buyer:UnpricedMaxPriceEach` | 0 | Pays up to this for items with no value (0: never) |

Example (`/etc/aion/game.env`):

```bash
Aion__Market__SaleTaxPercent=0
Aion__Market__CrossWorld=true
Aion__Market__Seller__Equipment=true
Aion__Market__Seller__MaxItemLevel=40
Aion__Market__Seller__Prices__610710006=250
Aion__Market__Buyer__BuyChancePercent=60
```

An invalid value stops the host at startup with the key's name. The startup log has a line beginning `Market:` with the
effective settings and how many items the seller bot picks from.

## Storage

Migration `040_market.sql`: `game.market_listings` (escrowed items sit in `game.item_instances` with location `market`),
`game.market_sales` (price history and the buyer bot's daily spending), `game.market_take_kinah`. Every sale is one
transaction (Kinah, item, payout mail, sale record, audit event `market_*`).

## Tests

`tests/Aion.Market.Checks`: pricing, command parsing, options and the bots against an in-memory market; with
`tools/check_market.py` (as for `check_persistence.py`, needs the dev PostgreSQL cluster) the storage checks run against a
disposable database: escrow, partial buys, tax and payout mail, enchanted items, worlds, cancel, expiry, bot buys and
the bot end to end.

```bash
dotnet build tests/Aion.Market.Checks && python3 tools/check_market.py
```

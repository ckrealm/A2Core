using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Application.Market;
using Aion.Core.Accounts;
using Npgsql;
using NpgsqlTypes;

namespace Aion.Infrastructure.Postgres;

/// <summary>
/// The market (migration 040, OURS) on the gameplay role. Every change is one transaction: escrow, Kinah, item, payout mail,
/// sale record and audit event commit together or not at all.
/// </summary>
public sealed class PostgresMarketStore(NpgsqlDataSource source) : IMarketStore
{
    public const int KinaItemId = 930100001;
    /// <summary>Post.dat 106: free title and body through "{title}|{body}" (as staff mail).</summary>
    const int FreePostDataId = 106;
    const short PostTabSystem = 1, PostTabReward = 2;

    static NpgsqlCommand Query(NpgsqlConnection c, NpgsqlTransaction? t, string sql, params object?[] args)
    {
        var q = new NpgsqlCommand(sql, c, t);
        foreach (var arg in args)
            q.Parameters.Add(arg switch
            {
                Typed typed => new NpgsqlParameter { Value = typed.Value ?? DBNull.Value, NpgsqlDbType = typed.Type },
                JsonElement json => new NpgsqlParameter { Value = json.GetRawText(), NpgsqlDbType = NpgsqlDbType.Jsonb },
                null => new NpgsqlParameter { Value = DBNull.Value },
                _ => new NpgsqlParameter { Value = arg },
            });
        return q;
    }
    sealed record Typed(object? Value, NpgsqlDbType Type);
    static Typed Uuid(Guid? v) => new(v, NpgsqlDbType.Uuid);
    static Typed Int(int? v) => new(v, NpgsqlDbType.Integer);
    static Typed Text(string? v) => new(v, NpgsqlDbType.Text);
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    static async Task<object?> Scalar(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object?[] args)
    { await using var q = Query(c, t, sql, args); var v = await q.ExecuteScalarAsync(ct); return v is DBNull ? null : v; }
    static async Task Execute(NpgsqlConnection c, NpgsqlTransaction t, string sql, CancellationToken ct, params object?[] args)
    { await using var q = Query(c, t, sql, args); await q.ExecuteNonQueryAsync(ct); }

    static async Task<bool> Own(NpgsqlConnection c, NpgsqlTransaction t, Guid account, Guid character, CancellationToken ct) =>
        await Scalar(c, t, "SELECT c.character_id FROM game.characters c JOIN auth.accounts a USING(account_id) WHERE c.character_id=$1 AND c.account_id=$2 AND a.disabled_at IS NULL FOR UPDATE OF c",
            ct, character, account) is Guid;

    const string ListingColumns = """
        l.listing_id,l.seller_character,l.seller_name,l.world_id,l.template_id,ci.name,l.quantity,l.price_each,l.listed_at,l.expires_at,
        COALESCE(i.upgrades<>'{}'::jsonb OR i.rolled_stats<>'{}'::jsonb,false)
        FROM game.market_listings l
        JOIN catalog.items ci ON ci.build_id=l.build_id AND ci.template_id=l.template_id
        LEFT JOIN game.item_instances i ON i.instance_id=l.instance_id
        """;

    static MarketListing ReadListing(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3), r.GetInt32(4), r.GetString(5),
        r.GetInt64(6), r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9), r.GetBoolean(10));

    static async Task<List<MarketListing>> Listings(NpgsqlConnection c, NpgsqlTransaction? t, string where, CancellationToken ct, params object?[] args)
    {
        var rows = new List<MarketListing>();
        await using var q = Query(c, t, $"SELECT {ListingColumns} {where}", args);
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) rows.Add(ReadListing(r));
        return rows;
    }

    public async Task<IReadOnlyList<MarketListing>> SearchAsync(string? text, int? templateId, int? worldId, int offset, int limit, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var pattern = string.IsNullOrWhiteSpace(text) ? null : "%" + text.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        return await Listings(c, null, """
            WHERE l.expires_at>now() AND ($1::text IS NULL OR ci.name ILIKE $1) AND ($2::integer IS NULL OR l.template_id=$2)
              AND ($3::integer IS NULL OR l.world_id IS NULL OR l.world_id=$3)
            ORDER BY ci.name, l.price_each, l.listing_id OFFSET $4 LIMIT $5
            """, ct, Text(pattern), Int(templateId), Int(worldId), Math.Max(0, offset), Math.Clamp(limit, 1, 200));
    }

    public async Task<IReadOnlyList<MarketListing>> ListingsOfAsync(Guid? seller, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return seller is { } s
            ? await Listings(c, null, "WHERE l.seller_character=$1 ORDER BY l.listing_id", ct, s)
            : await Listings(c, null, "WHERE l.seller_character IS NULL ORDER BY l.listing_id", ct);
    }

    public async Task<MarketListing?> ListingAsync(long id, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        return (await Listings(c, null, "WHERE l.listing_id=$1", ct, id)).FirstOrDefault();
    }

    public async Task<MarketListingResult> ListAsync(VerifiedAccount account, Guid character, Guid instance, long quantity, long priceEach,
        TimeSpan duration, int maxListings, CancellationToken ct = default)
    {
        if (quantity < 1 || priceEach < 1 || duration <= TimeSpan.Zero) return new("invalid");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("not_found");
        if (maxListings > 0 && (long)(await Scalar(c, t, "SELECT count(*) FROM game.market_listings WHERE seller_character=$1", ct, character))! >= maxListings)
            return new("too_many");
        string build; int template; long held; bool bound;
        await using (var q = Query(c, t, """
            SELECT build_id,template_id,quantity,(upgrades ? 'soul_bind') FROM game.item_instances
            WHERE instance_id=$1 AND character_id=$2 AND location='bag' FOR UPDATE
            """, instance, character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return new("not_found");
            build = r.GetString(0); template = r.GetInt32(1); held = r.GetInt64(2); bound = r.GetBoolean(3);
        }
        if (bound) return new("soul_bound");
        if (template == KinaItemId || quantity > held) return new("invalid");
        var name = (string)(await Scalar(c, t, "SELECT name FROM game.characters WHERE character_id=$1", ct, character))!;
        var world = (int)(await Scalar(c, t, "SELECT world_id FROM game.characters WHERE character_id=$1", ct, character))!;
        Guid escrow;
        if (quantity == held)
        {
            await Execute(c, t, "UPDATE game.item_instances SET location='market',slot=NULL,revision=revision+1 WHERE instance_id=$1", ct, instance);
            escrow = instance;
        }
        else
        {
            // Part of a stack: the listed units become their own plain row in escrow.
            await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1", ct, instance, quantity);
            escrow = (Guid)(await Scalar(c, t, """
                INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location)
                VALUES($1,$2,$3,$4,'market') RETURNING instance_id
                """, ct, character, build, template, quantity))!;
        }
        var id = (long)(await Scalar(c, t, """
            INSERT INTO game.market_listings(seller_character,seller_name,world_id,build_id,template_id,quantity,price_each,instance_id,expires_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,now()+$9) RETURNING listing_id
            """, ct, character, name, world, build, template, quantity, priceEach, escrow, duration))!;
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('market_listed',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { listing = id, template, quantity, price_each = priceEach }));
        await Execute(c, t, "SELECT pg_notify('aion_inventory',$1)", ct, character.ToString());
        var listing = (await Listings(c, t, "WHERE l.listing_id=$1", ct, id)).Single();
        await t.CommitAsync(ct);
        return new("ok", listing);
    }

    public async Task<MarketListing?> ListForBotAsync(string sellerName, int templateId, long quantity, long priceEach, TimeSpan duration, CancellationToken ct = default)
    {
        if (quantity < 1 || priceEach < 1 || templateId == KinaItemId) return null;
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        var build = await Scalar(c, t, """
            SELECT b.build_id FROM catalog.builds b JOIN catalog.items i ON i.build_id=b.build_id AND i.template_id=$1
            ORDER BY b.imported_at DESC LIMIT 1
            """, ct, templateId) as string;
        if (build is null) return null;
        var id = (long)(await Scalar(c, t, """
            INSERT INTO game.market_listings(seller_character,seller_name,world_id,build_id,template_id,quantity,price_each,expires_at)
            VALUES(NULL,$1,NULL,$2,$3,$4,$5,now()+$6) RETURNING listing_id
            """, ct, sellerName, build, templateId, quantity, priceEach, duration))!;
        var listing = (await Listings(c, t, "WHERE l.listing_id=$1", ct, id)).Single();
        await t.CommitAsync(ct);
        return listing;
    }

    /// <summary>The locked listing row: (seller, world, build, template, quantity, price each, escrowed item, seller name).</summary>
    sealed record Locked(Guid? Seller, int? World, string Build, int Template, long Quantity, long PriceEach, Guid? Instance, string SellerName);

    static async Task<Locked?> Lock(NpgsqlConnection c, NpgsqlTransaction t, long listingId, CancellationToken ct)
    {
        await using var q = Query(c, t, """
            SELECT seller_character,world_id,build_id,template_id,quantity,price_each,instance_id,seller_name FROM game.market_listings
            WHERE listing_id=$1 AND expires_at>now() FOR UPDATE
            """, listingId);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new(r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4), r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetGuid(6), r.GetString(7));
    }

    /// <summary>The seller's share after the market tax (rounded down; at least 0).</summary>
    public static long SellerShare(long total, double taxPercent) =>
        Math.Max(0, total - (long)Math.Ceiling(total * Math.Clamp(taxPercent, 0, 100) / 100.0));

    static async Task<PostRecord?> PayoutMail(NpgsqlConnection c, NpgsqlTransaction t, Guid seller, string itemName, long quantity, long total, long share, CancellationToken ct)
    {
        var body = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Your {itemName} x{quantity} sold for {total:N0} Kinah. After the market tax you receive {share:N0} Kinah.");
        var attachments = share > 0 ? new[] { new long[] { KinaItemId, share } } : [];
        await using var q = Query(c, t, """
            INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments) VALUES($1,$2,$3,$4,$5)
            RETURNING post_id,post_type,save_tab,post_data_id,msg_params,read,taken,sent_at,expire_at,attachments::text,reason
            """, seller, share > 0 ? PostTabReward : PostTabSystem, FreePostDataId, $"Market sale|{body}", Json(attachments));
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var items = (JsonSerializer.Deserialize<long[][]>(r.GetString(9)) ?? []).Where(a => a.Length >= 2).Select(a => ((int)a[0], a[1])).ToList();
        return new(r.GetInt64(0), r.GetInt16(1), r.GetInt16(2), r.GetInt32(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6),
            r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8), items, r.GetInt32(10));
    }

    public async Task<MarketPurchase> BuyAsync(VerifiedAccount account, Guid character, long listingId, long quantity, double taxPercent, int? worldId,
        CancellationToken ct = default)
    {
        if (quantity < 0) return new("invalid");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("not_found");
        if (await Lock(c, t, listingId, ct) is not { } l || (await Listings(c, t, "WHERE l.listing_id=$1", ct, listingId)).FirstOrDefault() is not { } before)
            return new("not_found");
        if (l.Seller == character) return new("own_listing", before);
        if (worldId is not null && l.World is not null && l.World != worldId) return new("other_world", before);
        var units = quantity == 0 ? l.Quantity : quantity;
        if (units > l.Quantity) return new("invalid", before);
        var total = checked(units * l.PriceEach);
        if (!(bool)(await Scalar(c, t, "SELECT game.market_take_kinah($1,$2)", ct, character, total))!) return new("not_enough_kinah", before);
        await Deliver(c, t, l, listingId, units, character, ct);
        await Execute(c, t, "INSERT INTO game.market_sales(listing_id,template_id,quantity,price_each,seller_character,buyer_character) VALUES($1,$2,$3,$4,$5,$6)",
            ct, listingId, l.Template, units, l.PriceEach, Uuid(l.Seller), character);
        long share = 0;
        PostRecord? post = null;
        if (l.Seller is { } seller)
        {
            share = SellerShare(total, taxPercent);
            post = await PayoutMail(c, t, seller, before.ItemName, units, total, share, ct);
        }
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('market_bought',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { listing = listingId, template = l.Template, quantity = units, paid = total, seller = l.Seller, seller_gets = share }));
        await Execute(c, t, "SELECT pg_notify('aion_inventory',$1)", ct, character.ToString());
        await t.CommitAsync(ct);
        return new("ok", before, units, total, share, post);
    }

    /// <summary>The bought units to <paramref name="buyer"/> (null: they leave the game) and the listing reduced or removed.</summary>
    static async Task Deliver(NpgsqlConnection c, NpgsqlTransaction t, Locked l, long listingId, long units, Guid? buyer, CancellationToken ct)
    {
        var all = units == l.Quantity;
        if (l.Instance is { } escrow)
        {
            var plain = (bool)(await Scalar(c, t, "SELECT upgrades='{}'::jsonb AND rolled_stats='{}'::jsonb FROM game.item_instances WHERE instance_id=$1 FOR UPDATE", ct, escrow) ?? true);
            if (all)
            {
                await Execute(c, t, "DELETE FROM game.market_listings WHERE listing_id=$1", ct, listingId);
                if (buyer is null || plain)
                    await Execute(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1", ct, escrow);
                else
                    // An enchanted or rolled item changes hands as it is.
                    await Execute(c, t, "UPDATE game.item_instances SET character_id=$2,location='bag',slot=NULL,revision=revision+1 WHERE instance_id=$1", ct, escrow, buyer.Value);
            }
            else
            {
                await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2,revision=revision+1 WHERE instance_id=$1", ct, escrow, units);
                await Execute(c, t, "UPDATE game.market_listings SET quantity=quantity-$2 WHERE listing_id=$1", ct, listingId, units);
            }
            if (buyer is { } b && (plain || !all))
                await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, b, l.Build, l.Template, units, Guid.NewGuid());
        }
        else
        {
            if (all) await Execute(c, t, "DELETE FROM game.market_listings WHERE listing_id=$1", ct, listingId);
            else await Execute(c, t, "UPDATE game.market_listings SET quantity=quantity-$2 WHERE listing_id=$1", ct, listingId, units);
            if (buyer is { } b) await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, b, l.Build, l.Template, units, Guid.NewGuid());
        }
    }

    public async Task<MarketPurchase> BotBuyAsync(long listingId, double taxPercent, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (await Lock(c, t, listingId, ct) is not { } l || (await Listings(c, t, "WHERE l.listing_id=$1", ct, listingId)).FirstOrDefault() is not { } before)
            return new("not_found");
        if (l.Seller is not { } seller) return new("own_listing", before);   // the bot never buys its own listings
        var total = checked(l.Quantity * l.PriceEach);
        await Deliver(c, t, l, listingId, l.Quantity, null, ct);
        await Execute(c, t, "INSERT INTO game.market_sales(listing_id,template_id,quantity,price_each,seller_character,buyer_character) VALUES($1,$2,$3,$4,$5,NULL)",
            ct, listingId, l.Template, l.Quantity, l.PriceEach, seller);
        var share = SellerShare(total, taxPercent);
        var post = await PayoutMail(c, t, seller, before.ItemName, l.Quantity, total, share, ct);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,details) VALUES('market_bot_bought',$1,$2)", ct, seller,
            Json(new { listing = listingId, template = l.Template, quantity = l.Quantity, paid = total, seller_gets = share }));
        await t.CommitAsync(ct);
        return new("ok", before, l.Quantity, total, share, post);
    }

    public async Task<string> CancelAsync(VerifiedAccount account, Guid character, long listingId, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return "not_found";
        var escrow = await Scalar(c, t, "DELETE FROM game.market_listings WHERE listing_id=$1 AND seller_character=$2 RETURNING instance_id", ct, listingId, character);
        if (escrow is not Guid item) return "not_found";
        await Return(c, t, item, ct);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('market_cancelled',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { listing = listingId }));
        await Execute(c, t, "SELECT pg_notify('aion_inventory',$1)", ct, character.ToString());
        await t.CommitAsync(ct);
        return "ok";
    }

    /// <summary>An escrowed item back in its owner's bag (a plain stack merges into the bag's stacks through grant_item).</summary>
    static async Task Return(NpgsqlConnection c, NpgsqlTransaction t, Guid item, CancellationToken ct)
    {
        Guid owner; string build; int template; long quantity; bool plain;
        await using (var q = Query(c, t, "SELECT character_id,build_id,template_id,quantity,upgrades='{}'::jsonb AND rolled_stats='{}'::jsonb FROM game.item_instances WHERE instance_id=$1 FOR UPDATE", item))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return;
            owner = r.GetGuid(0); build = r.GetString(1); template = r.GetInt32(2); quantity = r.GetInt64(3); plain = r.GetBoolean(4);
        }
        if (plain && (int)(await Scalar(c, t, "SELECT game.item_stack_max($1,$2)", ct, build, template))! != 1)
        {
            await Execute(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1", ct, item);
            await Scalar(c, t, "SELECT game.grant_item($1,$2,$3,$4,$5)", ct, owner, build, template, quantity, Guid.NewGuid());
        }
        else await Execute(c, t, "UPDATE game.item_instances SET location='bag',slot=NULL,revision=revision+1 WHERE instance_id=$1", ct, item);
    }

    public async Task<IReadOnlyList<MarketExpiry>> ExpireAsync(CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var t = await c.BeginTransactionAsync(ct);
        var expired = await Listings(c, t, "WHERE l.expires_at<=now() ORDER BY l.listing_id LIMIT 500 FOR UPDATE OF l", ct);
        foreach (var listing in expired)
        {
            var escrow = await Scalar(c, t, "DELETE FROM game.market_listings WHERE listing_id=$1 RETURNING instance_id", ct, listing.Id);
            if (escrow is Guid item) await Return(c, t, item, ct);
        }
        foreach (var seller in expired.Where(l => l.SellerCharacter is not null).Select(l => l.SellerCharacter!.Value).Distinct())
        {
            await Execute(c, t, "INSERT INTO audit.events(action,character_id,details) VALUES('market_expired',$1,$2)", ct, seller,
                Json(new { listings = expired.Where(l => l.SellerCharacter == seller).Select(l => l.Id) }));
            await Execute(c, t, "SELECT pg_notify('aion_inventory',$1)", ct, seller.ToString());
        }
        await t.CommitAsync(ct);
        return expired.Select(l => new MarketExpiry(l)).ToList();
    }

    public async Task<IReadOnlyList<(int TemplateId, string Name)>> FindItemsAsync(string text, int limit, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var trimmed = text.Trim();
        var id = int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        var pattern = "%" + trimmed.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var found = new List<(int, string)>();
        await using var q = Query(c, null, """
            SELECT DISTINCT ON (i.template_id) i.template_id,i.name FROM catalog.items i
            JOIN (SELECT build_id FROM catalog.builds ORDER BY imported_at DESC LIMIT 1) b USING(build_id)
            WHERE ($1::integer IS NOT NULL AND i.template_id=$1) OR ($1::integer IS NULL AND i.name ILIKE $2)
            ORDER BY i.template_id LIMIT $3
            """, Int(id), pattern, Math.Clamp(limit, 1, 50));
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) found.Add((r.GetInt32(0), r.GetString(1)));
        return found;
    }

    public async Task<MarketPriceHistory> PriceHistoryAsync(int templateId, TimeSpan window, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, """
            SELECT COALESCE(sum(quantity),0)::bigint, COALESCE(sum(quantity*price_each)/NULLIF(sum(quantity),0),0)::bigint,
                   COALESCE((SELECT min(price_each) FROM game.market_listings WHERE template_id=$1 AND expires_at>now()),0)::bigint
            FROM game.market_sales WHERE template_id=$1 AND sold_at>now()-$2
            """, templateId, window);
        await using var r = await q.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new(templateId, r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    public async Task<long> BotSpentSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var q = Query(c, null, "SELECT COALESCE(sum(quantity*price_each),0)::bigint FROM game.market_sales WHERE buyer_character IS NULL AND sold_at>=$1", since);
        return (long)(await q.ExecuteScalarAsync(ct))!;
    }
}

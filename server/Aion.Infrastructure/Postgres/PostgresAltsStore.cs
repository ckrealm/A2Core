using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Core.Accounts;
using Npgsql;

namespace Aion.Infrastructure.Postgres;

// Aion:Alts (OURS, docs/friends-server/ALTS.md; migration 040): the account warehouse and player mail.
public sealed partial class PostgresGameplayStore
{
    const int KinahItemId = 930100001;
    const string WarehouseColumns = "instance_id,build_id,template_id,quantity,location,slot,revision,rolled_stats::text,upgrades::text,wire_id";

    static async Task<(Guid Account, int World)> CharacterHome(NpgsqlConnection c, NpgsqlTransaction t, Guid character, CancellationToken ct)
    {
        await using var q = Query(c, t, "SELECT account_id,world_id FROM game.characters WHERE character_id=$1", character);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new GameplayException("character_unavailable");
        return (r.GetGuid(0), r.GetInt32(1));
    }

    // One writer per account warehouse at a time (two characters of one account may be in the world together): the lock is
    // held to the end of the transaction, so a take and a deposit never both see the same slot or the same item.
    static Task WarehouseLock(NpgsqlConnection c, NpgsqlTransaction t, Guid account, int world, CancellationToken ct) =>
        Execute(c, t, "SELECT pg_advisory_xact_lock(hashtextextended($1,7314))", ct, $"warehouse:{account}:{world}");

    // The account's other characters on the world reload their items (InventoryNotifications): a second session sees the change.
    static Task NotifyAccount(NpgsqlConnection c, NpgsqlTransaction t, Guid account, int world, Guid except, CancellationToken ct) =>
        Execute(c, t, "SELECT pg_notify('aion_inventory',character_id::text) FROM game.characters WHERE account_id=$1 AND world_id=$2 AND character_id<>$3", ct, account, world, except);

    static async Task<List<ItemState>> ReadItems(NpgsqlCommand q, CancellationToken ct)
    {
        var items = new List<ItemState>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.GetInt64(6),
                JsonSerializer.Deserialize<JsonElement>(r.GetString(7)), JsonSerializer.Deserialize<JsonElement>(r.GetString(8)), r.GetInt64(9)));
        return items;
    }

    public async Task<IReadOnlyList<ItemState>> AccountWarehouseAsync(VerifiedAccount account, Guid character, CancellationToken ct = default)
    {
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return [];
        var (owner, world) = await CharacterHome(c, t, character, ct);
        await WarehouseLock(c, t, owner, world, ct);
        // The character's own warehouse (the per-character one before Aion:Alts) joins the account's, keeping slots where free.
        var used = new HashSet<int>();
        await using (var q = Query(c, t, "SELECT slot FROM game.item_instances WHERE location='account_storage' AND warehouse_account=$1 AND warehouse_world=$2 AND slot IS NOT NULL", owner, world))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) used.Add(r.GetInt32(0));
        }
        var own = new List<(Guid Id, int? Slot)>();
        await using (var q = Query(c, t, "SELECT instance_id,slot FROM game.item_instances WHERE character_id=$1 AND location='storage' ORDER BY slot NULLS LAST,created_at,instance_id FOR UPDATE", character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) own.Add((r.GetGuid(0), r.IsDBNull(1) ? null : r.GetInt32(1)));
        }
        var next = 0;
        foreach (var (id, slot) in own)
        {
            int target;
            if (slot is { } kept && used.Add(kept)) target = kept;
            else { while (used.Contains(next)) next++; used.Add(target = next); }
            await Execute(c, t, "UPDATE game.item_instances SET location='account_storage',slot=$2,warehouse_account=$3,warehouse_world=$4 WHERE instance_id=$1", ct, id, target, owner, world);
        }
        if (own.Count > 0)
        {
            await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('warehouse_merged',$1,$2,$3)", ct, character, account.AccountId, Json(new { items = own.Count }));
            await NotifyAccount(c, t, owner, world, character, ct);
        }
        List<ItemState> items;
        await using (var q = Query(c, t, $"SELECT {WarehouseColumns} FROM game.item_instances WHERE location='account_storage' AND warehouse_account=$1 AND warehouse_world=$2 ORDER BY instance_id", owner, world))
            items = await ReadItems(q, ct);
        await t.CommitAsync(ct); return items;
    }

    public async Task<bool> MoveWarehouseItemAsync(VerifiedAccount account, Guid character, Guid item, long revision, string location, int slot, CancellationToken ct = default)
    {
        if (slot is < 0 or > 999 || location is not ("bag" or "account_storage")) throw new GameplayException("invalid_slot");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return false;
        var (owner, world) = await CharacterHome(c, t, character, ct);
        await WarehouseLock(c, t, owner, world, ct);
        // The moved item: the character's bag item (not soul-bound when it goes into the warehouse) or one of the warehouse's.
        string oldLocation; object oldSlot;
        await using (var q = Query(c, t, """
            SELECT location,slot,upgrades ? 'soul_bind' FROM game.item_instances
            WHERE instance_id=$1 AND revision=$2
              AND ((character_id=$3 AND location='bag') OR (location='account_storage' AND warehouse_account=$4 AND warehouse_world=$5)) FOR UPDATE
            """, item, revision, character, owner, world))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return false;
            oldLocation = r.GetString(0); oldSlot = r.IsDBNull(1) ? DBNull.Value : r.GetInt32(1);
            if (oldLocation == "bag" && (location == "bag" || r.GetBoolean(2))) return false;   // bag to bag is MoveBagItem; bound items stay out
        }
        // Off its slot first, so the swap below never meets it on the slot indexes.
        await Execute(c, t, "UPDATE game.item_instances SET slot=NULL WHERE instance_id=$1", ct, item);
        // The item on the target slot (if any) takes the moved item's old place.
        Guid? occupant = null; var occupantBound = false;
        await using (var q = Query(c, t, """
            SELECT instance_id,upgrades ? 'soul_bind' FROM game.item_instances
            WHERE slot=$1 AND instance_id<>$2
              AND (($3='account_storage' AND location='account_storage' AND warehouse_account=$4 AND warehouse_world=$5) OR ($3='bag' AND location='bag' AND character_id=$6)) FOR UPDATE
            """, slot, item, location, owner, world, character))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) { occupant = r.GetGuid(0); occupantBound = r.GetBoolean(1); }
        }
        if (occupant is { } other)
        {
            if (oldLocation == "account_storage" && occupantBound) return false;
            await Execute(c, t, """
                UPDATE game.item_instances SET location=$2,slot=$3,character_id=$4,
                    warehouse_account=CASE WHEN $2='account_storage' THEN $5 END,warehouse_world=CASE WHEN $2='account_storage' THEN $6 END
                WHERE instance_id=$1
                """, ct, other, oldLocation, oldSlot, character, owner, world);
        }
        await Execute(c, t, """
            UPDATE game.item_instances SET location=$2,slot=$3,character_id=$4,
                warehouse_account=CASE WHEN $2='account_storage' THEN $5 END,warehouse_world=CASE WHEN $2='account_storage' THEN $6 END
            WHERE instance_id=$1
            """, ct, item, location, slot, character, owner, world);
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('warehouse_move',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { item, from = oldLocation, to = location, slot, swapped = occupant }));
        await NotifyAccount(c, t, owner, world, character, ct);
        await t.CommitAsync(ct); return true;
    }

    public async Task<PlayerPostResult> SendPlayerPostAsync(VerifiedAccount account, Guid character, PlayerPost post, CancellationToken ct = default)
    {
        if (post.ItemCount < 0 || post.Kinah < 0 || post.Fee < 0 || post.Days is < 1 or > 3650 || string.IsNullOrWhiteSpace(post.To)) return new("invalid");
        await using var c = await source.OpenConnectionAsync(ct); await using var t = await c.BeginTransactionAsync(ct);
        if (!await Own(c, t, account.AccountId, character, ct)) return new("invalid");
        var (_, world) = await CharacterHome(c, t, character, ct);
        // Anyone on the world whose account is not disabled (deleted characters sit on a disabled account).
        if (await Scalar(c, t, "SELECT c.character_id FROM game.characters c JOIN auth.accounts a USING(account_id) WHERE c.world_id=$1 AND lower(c.name)=lower($2) AND a.disabled_at IS NULL LIMIT 1",
                ct, world, post.To.Trim()) is not Guid to) return new("unknown");
        if (to == character) return new("self");
        var escrow = new List<Guid>();
        if (post.Item is { } item)
        {
            long quantity;
            await using (var q = Query(c, t, "SELECT quantity,upgrades ? 'soul_bind' FROM game.item_instances WHERE instance_id=$1 AND character_id=$2 AND revision=$3 AND location='bag' FOR UPDATE",
                             item, character, post.ItemRevision))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return new("item");
                if (r.GetBoolean(1)) return new("bound");
                quantity = r.GetInt64(0);
            }
            var count = post.ItemCount == 0 ? quantity : post.ItemCount;
            if (count > quantity) return new("count");
            if (count == quantity)
            {
                // The stack itself travels (its enchant, rolled stats and identity kept).
                await Execute(c, t, "UPDATE game.item_instances SET character_id=$2,location='mail',slot=NULL WHERE instance_id=$1", ct, item, to);
                escrow.Add(item);
            }
            else
            {
                await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2 WHERE instance_id=$1", ct, item, count);
                escrow.Add((Guid)(await Scalar(c, t, """
                    INSERT INTO game.item_instances(character_id,build_id,template_id,quantity,location,rolled_stats,upgrades)
                    SELECT $2,build_id,template_id,$3,'mail',rolled_stats,upgrades FROM game.item_instances WHERE instance_id=$1 RETURNING instance_id
                    """, ct, item, to, count))!);
            }
        }
        var pay = post.Kinah + post.Fee;
        if (pay > 0)
        {
            var stacks = new List<(Guid Id, long Quantity)>();
            await using (var q = Query(c, t, "SELECT instance_id,quantity FROM game.item_instances WHERE character_id=$1 AND location='bag' AND template_id=$2 ORDER BY quantity,instance_id FOR UPDATE", character, KinahItemId))
            {
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) stacks.Add((r.GetGuid(0), r.GetInt64(1)));
            }
            if (stacks.Sum(s => s.Quantity) < pay) return new("kinah");
            var left = pay;
            foreach (var (id, quantity) in stacks)
            {
                if (left == 0) break;
                var take = Math.Min(left, quantity);
                if (take == quantity) await Execute(c, t, "DELETE FROM game.item_instances WHERE instance_id=$1", ct, id);
                else await Execute(c, t, "UPDATE game.item_instances SET quantity=quantity-$2 WHERE instance_id=$1", ct, id, take);
                left -= take;
            }
        }
        var granted = post.Kinah > 0 ? new[] { new[] { (long)KinahItemId, post.Kinah } } : [];
        var tab = granted.Length > 0 || escrow.Count > 0 ? 2 : 1;   // EPostSaveTab Reward with attachments, System without (as staff mail)
        var id2 = (long)(await Scalar(c, t, """
            INSERT INTO game.character_posts(character_id,save_tab,post_data_id,msg_params,attachments,attached_items,sender_character,expire_at)
            VALUES($1,$2,$3,$4,$5,$6,$7,now()+make_interval(days=>$8)) RETURNING post_id
            """, ct, to, (short)tab, post.DataId, post.Parameters, Json(granted), escrow.ToArray(), character, post.Days))!;
        PostRecord? stored = null;
        await using (var q = Query(c, t, $"SELECT {PostColumns} FROM game.character_posts p WHERE p.post_id=$1", id2))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct)) stored = ReadPost(r);
        }
        await Execute(c, t, "INSERT INTO audit.events(action,character_id,actor_id,details) VALUES('player_post_sent',$1,$2,$3)", ct, character, account.AccountId,
            Json(new { post = id2, to, items = escrow, kinah = post.Kinah, fee = post.Fee }));
        await t.CommitAsync(ct); return new("ok", to, stored);
    }
}

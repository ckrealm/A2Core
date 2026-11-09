using Aion.Protocol.Native;

namespace Aion.World;

// Monster loot. A kill rolls the monster's reward tables (LootTables); the drops lie on the ground around the
// corpse for the killer (0x561e DropItem_NT, owner Player), who picks them up with 0x5620 ItemLooting_RQ:
// the items are granted (stored), answered 0x5621 ItemLooting_RS per drop, removed with 0x561f
// DropItemRemove_NT, and auto-use items (pet souls) are used at once. Unlooted drops vanish after
// DropLifetimeMs. Opcodes: catalogue 340-343 + 0x54ca (the 0x56 run, OBSERVED around 0x5610/0x5612/0x5646);
// layouts are the usmap structs, no official sample (CANDIDATE).
// OURS on top: monsters without a reward table roll the generic level table (Aion:Rules:FallbackLoot), every monster
// pays Kinah (MonsterRewards), and a party member's kill is shared with the members nearby (Aion:Rules:PartyLoot, the
// members of the experience share): the killer's part lies on the ground as before, the others' part is granted straight
// to their bags on their own loops (the same grant and inventory messages as a pickup); Kinah is split evenly. A share
// that cannot go to the bag (the member is dead, the bag is full, the store fails) lies on the ground at the kill site
// for that member instead (its own drops, the same path as the killer's part), so nothing a kill gave is lost.
// Aion:Loot (OURS, LootRules): class-aware and duplicate-avoiding equipment picks, bad-luck protection per character and monster.
public sealed partial class GameSession
{
    sealed record GroundDrop(int Key, long Uid, int ItemId, long Count, Vector3d Center, Vector3d At, long ExpiresAt);

    readonly Dictionary<long, GroundDrop> drops = [];
    int dropCounter;
    public const int DropLifetimeMs = 60_000, FirstDropKey = 950_000;
    /// <summary>A drop is picked up within this distance (cm), with room for latency.</summary>
    public const double LootRange = 1500;
    const byte IncreaseDropItemGet = 5;   // EIncreaseReason kDropItemGet
    public IReadOnlyCollection<long> GroundDrops => drops.Keys;
    /// <summary>What lies on the ground for this player (item, count), for checks.</summary>
    internal IEnumerable<(int ItemId, long Count)> GroundDropItems => drops.Values.Select(d => (d.ItemId, d.Count));

    /// <summary>The loot of a killed monster: the drops for the killer (0x561e), or nothing; party members' shares go to them.</summary>
    List<byte[]> DropLoot(Target target, long now)
    {
        // The base drop rate (Aion:Rates:Drop, OURS) times drop events (SharedWorld.DropRate) rolls the table more often:
        // x2.5 = two rolls and a third half the time. Kinah in a drop is scaled by Aion:Rates:Kinah.
        var rolls = options.Rates.LootRolls(options.World?.DropRate ?? 1, rng);
        var generic = options.Rules.FallbackLoot && !LootTables.Embedded.HasLoot(target.NpcId);
        // Aion:Loot: picks re-drawn for the killer's class and bag; the equipment chance grows with its dry kills of this monster.
        var loot = options.Loot;
        var me = loot.Picks || loot.BadLuckProtection ? Looter() : null;
        Func<Func<int>, int>? pick = loot.Picks ? me!.Pick : null;
        var kill = new LootKill(target.NpcId, target.SubType, target.Level, generic);
        var pity = !Bot && PityCounts(kill, me);   // a playerbot's own kills give it no guarantee
        var boost = pity ? loot.ChanceAfter(loot.Book.Dry(PityKey, target.NpcId)) : 1;
        var rolled = new List<(int, long)>();
        for (var i = 0; i < rolls; i++)
            rolled.AddRange(generic ? MonsterRewards.Embedded.Fallback(target.SubType, target.Level, rng, pick, boost)
                : LootTables.Embedded.Roll(target.NpcId, profileRace, rng, pick, boost, me is null ? null : me.IsEquipment));
        rolled = options.Rates.WithKinah(rolled);
        var kinah = options.Rates.KinahAmount(MonsterRewards.Embedded.Kinah(target.SubType, target.Level, rng));
        var mine = ShareLootWithParty(rolled, kinah, loot.Counts(target.SubType) ? kill : null, target.Position);
        if (pity) mine = WithPity(mine, kill, me!);
        return PlaceDrops(mine, target.Position, now);
    }

    /// <summary>Lays items on the ground around <paramref name="center"/> for this player (0x561e), or nothing.</summary>
    List<byte[]> PlaceDrops(IReadOnlyList<(int ItemId, long Count)> items, Vector3d center, long now)
    {
        if (items.Count == 0) return [];
        var placed = new List<DropItem>();
        foreach (var (item, count) in items)
        {
            dropCounter++;
            var key = FirstDropKey + dropCounter % 40_000;
            var uid = ((long)playerKey << 32) | (uint)dropCounter;
            var angle = rng.NextDouble() * 2 * Math.PI;
            var radius = 60 + rng.NextDouble() * 90;
            var at = center with { X = center.X + Math.Cos(angle) * radius, Y = center.Y + Math.Sin(angle) * radius };
            var drop = new GroundDrop(key, uid, item, count, center, at, now + DropLifetimeMs);
            drops[uid] = drop;
            placed.Add(new DropItem(key, center, at, uid, item, (int)Math.Min(int.MaxValue, count), selected?.WireId ?? playerKey,
                now + DropLifetimeMs, (byte)profileRace));
        }
        return [LootPackets.DropItems(placed)];
    }

    /// <summary>
    /// Aion:Rules:PartyLoot over a kill's items and Kinah among the killer and its party members nearby (NearbyPartyMembers,
    /// as the experience share). The other members' parts are granted on their loops (ShareLoot); the killer's part is returned.
    /// <paramref name="killSite"/> (default: the killer's position) is where a member's part lands if its bag cannot take it.
    /// </summary>
    internal List<(int ItemId, long Count)> ShareLootWithParty(List<(int ItemId, long Count)> items, long kinah, LootKill? kill = null, Vector3d? killSite = null)
    {
        var rule = options.Rules.LootRule;
        var members = rule == PartyLootRule.Killer || worldPlayer is null ? [] : NearbyPartyMembers();
        var mine = new List<(int ItemId, long Count)>();
        if (members.Count == 0)
        {
            mine.AddRange(items);
            if (kinah > 0) mine.Add((KinaItemId, kinah));
            return mine;
        }
        var everyone = new List<WorldPlayer>(members.Count + 1) { worldPlayer! };
        everyone.AddRange(members);
        var parts = everyone.ToDictionary(p => p, _ => new List<(int ItemId, long Count)>());
        var party = Social?.PartyOf(worldPlayer!);
        foreach (var item in items)
        {
            var to = rule == PartyLootRule.RoundRobin && party is not null ? party.NextLooter(everyone) : everyone[rng.Next(everyone.Count)];
            parts[to].Add(item);
        }
        var shares = MonsterRewards.Split(kinah, everyone.Count);
        for (var i = 0; i < everyone.Count; i++)
            if (shares[i] > 0) parts[everyone[i]].Add((KinaItemId, shares[i]));
        // With bad-luck protection every player nearby hears of the kill (its own dry count), even with an empty share.
        foreach (var member in members)
        {
            // Playerbots get no dry count and no guaranteed item (their gear is not the players' progression).
            var memberKill = member.Session.Bot ? null : kill;
            if (parts[member].Count > 0 || memberKill is not null) member.Session.ShareLoot(parts[member], killSite ?? position ?? Vector3d.Zero, memberKill);
        }
        return parts[worldPlayer!];
    }

    /// <summary>
    /// Loot a party member's kill gave this player: granted on this player's own loop (its store, its messages); if the bag
    /// cannot take it, it lies on the ground at <paramref name="killSite"/> for this player. (A share still queued when this
    /// player's connection ends is dropped with the connection's other pending work.) <paramref name="kill"/>: the kill, for
    /// this member's own Aion:Loot dry count.
    /// </summary>
    public void ShareLoot(IReadOnlyList<(int ItemId, long Count)> items, Vector3d killSite, LootKill? kill = null, CancellationToken ct = default) =>
        _ = InvokeAsync(async now => (true, await GrantSharedLootAsync(items, killSite, kill, now, ct)));

    /// <summary>Item and Kinah lines this player received from party members' kills.</summary>
    public long SharedLootReceived { get; private set; }

    /// <summary>Item and Kinah lines of shares that went to the ground instead (dead, bag full, store error).</summary>
    public long SharedLootDropped { get; private set; }

    async Task<List<byte[]>> GrantSharedLootAsync(IReadOnlyList<(int ItemId, long Count)> items, Vector3d killSite, LootKill? kill, long now, CancellationToken ct)
    {
        // Aion:Loot on this member's own loop: another class's weapon becomes this class's; this member's dry kills and guarantee.
        if (options.Loot.ClassAware || kill is not null && options.Loot.BadLuckProtection)
        {
            var me = Looter();
            if (options.Loot.ClassAware) items = items.Select(i => (me.Adjust(i.ItemId), i.Count)).ToList();
            if (kill is not null && PityCounts(kill, me)) items = WithPity([.. items], kill, me);
        }
        if (items.Count == 0) return [];
        if (dead) return DropSharedLoot(items, killSite, now);
        var replies = new List<byte[]>();
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, items, ct) ? [[]] : []);
            if (granted.Count == 0) return DropSharedLoot(items, killSite, now);
            replies.AddRange(await InventoryChangesAsync(ct, IncreaseDropItemGet, 0));
            replies.AddRange(await AutoUseAsync(items.Select(i => i.ItemId), now, ct));
        }
        SharedLootReceived += items.Count;
        return replies;
    }

    /// <summary>This player as a looter (Aion:Loot): class, race and the equipment it already has (stored, worn, on the ground).</summary>
    LootLooter Looter(LootRules? rules = null)
    {
        var owned = new HashSet<int>(storedItems.Values.Select(s => s.TemplateId));
        owned.UnionWith(inventory.Values.Select(i => i.ItemId));
        owned.UnionWith(drops.Values.Select(d => d.ItemId));
        return new LootLooter(data, rules ?? options.Loot, className, profileRace, owned);
    }

    /// <summary>The key of this player's dry kills: the stored character, or this connection on development logins.</summary>
    Guid PityKey => CharacterId ?? developmentPityKey;
    readonly Guid developmentPityKey = Guid.NewGuid();

    /// <summary>Whether bad-luck protection counts this kill: a counted rank, and a monster that can drop equipment at all.</summary>
    bool PityCounts(LootKill kill, LootLooter? me) =>
        me is not null && options.Loot.Counts(kill.SubType)
        && (kill.Generic ? MonsterRewards.Embedded.HasFallback(kill.SubType, kill.Level) : LootTables.Embedded.CanDropEquipment(kill.NpcId, profileRace, me.IsEquipment));

    /// <summary>
    /// Bad-luck protection over this player's part of a kill: no equipment after PityKills dry kills adds one item for the class
    /// (from the monster's own equipment packages, or its level band; always class-aware, avoiding what the player has when it
    /// can); the dry count resets on any equipment, else grows.
    /// </summary>
    List<(int ItemId, long Count)> WithPity(List<(int ItemId, long Count)> items, LootKill kill, LootLooter me)
    {
        var got = items.Any(i => me.IsEquipment(i.ItemId));
        if (!got && options.Loot.Book.Dry(PityKey, kill.NpcId) >= options.Loot.PityKills)
        {
            var sure = Looter(options.Loot with { ClassAware = true, AvoidDuplicates = true, ClassRerolls = Math.Max(options.Loot.ClassRerolls, LootRules.DefaultPityRerolls) });
            var item = kill.Generic ? MonsterRewards.Embedded.GuaranteedFallback(kill.Level, rng, sure.Pick)
                : LootTables.Embedded.GuaranteedEquipment(kill.NpcId, profileRace, rng, me.IsEquipment, sure.Pick);
            if (item != 0)
            {
                items = [.. items, (item, 1)];
                got = true;
                PityDrops++;
            }
        }
        options.Loot.Book.Record(PityKey, kill.NpcId, got);
        return items;
    }

    /// <summary>Equipment items bad-luck protection guaranteed to this player (for checks).</summary>
    public int PityDrops { get; private set; }

    /// <summary>A share the bag could not take: on the ground at the kill site for this player (picked up later, or it expires).</summary>
    List<byte[]> DropSharedLoot(IReadOnlyList<(int ItemId, long Count)> items, Vector3d killSite, long now)
    {
        SharedLootDropped += items.Count;
        return PlaceDrops(items, killSite, now);
    }

    /// <summary>Pet souls (and pets) are used as soon as they are acquired (Item.dat AutoUseWhenAcquire).</summary>
    async Task<List<byte[]>> AutoUseAsync(IEnumerable<int> itemIds, long now, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        foreach (var id in itemIds)
            if (Boxes.Use(id) is { AutoUse: true, Effect: "GetVehicle" or "GetVehicleSoul" })
                foreach (var key in storedItems.Where(s => s.Value.TemplateId == id && s.Value.Location == "bag").Select(s => s.Key).ToList())
                    replies.AddRange(await UseItemAsync(key, now, ct, reply: false));
        return replies;
    }

    /// <summary>0x5620 ItemLooting_RQ {array of drop uids (i64), auto looting bool}.</summary>
    async Task<List<byte[]>> LootAsync(byte[] plain, long now, CancellationToken ct)
    {
        var uids = new List<long>();
        bool auto;
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var count = reader.Varint();
            for (var i = 0; i < count && i < 64; i++) uids.Add((long)reader.U64());
            auto = reader.U8() != 0;
        }
        catch (ProtocolException) { return []; }
        var replies = new List<byte[]>();
        var taken = new List<GroundDrop>();
        foreach (var uid in uids.Distinct())
        {
            var ok = drops.TryGetValue(uid, out var drop) && now < drop.ExpiresAt && !dead
                && (position is not { } p || (p.X - drop.At.X) * (p.X - drop.At.X) + (p.Y - drop.At.Y) * (p.Y - drop.At.Y) <= LootRange * LootRange);
            if (ok) taken.Add(drop!);
            else replies.Add(LootPackets.LootReply(1, uid, auto));
        }
        if (taken.Count == 0) return replies;
        if (Store is { } store && CharacterId is { } character)
        {
            var granted = await Stored(async () => await store.GrantLootAsync(character, taken.Select(d => (d.ItemId, d.Count)).ToList(), ct) ? [[]] : []);
            if (granted.Count == 0) return [.. replies, .. taken.Select(d => LootPackets.LootReply(1, d.Uid, auto))];
        }
        foreach (var d in taken) drops.Remove(d.Uid);
        replies.AddRange(taken.Select(d => LootPackets.LootReply(0, d.Uid, auto)));
        replies.Add(LootPackets.DropsRemoved(taken.Select(d => (d.Key, d.Uid, (long)(selected?.WireId ?? playerKey), auto)).ToList()));
        replies.AddRange(await InventoryChangesAsync(ct, IncreaseDropItemGet, 0));
        replies.AddRange(await AutoUseAsync(taken.Select(d => d.ItemId), now, ct));
        return replies;
    }

    /// <summary>Drops nobody took vanish (0x561f).</summary>
    List<byte[]> DropTick(long now)
    {
        if (drops.Count == 0) return [];
        var gone = drops.Values.Where(d => now >= d.ExpiresAt).ToList();
        if (gone.Count == 0) return [];
        foreach (var d in gone) drops.Remove(d.Uid);
        return [LootPackets.DropsRemoved(gone.Select(d => (d.Key, d.Uid, 0L, false)).ToList())];
    }
}

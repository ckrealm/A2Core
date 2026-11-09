namespace Aion.World;

// Aion:Fun (OURS, docs/friends-server/FUN.md), on a session's loop:
// - Field bosses: an open-world boss or named elite (FunFeatures.Rank) respawns after a random time in its window instead
//   of Aion:Rules:RespawnSeconds; a boss's kill and its next appearance are server-wide notices (FunFeatures queues them,
//   the host shows them to everyone); everyone on its hate table who is still on the map rolls its loot.
// - //buff: the configured item buffs at once, for Aion:Fun:BuffMinutes.
// - //hub: the race's towns and the Abyss (not from a dungeon, a duel, an arena or a fight).
// - A duel's end puts both duelists back at full HP and MP.
public sealed partial class GameSession
{
    long buffReadyAt;

    FunFeatures? Fun => options.Fun;

    /// <summary>A killed monster's respawn delay: its field-boss window (Aion:Fun) or Aion:Rules:RespawnSeconds.</summary>
    long KillRespawnMs(Target target) =>
        Fun is { Options.FieldBosses: true } fun && FunFeatures.Rank(target, instanceKey != 0) is not FieldRank.None and var rank
            ? fun.RespawnMs(rank, rng) : options.Rules.RespawnMs;

    /// <summary>The field rank of a target on this session's map (checks).</summary>
    public FieldRank FieldRankOf(Target target) => FunFeatures.Rank(target, instanceKey != 0);

    /// <summary>
    /// A field boss or named elite died to this player: the others who fought it (its hate table, on this map, not already
    /// sharing as party members nearby) roll its loot on their own loops; a boss's kill and next appearance are announced.
    /// </summary>
    void FieldBossKilled(Target target, long now)
    {
        if (Fun is not { Options.FieldBosses: true } fun) return;
        var rank = FunFeatures.Rank(target, instanceKey != 0);
        if (rank == FieldRank.None) return;
        var helpers = new List<WorldPlayer>();
        if (worldPlayer?.Map is { } map && map.Ai.Brains.TryGetValue(target.Key, out var brain))
        {
            var sharing = options.Rules.LootRule == PartyLootRule.Killer ? [] : NearbyPartyMembers();
            foreach (var key in brain.Hate.Keys)
                if (key != playerKey && map.Players.TryGetValue(key, out var helper) && !helper.Session.Dead && !sharing.Contains(helper))
                    helpers.Add(helper);
        }
        if (fun.Options.SharedBossLoot)
            foreach (var helper in helpers)
            {
                var items = RollKillLoot(target, helper.Session.profileRace);
                if (items.Count > 0) helper.Session.ShareLoot(items);
            }
        if (rank != FieldRank.Boss) return;
        var name = FunFeatures.DisplayName(target.Name);
        var party = worldPlayer is not null && Social?.PartyOf(worldPlayer) is { } p ? p.Others(worldPlayer).Count() : 0;
        var who = helpers.Count + party > 0 ? $"{StoredName} and {helpers.Count + party} other{(helpers.Count + party == 1 ? "" : "s")}" : StoredName;
        var region = FunFeatures.Region(worldMap);
        fun.Announce($"{who} defeated the field boss {name} (level {target.Level}) in the {region}!");
        var due = respawnAt.GetValueOrDefault(target.Key);
        if (due > now) fun.Announce($"The field boss {name} (level {target.Level}) has appeared in the {region}.", due);
    }

    /// <summary>One player's roll of a kill's loot (as DropLoot: rate rolls, the table or the fallback, Kinah).</summary>
    List<(int ItemId, long Count)> RollKillLoot(Target target, int race)
    {
        var rolls = options.Rates.LootRolls(options.World?.DropRate ?? 1, rng);
        var generic = options.Rules.FallbackLoot && !LootTables.Embedded.HasLoot(target.NpcId);
        var items = new List<(int ItemId, long Count)>();
        for (var i = 0; i < rolls; i++)
            items.AddRange(generic ? MonsterRewards.Embedded.Fallback(target.SubType, target.Level, rng) : LootTables.Embedded.Roll(target.NpcId, race, rng));
        items = options.Rates.WithKinah(LuckyLoot(items, target));
        var kinah = options.Rates.KinahAmount(MonsterRewards.Embedded.Kinah(target.SubType, target.Level, rng));
        if (kinah > 0) items.Add((KinaItemId, kinah));
        return items;
    }

    /// <summary>"//buff": the configured buffs (Aion:Fun:BuffSkills) for Aion:Fun:BuffMinutes. Call on the game loop.</summary>
    public (string Message, List<byte[]> Packets) FunBuff(long now)
    {
        if (Fun is not { Options.BuffCommand: true } fun) return ("//buff is off on this server.", []);
        if (resources is null) return ("Enter the world first.", []);
        if (dead) return ("You can't use //buff while dead.", []);
        if (now < buffReadyAt) return ($"//buff is ready again in {(buffReadyAt - now + 999) / 1000} s.", []);
        var durationMs = (fun.Options.BuffMinutes * 60_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var replies = new List<byte[]>();
        var applied = 0;
        foreach (var skill in fun.Options.BuffSkills)
        {
            // The item's own rows with the duration set to BuffMinutes.
            var effects = Boxes.SkillEffects(skill)
                .Select(e => e.Type == "Abnormal_True" ? e with { Values = [durationMs, durationMs, .. e.Values.Skip(2)] } : e)
                .ToList();
            if (!effects.Any(e => e.Type == "Abnormal_True") || !ItemAbnormalsSupported(effects)) continue;
            replies.AddRange(ApplyItemAbnormals(skill, effects, now));
            applied++;
        }
        if (applied == 0) return ("No buffs are available.", []);
        buffReadyAt = now + fun.Options.BuffCooldownSeconds * 1000L;
        return ($"{applied} buffs applied for {fun.Options.BuffMinutes} minutes.", replies);
    }

    /// <summary>"//hub" lists the destinations; "//hub &lt;number&gt;" teleports there. Call on the game loop.</summary>
    public (string Message, List<byte[]> Packets) FunHub(string args, long now)
    {
        if (Fun is not { Options.HubCommand: true }) return ("//hub is off on this server.", []);
        var hubs = FunFeatures.Hubs(profileRace);
        if (args.Length == 0)
            return ("Teleports: " + string.Join("; ", hubs.Select((h, i) => $"{i + 1}. {h.Label}")) + ". Use //hub <number>.", []);
        if (!int.TryParse(args, System.Globalization.CultureInfo.InvariantCulture, out var pick) || pick < 1 || pick > hubs.Count)
            return ($"Pick a number from 1 to {hubs.Count} (//hub lists them).", []);
        if (resources is null || position is null) return ("Enter the world first.", []);
        if (dead) return ("You can't teleport while dead.", []);
        if (instanceKey != 0) return ("Leave the dungeon first.", []);
        if (worldPlayer is not null && (Social?.DuelOf(worldPlayer) is not null || ArenaOf(worldPlayer) is not null)) return ("Finish your duel or arena match first.", []);
        if (inCombat) return ("You can't teleport during a fight.", []);
        var (label, mapId, point) = hubs[pick - 1];
        if (!data.KnownMap(mapId) || data.Destinations().FirstOrDefault(d => d.MapId == mapId && d.Name == point) is not { Name: not null } spot)
            return ($"{label} is not available on this server.", []);
        return ($"Teleporting to {label}.", Teleport(mapId, spot.Position, now));
    }

    /// <summary>Aion:Fun:DuelRestore: both duelists back at full HP and MP when their duel ends.</summary>
    void RestoreDuelists(Duel duel)
    {
        if (Fun is not { Options.DuelRestore: true }) return;
        foreach (var p in new[] { duel.Challenger, duel.Accepter })
            if (p.Session.resources is { } r && !p.Session.dead)
            {
                r.Hp = r.HpMax;
                r.Mp = r.MpMax;
                p.Send(r.Update());
            }
    }

    /// <summary>Aion:Fun:LuckyDrops: a kill's equipment drops may come one grade up.</summary>
    List<(int ItemId, long Count)> LuckyLoot(List<(int ItemId, long Count)> items, Target target) =>
        Fun is { } fun ? LootUpgrades.Apply(items, target.SubType, fun.Options, rng) : items;

    /// <summary>The bag's item instances now (to find what a loot grant added).</summary>
    HashSet<Guid> BagInstances() => storedItems.Values.Select(i => i.InstanceId).ToHashSet();

    /// <summary>Aion:Fun:LuckyDrops: each equipment piece a loot grant added may arrive enchanted (stored, then the bag update).</summary>
    async Task<List<byte[]>> EnchantLuckyLootAsync(HashSet<Guid> before, CancellationToken ct)
    {
        if (Fun is not { Options.LuckyDrops: true } fun || Store is not { } store || CharacterId is not { } character) return [];
        var changed = false;
        foreach (var item in storedItems.Values.Where(i => !before.Contains(i.InstanceId) && i.Location == "bag" && EnchantOf(i) == 0 && data.Item(i.TemplateId) is not null).ToList())
        {
            var level = LootUpgrades.EnchantLevel(item.TemplateId, fun.Options, rng);
            if (level <= 0) continue;
            var done = await Stored(async () => await store.EnchantItemAsync(character, item.InstanceId, item.Revision, [], level, 0, ct) ? [[]] : []);
            changed |= done.Count > 0;
        }
        return changed ? await InventoryChangesAsync(ct, IncreaseDropItemGet, 0) : [];
    }
}

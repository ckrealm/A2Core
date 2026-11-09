using Aion.Protocol.Native;
using Aion.World;

/// <summary>Organised Abyss PvP (docs/ABYSS_PVP.md): the artifact siege, the Spacetime Rifts, the battlefield and its filler bots.</summary>
static class AbyssChecks
{
    static long At(int day, int hour, int minute = 0, int second = 0) =>
        new DateTimeOffset(2026, 11, day, hour, minute, second, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static async Task<GameSession> EnterAsync(GameData data, SharedWorld world, string name, int creation, int level, int map, long t, Vector3d? at = null)
    {
        var s = new GameSession(data, new GameSessionOptions { World = world, CharacterLevel = level, DevelopmentMap = map }, new Random(name.Length));
        await s.HandleAsync(BotRequests.GameLogin("abyss:" + name), t);
        await s.HandleAsync(BotRequests.Create(name, creation), t);
        await s.HandleAsync(BotRequests.Select(65536), t);
        await s.HandleAsync(BotRequests.LoadComplete(1), t);
        if (at is { } a)
        {
            s.Teleport(map, a, t);
            await s.HandleAsync(BotRequests.LoadComplete(1), t);
        }
        return s;
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    public static async Task<int> RunAsync(GameData data, Action<bool, string> check)
    {
        var count = 0;
        void Check(bool condition, string name) { check(condition, name); count++; }

        // Options: the global defaults are valid, bad values are refused.
        var options = new AbyssOptions { Zone = TimeZoneInfo.Utc }.Validated();
        Check(options.Siege.Days.SequenceEqual(["Thursday", "Saturday"]) && options.Siege.Hour == 21 && options.Rifts.EveryHours == 3 && options.Battlefield.TeamSize == 10,
            "Aion:Abyss defaults: siege Thu/Sat 21:00, rifts every 3 hours, battlefield 10 a side");
        Check(BattlefieldOptions.Window("19:00-21:00") == (TimeSpan.FromHours(19), TimeSpan.FromHours(21)), "battlefield windows parse as HH:mm-HH:mm");
        Check(Throws(() => new AbyssOptions { Siege = new SiegeOptions { Hour = 25 } }.Validated())
              && Throws(() => new AbyssOptions { Battlefield = new BattlefieldOptions { Windows = ["noon"] } }.Validated())
              && Throws(() => new AbyssOptions { Battlefield = new BattlefieldOptions { Dungeons = [1] } }.Validated())
              && Throws(() => new AbyssBotOptions { SiegePerFaction = -1 }.Validated()), "Aion:Abyss and Aion:Abyss:Bots are validated");

        // The artifacts: the Dimensional Cores of the Lower and Middle layers, three each (RR, STI, WSA).
        var world = new SharedWorld(data);
        var war = new AbyssWar(data, options, world);
        world.Abyss = war;
        Check(war.Artifacts.Count == 6 && war.Artifacts.Count(a => a.Layer == 20) == 3 && war.Artifacts.Count(a => a.Layer == 22) == 3
              && war.Artifacts.Where(a => a.Layer == 20).Select(a => a.Region).SequenceEqual(["RR", "STI", "WSA"]),
            $"six artifacts: three Dimensional Cores on each siege layer ({war.Artifacts.Count})");

        // The schedule: Thursday and Saturday at 21:00.
        Check(war.NextSiege(At(11, 12)) == At(12, 21) && war.NextSiege(At(12, 22)) == At(14, 21), "the next siege: Thursday 21:00, then Saturday 21:00");

        // A siege: cores open, a faction breaks one, it holds the artifact, the bonus, the corridor.
        var elyos = await EnterAsync(data, world, "Siegewalker", 1, 50, 20, At(12, 20, 59));
        var asmo = await EnterAsync(data, world, "Riftrunner", 9, 50, 1110, At(12, 20, 59));
        war.Tick(At(12, 20, 59));
        Check(!war.SiegeRunning, "no siege before 21:00");
        war.Tick(At(12, 21, 0, 5));
        var layer = world.Map(20);
        var cores = layer.Targets.Values.Where(t => war.CoreOf(20, t) is not null).ToList();
        Check(war.SiegeRunning && cores.Count == 3 && cores.All(c => !c.Dead), "at 21:00 the siege starts and the Lower layer's cores are alive");
        var first = war.CoreOf(20, cores[0])!;
        Check(war.CorridorRefusal(first, 1) is not null, "no corridor while the artifact is still contested");
        war.CoreBroken(layer, cores[0], 1, At(12, 21, 10));
        Check(first.Owner == 1 && first.ClaimedThisSiege && Math.Abs(war.Bonus(1) - 0.025) < 1e-9 && war.Bonus(2) == 0,
            "the Elyos break a core: they hold its artifact, +2.5% Abyss Points");
        Check(war.CorridorRefusal(first, 1) is null && war.CorridorRefusal(first, 2) is not null, "the holder may enter the artifact's corridor, the enemy may not");
        Check(elyos.AbyssStatus(At(12, 21, 10)).Contains("Artifacts: Elyos 1, Asmodians 0"), "//abyss shows the artifacts held");
        Check(AbyssPackets.EventInfo(war.EventInfo(At(12, 21, 10)), war.Artifacts, war.SiegeEndsAt) is [0x07, 0xe3, ..], "AbyssArtifactEventInfo_RS (0xe307)");
        war.Tick(At(12, 22, 0, 1));
        Check(!war.SiegeRunning && first.Owner == 1 && war.Artifacts.Where(a => a.Layer == 20 && !a.ClaimedThisSiege).All(a => a.Owner == 0),
            "the siege ends after its hour; the claimed artifact stays held");
        Check(world.Map(20).Targets.Values.Where(t => war.CoreOf(20, t) is not null).All(c => c.Dead), "outside the siege the cores are gone");

        // Rifts: every 3 hours from 02:00 a portal opens in each home field; level 45 goes through; the stay ends at home.
        war.Tick(At(13, 2, 0, 30));
        var portals = war.Portals.ToList();
        Check(portals.Count == 2 && portals.Any(p => p is { HomeMap: 1010, TargetMap: 1110, Race: 1 }) && portals.Any(p => p is { HomeMap: 1110, TargetMap: 1010, Race: 2 }),
            "at 02:00 a rift opens in Verteron (to Altgard) and in Altgard (to Verteron)");
        Check(war.PortalAt(1010, 1100007, portals.First(p => p.HomeMap == 1010).At) is not null
              && AbyssWar.RiftSpots(1010, 1100007).Count == 5, "the open portal stands on one of the five rift spots of the field");
        var rookie = await EnterAsync(data, world, "Rookie", 1, 20, 1010, At(13, 2, 1));
        var light = portals.First(p => p.Race == 1);
        Check(war.EnterRift(rookie, light, default, At(13, 2, 1)).Refusal is not null && war.EnterRift(asmo, light, default, At(13, 2, 1)).Refusal is not null,
            "a rift refuses the low level and the other race");
        var visitor = await EnterAsync(data, world, "Visitor", 1, 50, 1010, At(13, 2, 1));
        var home = visitor.WorldPosition!.Value;
        var (arrival, refusal) = war.EnterRift(visitor, light, home, At(13, 2, 1));
        visitor.Teleport(1110, arrival, At(13, 2, 1));
        await visitor.HandleAsync(BotRequests.LoadComplete(1), At(13, 2, 1));
        Check(refusal is null && war.Visiting(visitor) && visitor.MapId == 1110, "a level 50 Elyos goes through into Altgard");
        war.Tick(At(13, 2, 11));
        Check(war.Portals.Count == 0, "the portals close after ten minutes");
        war.Tick(At(13, 3, 2, 1));
        await visitor.TickAsync(At(13, 3, 2, 1));
        await visitor.HandleAsync(BotRequests.LoadComplete(1), At(13, 3, 2, 1));
        Check(!war.Visiting(visitor) && visitor.MapId == 1010, $"(map {visitor.MapId}, visiting {war.Visiting(visitor)}) after an hour on the other side the visitor is pulled back home");

        // Rift PvP: switched in your own territory, 70 minutes apart; both on in a home field may fight.
        var t = At(13, 4);
        asmo.SetRiftPvp(true, t);
        Check(asmo.RiftPvp, "rift PvP switched on in one's own territory");
        asmo.SetRiftPvp(false, t + 60_000);
        Check(asmo.RiftPvp, "it cannot be switched back within 70 minutes");
        var spot = visitor.WorldPosition!.Value;
        Check(visitor.SetRiftPvp(true, t) is { } _ && visitor.RiftPvp, "the Elyos switches it on in Verteron");
        asmo.Teleport(1010, spot with { X = spot.X + 500 }, t);
        await asmo.HandleAsync(BotRequests.LoadComplete(1), t);
        Check(visitor.CanHitPlayer(asmo) && asmo.CanHitPlayer(visitor), "both with rift PvP on in a home field may fight");
        Check(visitor.PvpFoe(visitor.WorldPosition!.Value, 2500)?.Key == asmo.PlayerKey && rookie.PvpFoe(rookie.WorldPosition!.Value, 1_000_000) is null,
            "a bot's PvpFoe finds the enemy in reach; never for a player whose rift PvP is off");

        // Battlefield rewards: 1,000 Abyss Points and a medal to join, 3,000 and two more to win, three of each a week.
        var r = Enumerable.Range(0, 4).Select(i => war.BattlefieldRewards(elyos, won: true, At(12, 12) + i)).ToList();
        Check(r[0].Contains((GameSession.AbyssPointItemId, 1000)) && r[0].Contains((GameSession.AbyssPointItemId, 3000)) && r[0].Contains((930100014, 1)) && r[0].Contains((930100014, 2))
              && r[2].Count > 0 && r[3].Count == 0, "battlefield rewards: join and win points and medals, three a week");
        Check(war.BattlefieldRewards(elyos, won: false, At(18, 12)).Contains((GameSession.AbyssPointItemId, 1000)), "the week starts again on Wednesday");
        foreach (var s in new[] { elyos, asmo, rookie, visitor }) s.ExitSharedWorld();

        // The battlefield with filler bots: one player queues at noon, after the fill wait bots take the other 19 places.
        var field = new SharedWorld(data);
        var fieldWar = new AbyssWar(data, options with { Battlefield = options.Battlefield with { FillAfterSeconds = 5 } }, field)
        {
            BotOptions = new AbyssBotOptions { SiegePerFaction = 0, RiftPerFaction = 0 },
        };
        field.Abyss = fieldWar;
        var pool = new WorldBots(data, new WorldBotOptions(), field, new GameSessionOptions { World = field });
        var fillers = new PvpBots(pool, data, fieldWar.BotOptions);
        fieldWar.Bots = fillers;
        t = At(12, 12);
        Check(fieldWar.BattlefieldOpen(t) && !fieldWar.BattlefieldOpen(At(12, 15)) && fieldWar.BattlefieldOpen(At(12, 20)), "the battlefield is open 11-14 and 19-21");
        var player = await EnterAsync(data, field, "Fielder", 1, 50, 1010, t);
        player.JoinBattlefield(t);
        Check(field.Social.ArenaQueue.Count == 1, "//abyss bf queues the player");
        ArenaMatch? match = null;
        for (var i = 0; i < 20 * 90 && match is null; i++)
        {
            t += 50;
            await pool.TickAsync(t);
            if (i % 20 == 0) fieldWar.Tick(t);
            match = field.Social.Arenas.Values.FirstOrDefault(m => m.Battlefield is not null);
        }
        Check(match is { BlueTeam.Count: 10, RedTeam.Count: 10 } && match.Players.Count(p => p.Session.Bot) == 19 && fillers.BusyCount == 19,
            $"after the fill wait 19 bots join: ten against ten ({match?.BlueTeam.Count}v{match?.RedTeam.Count})");
        await player.HandleAsync(new PacketWriter().U16(GameSession.MatchingStartResponseRq).I32(match!.MatchingId).U8(1).ToArray(), t);
        Check(match.State >= 1, "the player accepts and everyone moves in (bots accept at once)");
        for (var i = 0; i < 20 * 30 && match.State < 3; i++)
        {
            t += 50;
            await pool.TickAsync(t);
            foreach (var reply in await player.TickAsync(t)) { }
            if (match.State is 1 or 2 && !match.Ready.Contains(player.PlayerKey) && player.InstanceKey != 0)
                await player.HandleAsync(new PacketWriter().U16(GameSession.ArenaDungeonReadyRq).I64(player.PlayerKey).ToArray(), t);
        }
        Check(match.State == 3 && match.CatalystKey != 0, $"the battle starts with the Shard Catalyst between the camps (state {match.State})");
        var bots = match.Players.Where(p => p.Session.Bot).Select(p => p.Session).ToList();
        Check(bots.All(b => b.PvpObjective(b.WorldPosition!.Value).Point is not null), "every filler bot heads for the catalyst");
        player.ExitSharedWorld();
        await pool.StopAsync();
        return count;
    }
}

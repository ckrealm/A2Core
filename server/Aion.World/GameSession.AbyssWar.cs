using Aion.Protocol.Native;

namespace Aion.World;

// Abyss PvP on the player's side (docs/ABYSS_PVP.md): the artifact siege's cores and corridors, the Spacetime Rifts (portals,
// visits, rift PvP) and the battlefield (Aion:Abyss:Battlefield) built on the arena match. The world-wide state and schedules
// are AbyssWar's (the host ticks it). Opcodes: the AbyssArtifact family follows AbyssArtifactEventInfo_RQ 0xe306 (live, host log
// 2026-10-16) in usmap order (EventStart_NT 0xe300 .. Teleport_RS 0xe30a), CANDIDATE; RiftOptionUpdate 0x3652-0x3654 in the 0x36
// block (catalogue index + 0x360c, as EnvObjSpawn 0x3634), CANDIDATE strong. Layouts from the usmap.
public sealed partial class GameSession
{
    GameData data_ => data;

    /// <summary>The host's Abyss PvP state (null: none on this host, or a private world).</summary>
    internal AbyssWar? War => options.World?.Abyss;

    /// <summary>Items for this player granted on its own loop (safe from another player's turn).</summary>
    public void PostReward(List<(int ItemId, long Count)> items)
    {
        if (items.Count == 0) return;
        _ = InvokeAsync(_ => { pendingRewards.Add(items); return (true, new List<byte[]>()); });
    }

    /// <summary>The faction's artifact bonus on Abyss Points (AbyssWar.Bonus), applied when they are granted.</summary>
    List<(int ItemId, long Count)> WithAbyssBonus(List<(int ItemId, long Count)> items)
    {
        var bonus = War?.Bonus(profileRace) ?? 0;
        if (bonus <= 0) return items;
        return items.Select(i => i.ItemId == AbyssPointItemId ? (i.ItemId, (long)Math.Round(i.Count * (1 + bonus))) : i).ToList();
    }

    /// <summary>The kill of a siege layer's core on the shared map: the killer's race claims the artifact.</summary>
    void AbyssCoreKilled(Target target, long now)
    {
        if (War is { } war && instanceKey == 0 && worldPlayer?.Map is { Instance: 0 } map && war.CoreOf(map.MapId, target) is not null)
            war.CoreBroken(map, target, (byte)profileRace, now);
    }

    /// <summary>
    /// An AbyssArtifact portal (the Abyss Corridor): only the holding faction, after its artifact's siege is decided; the corridor
    /// lasts CorridorMinutes. Null: not a corridor portal (the normal entry goes on); otherwise the replies (a refusal notice, or the entry).
    /// </summary>
    List<byte[]>? CorridorEntry(WorldObject o, long now)
    {
        if (o.Portal is not { Length: > 0 } portal || MapEventData.Dungeon(portal[0]) is not { Type: "AbyssArtifact" } || War is not { } war || IsAdmin) return null;
        if (war.CorridorArtifact(worldMap, o.At) is not { } artifact) return null;
        if (war.CorridorRefusal(artifact, profileRace) is { } why) return [Notice(why)];
        // The corridors come in pairs, one per faction (Dungeon_Artifact_L_* / _D_*): the holder's own.
        var mine = portal.Where(d => MapEventData.Dungeon(d)?.Name.Contains(profileRace == 2 ? "_D_" : "_L_", StringComparison.Ordinal) == true).ToList();
        var replies = EnterPartyDungeon(mine.Count > 0 ? mine : portal, now);
        if (instanceKey != 0) instanceExitAt = now + war.Options.Siege.CorridorMs;
        return replies;
    }

    // ---------- Spacetime Rifts ----------

    bool riftPvpOn;
    long riftPvpSetAt = long.MinValue / 2;

    /// <summary>A rift portal object: through it into the enemy's field when its portal is open; null when the object is no rift portal.</summary>
    List<byte[]>? RiftPortalUse(WorldObject o, long now)
    {
        if (War is not { } war || !AbyssWar.RiftRoutes.Any(r => r.Home == worldMap && r.Env == o.Env)) return null;
        if (instanceKey != 0 || war.PortalAt(worldMap, o.Env, o.At) is not { } portal) return [Notice("The rift is closed.")];
        var back = position ?? o.At;
        var (arrival, refusal) = war.EnterRift(this, portal, back, now);
        if (refusal is not null) return [Notice(refusal)];
        return [Notice($"You step through the rift. You have {war.Options.Rifts.StayMinutes} minutes on the other side."), .. Teleport(portal.TargetMap, arrival, now)];
    }

    /// <summary>The rift PvP switch: on, or forced by an attack, it lets two such players of different races fight in the home fields.</summary>
    public bool RiftPvp => riftPvpOn;

    static bool HomeField(int map) => map is 1010 or 1110 or 1011 or 1111;
    bool OwnTerritory => profileRace == 2 ? worldMap is 1110 or 1111 : worldMap is 1010 or 1011;

    /// <summary>RiftOptionUpdate_RQ {rift_pvp_on u8}: only in one's own territory, once per PvpCooldownMinutes.</summary>
    List<byte[]> RiftOption(byte[] plain, long now)
    {
        var on = plain.Length > 2 && plain[2] != 0;
        var cooldown = War?.Options.Rifts.PvpCooldownMs ?? 0;
        if (resources is null || !OwnTerritory || now - riftPvpSetAt < cooldown && on != riftPvpOn)
            return [AbyssPackets.RiftOption(riftPvpOn, riftPvpSetAt, 0, result: 1),
                Notice(!OwnTerritory ? "Rift PvP can only be changed in your own territory." : $"Rift PvP can be changed again in {Math.Ceiling((cooldown - (now - riftPvpSetAt)) / 60_000.0)} minutes.")];
        if (on != riftPvpOn)
        {
            riftPvpOn = on;
            riftPvpSetAt = now;
            worldPlayer?.Map?.Broadcast(worldPlayer, [AbyssPackets.RiftOptionChar(playerKey, on)]);
        }
        return [AbyssPackets.RiftOption(riftPvpOn, riftPvpSetAt, 0)];
    }

    /// <summary>Checks and chat: switches rift PvP as the client request does.</summary>
    public List<byte[]> SetRiftPvp(bool on, long now) => RiftOption([0x52, 0x36, (byte)(on ? 1 : 0)], now);

    /// <summary>Whether this player may hit <paramref name="victim"/> outside a duel or arena: the Abyss, or rift PvP on both in a home field.</summary>
    bool FactionTarget(GameSession victim) =>
        victim.profileRace != profileRace && victim.worldMap == worldMap && victim.instanceKey == instanceKey
        && (OnAbyssMap || Options.World?.Abyss is not null && instanceKey == 0 && HomeField(worldMap) && riftPvpOn && victim.riftPvpOn);

    /// <summary>A visitor in the enemy's field died: the visit ends and the revival is at home (null: not visiting).</summary>
    (int Map, Vector3d At)? RiftDeathReturn()
    {
        if (War?.EndVisit(this) is not { } visit) return null;
        return (visit.HomeMap, visit.Back);
    }

    // ---------- Who a bot may fight ----------

    /// <summary>Whether this player could hit <paramref name="other"/> now (duel opponent, arena or battlefield enemy, Abyss or rift foe).</summary>
    public bool CanHitPlayer(GameSession other)
    {
        if (other == this || other.worldPlayer is not { } them || worldPlayer is not { } me || other.dead || other.Dead || them.Hidden || !options.Rules.Pvp) return false;
        if (Social is { } s)
        {
            if (s.DuelOf(me) is { State: DuelState.InBattle } duel && (duel.Has(them))) return true;
            if (ArenaOf(me) is { State: 3 } match) return match.Enemies(me, them) && !match.RespawnAt.ContainsKey(them.Key);
        }
        return FactionTarget(other);
    }

    /// <summary>
    /// The nearest player this bot may fight within <paramref name="range"/> of <paramref name="around"/>, as a target snapshot (its key,
    /// position and HP) the bot brain can fight like a monster: the casts go to the player's key, the session's PvP path does the rest.
    /// </summary>
    public Target? PvpFoe(Vector3d around, double range)
    {
        if (worldPlayer?.Map is not { } map) return null;
        WorldPlayer? best = null;
        var bestDistance = range * range;
        foreach (var p in map.Players.Values)
        {
            var d = (p.Position.X - around.X) * (p.Position.X - around.X) + (p.Position.Y - around.Y) * (p.Position.Y - around.Y);
            if (d <= bestDistance && CanHitPlayer(p.Session)) (best, bestDistance) = (p, d);
        }
        return best is null ? null : Snapshot(best);
    }

    static Target Snapshot(WorldPlayer p) => new()
    {
        Key = p.Key, Name = p.Name, HpMax = Math.Max(1, p.Session.resources?.HpMax ?? 1), Hp = p.Session.dead ? 0 : p.Session.resources?.Hp ?? 1,
        Position = p.Position, Level = p.Session.level, Direction = p.Heading, Relationship = 6,
    };

    // ---------- Battlefield ----------

    /// <summary>MatchingStateChange_NT for the world's battlefield queue.</summary>
    public static byte[] MatchingStateFor(int matchingId, byte state) => MatchingState(matchingId, state);

    /// <summary>MatchingStart_RQ for the battlefield: the open hours, the level, PvP on; then the queue (AbyssWar matches it).</summary>
    List<byte[]> BattlefieldQueue(AbyssWar war, int matchingId, long now)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(MatchingStartRs).U16(result).I32(matchingId).ToArray();
        var o = war.Options.Battlefield;
        string? why = !options.Rules.Pvp || !o.Enabled ? "The battlefield is closed on this server."
            : !war.BattlefieldOpen(now) ? $"The battlefield is open {string.Join(" and ", o.Windows)}."
            : level < o.MinLevel ? $"The battlefield needs level {o.MinLevel}."
            : null;
        if (why is not null || Social is not { } s || worldPlayer is null || instanceKey != 0 || ArenaOf(worldPlayer) is not null || s.ArenaQueue.Any(q => q.Player == worldPlayer))
            return why is null ? [Reply(1)] : [Reply(1), Notice(why)];
        s.ArenaQueue.Add(new ArenaQueueEntry(worldPlayer, matchingId, now));
        var waiting = s.ArenaQueue.Count(q => q.MatchingId == matchingId);
        return [Reply(0), MatchingState(matchingId, MatchingStateMatching),
            Notice($"In the battlefield queue ({waiting} waiting). Free places are filled after {o.FillAfterSeconds} seconds.")];
    }

    /// <summary>//abyss bf: queue for the battlefield from chat (the client's matching window id is CANDIDATE).</summary>
    public List<byte[]> JoinBattlefield(long now) =>
        War is { } war ? BattlefieldQueue(war, war.Options.Battlefield.MatchingId, now) : [Notice("There is no battlefield on this server.")];

    /// <summary>
    /// A battlefield match for these players (AbyssWar's queue): two teams by turns, each human asked to accept (PendingReady), bots
    /// accepted at once; when everyone has accepted they all move in.
    /// </summary>
    internal static void OpenBattlefield(SharedWorld world, List<WorldPlayer> players, BattlefieldOptions o, long now)
    {
        var s = world.Social;
        var dungeon = o.Dungeons[Random.Shared.Next(o.Dungeons.Length)];
        var room = MapEventTables.Embedded.Dungeon(dungeon)!;
        var match = new ArenaMatch((int)s.NextId(), o.MatchingId, 0, dungeon, room.Map, 0, o.PlayMs)
        {
            NextMs = now + ArenaReadyWaitMs, Battlefield = o, RespawnMs = o.RespawnMs,
        };
        // Humans first, spread over both sides; bots then fill each side up.
        foreach (var p in players.OrderBy(p => p.Session.Bot ? 1 : 0))
            (match.BlueTeam.Count <= match.RedTeam.Count ? match.BlueTeam : match.RedTeam).Add(p);
        s.Arenas[match.RoomKey] = match;
        foreach (var p in match.Players)
        {
            if (p.Session.Bot) match.Accepted.Add(p.Key);
            else p.Send([MatchingState(o.MatchingId, MatchingStatePendingReady), Notice($"Battlefield ready: {match.BlueTeam.Count} against {match.RedTeam.Count}. Accept to enter.")]);
        }
        if (match.Accepted.Count >= match.Players.Count()) BeginArena(match, now);
    }

    /// <summary>Equalised PvP hits in a battlefield (the normalised level, skill level 1, no gear defense, HP scaled to the class's base); arena hits as they were.</summary>
    IReadOnlyList<Hit> ArenaSkillHits(ArenaMatch match, int skill, GameSession victim)
    {
        if (match.Battlefield is not { } o)
            return Combat.SkillHits(data, skill, className, level, equippedWeapon, victim.Defense, rng, pvp: true, fixingBonus: BuffStat("FixingDamage"), skillLevel: SkillLevel(skill));
        var defense = data.BaseStats(victim.className, o.NormalizedLevel).GetValueOrDefault("Defense");
        var hits = Combat.SkillHits(data, skill, className, o.NormalizedLevel, equippedWeapon, defense, rng, pvp: true);
        // Everyone's HP counts as the class's base at that level: a hit takes the same share of anyone's bar.
        var baseHp = data.BaseStats(victim.className, o.NormalizedLevel).GetValueOrDefault("HPMax") is > 0 and var b ? b : GamePackets.SpawnHpMax;
        var scale = (victim.resources?.HpMax ?? baseHp) / (double)Math.Max(1, baseHp);
        return hits.Select(h => h with { Amount = Math.Max(1, (int)Math.Round(h.Amount * scale)) }).ToList();
    }

    /// <summary>The battle starts: the catalyst between the camps (alive, held, immune: it only moves when pushed).</summary>
    static void StartCatalyst(ArenaMatch match, long now)
    {
        if (match.Players.FirstOrDefault()?.Session is not { Options.World: { } world } any) return;
        var map = world.Map(match.MapId, match.RoomKey);
        if (ArenaPointsOf(any.data_, match, ArenaMatch.Blue) is not { } blue || ArenaPointsOf(any.data_, match, ArenaMatch.Red) is not { } red) return;
        match.BlueCamp = blue.Start;
        match.RedCamp = red.Start;
        var car = map.Targets.Values.FirstOrDefault(t => t.Function == "ArenaCar");
        match.CatalystTickMs = now;
        match.LastAnnounced = 0;
        if (car is null) return;
        match.CatalystKey = car.Key;
        car.Held = true;
        car.Immune = true;
        map.RespawnAt.Remove(car.Key);
        if (car.Dead) { car.Hp = car.HpMax; car.CorpseRemoved = false; map.BroadcastAll([GamePackets.NpcAppear(car.Appearance())]); }
        match.Progress = match.StartProgress = Math.Clamp(Along(match, car.Position), 0.05, 0.95);
    }

    /// <summary>Where a point lies between the camps: 0 Blue's, 1 Red's.</summary>
    static double Along(ArenaMatch match, Vector3d at)
    {
        double dx = match.RedCamp.X - match.BlueCamp.X, dy = match.RedCamp.Y - match.BlueCamp.Y;
        var length = dx * dx + dy * dy;
        return length <= 0 ? 0.5 : ((at.X - match.BlueCamp.X) * dx + (at.Y - match.BlueCamp.Y) * dy) / length;
    }

    static Vector3d CatalystAt(ArenaMatch match, double progress, double z) =>
        new(match.BlueCamp.X + (match.RedCamp.X - match.BlueCamp.X) * progress, match.BlueCamp.Y + (match.RedCamp.Y - match.BlueCamp.Y) * progress, z);

    /// <summary>
    /// The push (the match owner's tick): the team with more living players within CatalystRadius moves the catalyst toward the other
    /// camp at CatalystSpeed; both or neither hold it still. Into the enemy camp (within the last 5% of the way) wins at once.
    /// </summary>
    void CatalystTick(ArenaMatch match, long now)
    {
        var o = match.Battlefield!;
        var dt = Math.Clamp(now - match.CatalystTickMs, 0, 1000) / 1000.0;
        match.CatalystTickMs = now;
        if (match.Players.FirstOrDefault()?.Session is not { Options.World: { } world }) return;
        var map = world.Map(match.MapId, match.RoomKey);
        var car = map.Targets.GetValueOrDefault(match.CatalystKey);
        var at = CatalystAt(match, match.Progress, car?.Position.Z ?? match.BlueCamp.Z);
        int Near(List<WorldPlayer> team) => team.Count(p => !p.Session.dead && !match.RespawnAt.ContainsKey(p.Key) && p.Map == map
            && (p.Position.X - at.X) * (p.Position.X - at.X) + (p.Position.Y - at.Y) * (p.Position.Y - at.Y) <= o.CatalystRadius * o.CatalystRadius);
        int blue = Near(match.BlueTeam), red = Near(match.RedTeam);
        if (blue == red) return;
        var length = Math.Sqrt(Math.Pow(match.RedCamp.X - match.BlueCamp.X, 2) + Math.Pow(match.RedCamp.Y - match.BlueCamp.Y, 2));
        var step = length <= 0 ? 0 : o.CatalystSpeed * dt / length;
        match.Progress = Math.Clamp(match.Progress + (blue > red ? step : -step), 0, 1);
        var moved = CatalystAt(match, match.Progress, at.Z);
        if (car is not null)
        {
            car.Position = moved;
            if (now - match.CatalystSentMs >= 500)
            {
                match.CatalystSentMs = now;
                var heading = Math.Atan2((blue > red ? match.RedCamp.Y : match.BlueCamp.Y) - moved.Y, (blue > red ? match.RedCamp.X : match.BlueCamp.X) - moved.X) * 180 / Math.PI;
                map.BroadcastAll([GamePackets.NpcMove(car.Key, moved, heading)]);
            }
        }
        // Progress notices at each quarter of the way from the start toward a camp.
        var share = match.Progress >= match.StartProgress ? (match.Progress - match.StartProgress) / Math.Max(0.01, 0.95 - match.StartProgress)
            : -(match.StartProgress - match.Progress) / Math.Max(0.01, match.StartProgress - 0.05);
        var quarter = (int)Math.Truncate(share * 4);
        if (quarter != match.LastAnnounced && Math.Abs(quarter) is > 0 and < 4)
        {
            match.LastAnnounced = quarter;
            var notice = Notice($"The Shard Catalyst is {Math.Abs(quarter) * 25}% of the way to the {(quarter > 0 ? "Red" : "Blue")} camp.");
            foreach (var p in match.Players) p.Send(notice);
        }
        if (match.Progress >= 0.95) EndArena(match, ArenaMatch.Blue, now);
        else if (match.Progress <= 0.05) EndArena(match, ArenaMatch.Red, now);
        else if (match.ExtraTime && Math.Abs(match.Progress - match.StartProgress) > 1e-9)
            // Extra time: any push past the start point decides it.
            EndArena(match, match.Progress > match.StartProgress ? ArenaMatch.Blue : ArenaMatch.Red, now);
    }

    /// <summary>Time is up: the team that pushed the catalyst farther wins; level, extra time once, then a draw.</summary>
    void BattlefieldTimeUp(ArenaMatch match, long now)
    {
        var o = match.Battlefield!;
        var delta = match.Progress - match.StartProgress;
        if (Math.Abs(delta) > 1e-9) { EndArena(match, delta > 0 ? ArenaMatch.Blue : ArenaMatch.Red, now); return; }
        if (!match.ExtraTime && o.ExtraMs > 0)
        {
            match.ExtraTime = true;
            match.NextMs = now + o.ExtraMs;
            var notice = Notice("Extra time! Push the Shard Catalyst toward the enemy camp to win.");
            foreach (var p in match.Players) p.Send(notice);
            return;
        }
        EndArena(match, 0, now);
    }

    /// <summary>A battlefield player leaves: out of the team (a bot is released); a team left empty loses.</summary>
    void LeaveBattlefield(ArenaMatch match, long now)
    {
        var me = worldPlayer!;
        var mine = match.TeamOf(me);
        match.BlueTeam.Remove(me);
        match.RedTeam.Remove(me);
        match.Scores.Remove(me.Key);
        match.RespawnAt.Remove(me.Key);
        match.Accepted.Remove(me.Key);
        if (Bot) War?.Bots?.Release(this);
        if (match.BlueTeam.Count == 0 || match.RedTeam.Count == 0)
        {
            if (match.State == 3 && match.Players.Any()) EndArena(match, mine == ArenaMatch.Blue ? ArenaMatch.Red : ArenaMatch.Blue, now);
            else
            {
                Social?.Arenas.Remove(match.RoomKey);
                foreach (var p in match.Players) { p.Session.instanceExitAt = now; if (p.Session.Bot) War?.Bots?.Release(p.Session); }
            }
        }
        else if (match.State < 3 && match.Accepted.Count >= match.Players.Count() && match.State == 0) BeginArena(match, now);
    }

    // ---------- Requests ----------

    List<byte[]> AbyssArtifactInfo(long now)
    {
        if (War is not { } war) return [PacketWriter.Opcode(AbyssPackets.EventInfoRs).U16(0).Varint(0).Varint(0).ToArray()];
        return [AbyssPackets.EventInfo(war.EventInfo(now), war.Artifacts, war.SiegeRunning ? war.SiegeEndsAt : war.NextSiege(now))];
    }

    /// <summary>//abyss: the state of the siege, the rifts and the battlefield, for this player.</summary>
    public string AbyssStatus(long now)
    {
        if (War is not { } war) return "There is no Abyss PvP on this server.";
        string When(long at) => at <= 0 ? "not scheduled" : DateTimeOffset.FromUnixTimeMilliseconds(at).ToOffset(war.Options.Zone.GetUtcOffset(DateTime.UtcNow)).ToString("ddd HH:mm");
        var light = war.Artifacts.Count(a => a.Owner == 1);
        var dark = war.Artifacts.Count(a => a.Owner == 2);
        var siege = war.SiegeRunning ? $"Siege running ({Math.Ceiling((war.SiegeEndsAt - now) / 60_000.0)} min left)" : $"Next siege {When(war.NextSiege(now))}";
        var rift = war.Portals.FirstOrDefault(p => p.Race == profileRace) is { } portal
            ? $"your rift is open in {(portal.HomeMap == 1010 ? "Verteron" : "Altgard")} at ({portal.At.X / 100:0}, {portal.At.Y / 100:0}) for {Math.Ceiling((portal.ClosesAt - now) / 60_000.0)} min"
            : "no rift open";
        var bf = war.BattlefieldOpen(now) ? "battlefield open (//abyss bf)" : "battlefield closed";
        var bonus = war.Bonus(profileRace) * 100;
        return $"{siege}. Artifacts: Elyos {light}, Asmodians {dark} (your Abyss Point bonus {bonus:0.#}%). Rift PvP {(riftPvpOn ? "on" : "off")}; {rift}; {bf}.";
    }
}

// ---------- PvP bots: their job and objective ----------
public sealed partial class GameSession
{
    /// <summary>What a PvP bot does for AbyssWar (siege, rift; the battlefield goes by the match).</summary>
    public PvpJob PvpJob { get; set; }
    Vector3d? riftAnchor;

    /// <summary>A rift bot arrived on the other side: rift PvP on (a bot goes there to fight) and it holds around the arrival.</summary>
    internal void RiftBotArrived(Vector3d arrival)
    {
        riftAnchor = arrival;
        riftPvpOn = true;
    }

    /// <summary>
    /// Where a PvP bot should be and what it should hit when no player is in reach: in a running battlefield its place around the
    /// Shard Catalyst (it pushes by standing there); on a siege job an unclaimed core of the layer (spread over the cores by key); on a
    /// rift job around where it arrived.
    /// </summary>
    public (Vector3d? Point, Target? Npc) PvpObjective(Vector3d me)
    {
        Vector3d Around(Vector3d at, double radius)
        {
            var angle = playerKey % 8 * Math.PI / 4;
            return new Vector3d(at.X + radius * Math.Cos(angle), at.Y + radius * Math.Sin(angle), at.Z);
        }
        if (worldPlayer is { } wp && ArenaOf(wp) is { Battlefield: { } o } match)
            return match.State == 3 ? (Around(CatalystAt(match, match.Progress, me.Z), o.CatalystRadius * 0.4), null) : (null, null);
        if (PvpJob == PvpJob.Siege && War is { SiegeRunning: true } war && worldPlayer?.Map is { Instance: 0 } map && war.Options.Siege.Layers.Contains(map.MapId))
        {
            var cores = map.Targets.Values.Where(t => !t.Dead && war.CoreOf(map.MapId, t) is { ClaimedThisSiege: false }).OrderBy(t => t.Key).ToList();
            if (cores.Count == 0) return (null, null);
            var core = cores[playerKey % cores.Count];
            var near = (core.Position.X - me.X) * (core.Position.X - me.X) + (core.Position.Y - me.Y) * (core.Position.Y - me.Y) <= 4000.0 * 4000.0;
            return (Around(core.Position, 400), near ? core : null);
        }
        if (PvpJob == PvpJob.Rift && riftAnchor is { } anchor && War?.Visiting(this) == true) return (Around(anchor, 600), null);
        return (null, null);
    }
}

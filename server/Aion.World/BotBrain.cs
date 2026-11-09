using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// The client request bodies a playerbot feeds its own <see cref="GameSession"/> (the null transport). The session's dispatch reads
/// decrypted client bodies, and the movement relay and cast broadcast work from those bodies, so a bot speaks the same bytes a
/// client does and goes through exactly the same handlers and broadcast paths. Layouts are the ones the native checks drive the
/// session with (live client traces, see GameSession.PositionReport / ParseCast).
/// </summary>
public static class BotRequests
{
    /// <summary>0x3613 game login: "account:guid", three more strings and 9 bytes (only the identity is read).</summary>
    public static byte[] GameLogin(string identity) =>
        new PacketWriter().U16(0x3613).String(identity).String("").String("bot").String("").Raw(new byte[9]).ToArray();

    /// <summary>0x361b character creation (development logins: the bot is an unsaved character).</summary>
    public static byte[] Create(string name, int creationId, int gender = 1) =>
        new PacketWriter().U16(0x361b).String(name).U8((byte)gender).U32((uint)creationId).Bytes([0x0c, 0x64]).U8(2).U8(1).U8(0).ToArray();

    /// <summary>0x361f selection: the key (a stored character's wire id shifted 16 bits), then i32 0 and a zero position.</summary>
    public static byte[] Select(uint key) => new PacketWriter().U16(0x361f).Varint(key).I32(0).Vec3(Vector3d.Zero).ToArray();

    /// <summary>0x3622 load complete, answering a 0x3621 map load (the ticket it carried).</summary>
    public static byte[] LoadComplete(int ticket) => new PacketWriter().U16(0x3622).I32(ticket).ToArray();

    /// <summary>
    /// 0x3700 move start / 0x3701 move update: mask 2, position, u16 direction, f32 heading, u8 1, u64 client time.
    /// CANDIDATE: direction and heading both carry the yaw in degrees (u16 as 65536 per turn).
    /// </summary>
    public static byte[] Move(bool start, Vector3d at, double yaw, long now) =>
        new PacketWriter().U16(start ? (ushort)0x3700 : (ushort)0x3701).U8(2).Vec3(at).U16((ushort)Math.Round(Normalize(yaw) * 65536 / 360) ).F32(yaw).U8(1).U64((ulong)now).ToArray();

    /// <summary>0x3718 stop (the observed 28-byte form): 00 00, position, f32 heading, u64 (CANDIDATE: client time).</summary>
    public static byte[] Stop(Vector3d at, double yaw, long now) =>
        new PacketWriter().U16(0x3718).U8(0).U8(0).Vec3(at).F32(yaw).U64((ulong)now).ToArray();

    /// <summary>The cast bracket the client sends around every cast (0x383a before, 0x383c after).</summary>
    public static byte[] CastBegin() => [0x3a, 0x38];
    public static byte[] CastEnd() => [0x3c, 0x38];

    /// <summary>0x3800 cast: presence mask 16 (the action uid byte), skill, 2, target, yaw, location, uid.</summary>
    public static byte[] Cast(int skill, int target, double yaw, Vector3d at, byte uid) =>
        new PacketWriter().U16(0x3800).U16(16).I32(skill).U8(2).Varint((uint)target).F32(yaw).Vec3(at).U8(uid).ToArray();

    /// <summary>0x5620 ItemLooting_RQ {varint count, the drops' uids (u64), auto looting u8}: a pick-up of the bot's own drops (not auto loot).</summary>
    public static byte[] Loot(IReadOnlyList<long> uids)
    {
        var w = new PacketWriter().U16(0x5620).Varint(uids.Count);
        foreach (var uid in uids) w.U64((ulong)uid);
        return w.U8(0).ToArray();
    }

    /// <summary>0x4006 NpcConversation_RQ {u8 0, i32 NPC key} (OBSERVED live 2026-10-18): talking to a dungeon's entry NPC.</summary>
    public static byte[] NpcConversation(int npcKey) => new PacketWriter().U16(0x4006).U8(0).I32(npcKey).ToArray();

    /// <summary>
    /// 0x3636 InteractEnvObj_RQ the live client's way (2026-10-15): u8 1, varint object key, i64 0, the EnvObj id (CANDIDATE: i32).
    /// A dungeon portal is used like this.
    /// </summary>
    public static byte[] UseObject(int objectKey, int env) => new PacketWriter().U16(0x3636).U8(1).Varint(objectKey).I64(0).I32(env).ToArray();

    /// <summary>0x4004 the client's request to leave an instance copy (answered 0x4005).</summary>
    public static byte[] LeaveInstance() => [0x04, 0x40];

    static double Normalize(double degrees) => (degrees % 360 + 360) % 360;
}

/// <summary>What a bot casts next: the rotation entry and its target, a group member (heals, buffs, resurrection) or the fight's NPC.</summary>
public sealed record BotChoice(BotRotationStep Step, GameSession? Ally, Target? Enemy);

public enum BotMovement { Follow, Stay }

/// <summary>
/// One playerbot's decisions (mod-playerbots style: strategies decide, actions are client requests). Each tick it looks at its
/// leader, its group and the fight and returns the requests to feed, in this priority: the rotation's heal, resurrection and
/// buff entries on the group (by the rotation's order), the fight (the leader's target, its own, or a monster attacking the
/// group; a tank first takes monsters off the others), picking up its own drops after the fight, following. Nothing here
/// touches the world; the session does when it handles the requests. All distances and speeds are OURS.
/// </summary>
public sealed class BotBrain(GameData data, GameSession bot, GameSession leader, BotClassRotation? rotation, int slot, int globalCooldownMs = 1000,
    double tankHateMultiplier = 1)
{
    /// <summary>The run speed a bot moves at (cm/s): the official run is about 600 (GameSession.RunThreshold); the check allows 1280.</summary>
    public const double RunSpeed = 600;
    /// <summary>A bot sends a movement update this often while it moves (ms).</summary>
    public const int MoveUpdateMs = 200;
    /// <summary>Following: start moving beyond FollowFar from the leader, stop within ArriveWithin of its slot; slots sit FollowDistance away.</summary>
    public const double FollowFar = 450, ArriveWithin = 80, FollowDistance = 250;
    /// <summary>Assist only targets within this distance of the leader (cm).</summary>
    public const double AssistRange = 3000;
    /// <summary>A refused cast (weapon, unlearned) is not tried again for this long.</summary>
    public const int RefusedRetryMs = 10_000;
    /// <summary>Looting: drops within LootSearch of the bot are collected, walking to within LootReach (the server allows GameSession.LootRange).</summary>
    public const double LootSearch = 2500, LootReach = 300;
    /// <summary>Heals and buffs are cast from within this share of the skill-effects heal_range.</summary>
    public const double HealReachShare = 0.8;

    public BotMovement Movement { get; set; } = BotMovement.Follow;
    public bool Assist { get; set; } = true;
    /// <summary>tank, dps or heal (rotation entries may name the roles they belong to).</summary>
    public string Role { get; set; } = rotation?.Roles.FirstOrDefault() ?? "dps";
    public int Slot { get; set; } = slot;
    public BotClassRotation? Rotation => rotation;

    /// <summary>Diagnostics: casts sent, accepted, movement messages sent.</summary>
    public int CastsSent { get; private set; }
    public int CastsAccepted { get; private set; }
    public int MovesSent { get; private set; }
    public int? FightingKey { get; private set; }
    /// <summary>Diagnostics: the last 100 accepted casts at group members (time, skill, player key).</summary>
    public IReadOnlyList<(long At, int Skill, int Target)> SupportLog => supportLog;
    readonly List<(long At, int Skill, int Target)> supportLog = [];
    /// <summary>Diagnostics: loot requests sent.</summary>
    public int LootsSent { get; private set; }
    /// <summary>Diagnostics: the last 100 accepted casts (time, skill).</summary>
    public IReadOnlyList<(long At, int Skill)> CastLog => castLog;
    readonly List<(long At, int Skill)> castLog = [];

    bool moving;
    long lastMoveAt, nextCastAt;
    Vector3d? pathGoal;
    List<Vector3d>? path;
    int pathIndex;
    byte uid;
    double yaw;
    readonly Dictionary<int, long> refused = [];
    readonly HashSet<long> lootTried = [];
    (int Skill, byte Uid, int Target, bool Friendly)? lastCast;

    static double Flat(Vector3d a, Vector3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double Yaw(Vector3d from, Vector3d to) => Math.Atan2(to.Y - from.Y, to.X - from.X) * 180 / Math.PI;

    /// <summary>Where this bot stands when following: its slot on a circle around the leader (OURS).</summary>
    public Vector3d SlotAround(Vector3d lead)
    {
        var angle = (180 + 60 * (Slot % 2 == 0 ? Slot / 2 : -(Slot + 1) / 2)) * Math.PI / 180;
        return new Vector3d(lead.X + FollowDistance * Math.Cos(angle), lead.Y + FollowDistance * Math.Sin(angle), lead.Z);
    }

    /// <summary>The requests for this tick, in order.</summary>
    public List<byte[]> Think(long now)
    {
        if (!bot.InWorld || bot.Dead || bot.WorldPosition is not { } me) return [];
        bot.BotRole = Role;
        bot.HateMultiplier = Role == "tank" ? tankHateMultiplier : 1;
        if (!leader.InWorld || leader.WorldPosition is not { } lead || leader.MapId != bot.MapId || leader.InstanceKey != bot.InstanceKey)
            return StopMoving(me, now);
        var group = Group();
        var threats = Threats(group, lead);
        var target = Assist ? Fight(now, me, lead, threats) : null;
        FightingKey = target?.Key;
        var choice = now >= nextCastAt ? Choose(target, now, group, Fighting(target, group, threats, now)) : null;
        // Heals, resurrection and buffs: run within reach of the group member, stop, cast.
        if (choice is { Ally: { } ally } && ally.WorldPosition is { } there)
        {
            var reach = HealRange * HealReachShare;
            if (ally != bot && Flat(me, there) > reach) return MoveToward(me, there, reach * 0.75, now);
            var requests = StopMoving(me, now);
            requests.AddRange(CastAt(choice.Step, ally.PlayerKey, there, me, now, friendly: true));
            return requests;
        }
        if (target is not null)
        {
            var range = rotation?.Range ?? 300;
            if (Flat(me, target.Position) > range) return MoveToward(me, target.Position, range * 0.8, now);
            var requests = StopMoving(me, now);
            if (choice is { Enemy: { } enemy }) requests.AddRange(CastAt(choice.Step, enemy.Key, enemy.Position, me, now, friendly: false));
            return requests;
        }
        if (Assist && Loot(me, lead, now) is { } loot) return loot;
        if (Movement == BotMovement.Stay) return StopMoving(me, now);
        var spot = SlotAround(lead);
        if (moving ? Flat(me, spot) > ArriveWithin : Flat(me, lead) > FollowFar) return MoveToward(me, spot, ArriveWithin / 2, now);
        return StopMoving(me, now);
    }

    /// <summary>The cast reply for the last cast this brain sent (from the session's replies): refused skills rest a while.</summary>
    public void Observe(IEnumerable<byte[]> replies, long now)
    {
        if (lastCast is not { } cast) return;
        foreach (var reply in replies)
        {
            if (reply.Length < 11 || reply[0] != 0x01 || reply[1] != 0x38 || BitConverter.ToInt32(reply, 5) != cast.Skill) continue;
            if (BitConverter.ToUInt16(reply, 3) == 0)
            {
                CastsAccepted++;
                castLog.Add((now, cast.Skill));
                if (castLog.Count > 100) castLog.RemoveAt(0);
                if (cast.Friendly)
                {
                    supportLog.Add((now, cast.Skill, cast.Target));
                    if (supportLog.Count > 100) supportLog.RemoveAt(0);
                }
            }
            else refused[cast.Skill] = now + RefusedRetryMs;
            lastCast = null;
            return;
        }
    }

    double HealRange => (bot.Options.SkillEffects ?? SkillEffectTable.Embedded).HealRange;

    /// <summary>The bot's group on its map and copy: itself, its leader, and their party members.</summary>
    public List<GameSession> Group()
    {
        var group = new List<GameSession> { bot };
        foreach (var member in new[] { leader }.Concat(bot.PartyMembers()).Concat(leader.PartyMembers()))
            if (!group.Contains(member) && member.InWorld && member.MapId == bot.MapId && member.InstanceKey == bot.InstanceKey) group.Add(member);
        return group;
    }

    /// <summary>
    /// Monsters fighting the group (their NpcAi brain is engaged on a group member: its hate table's top, or a map event's aggro),
    /// alive, hostile and within <see cref="AssistRange"/> of the leader, with the member each one attacks.
    /// </summary>
    List<(Target Npc, GameSession Victim)> Threats(List<GameSession> group, Vector3d lead)
    {
        if (bot.SharedMap is not { } map) return [];
        var found = new List<(Target, GameSession)>();
        foreach (var (key, brain) in map.Ai.Brains)
        {
            if (!brain.Engaged || brain.VictimIsNpc || brain.Mode is NpcAi.Mode.Return or NpcAi.Mode.Dead) continue;
            if (group.FirstOrDefault(s => s.PlayerKey == brain.VictimKey) is not { } victim) continue;
            if (bot.Targets.GetValueOrDefault(key) is { Dead: false, Hostile: true } npc && Flat(lead, npc.Position) <= AssistRange) found.Add((npc, victim));
        }
        return found;
    }

    static bool Healer(GameSession s) => s.BotRole is { } role ? role == "heal" : s.ClassName == "Cleric";

    /// <summary>
    /// The fight: a tank first takes the monsters attacking someone else (healers first, then the nearest); then the leader's
    /// target, the bot's own while it lives, a monster attacking the bot (self-defence), a monster attacking the leader or the
    /// party (the leader's victim first).
    /// </summary>
    Target? Fight(long now, Vector3d me, Vector3d lead, List<(Target Npc, GameSession Victim)> threats)
    {
        if (Role == "tank" && threats.Where(x => x.Victim != bot).OrderBy(x => Healer(x.Victim) ? 0 : 1).ThenBy(x => Flat(me, x.Npc.Position))
                .Select(x => x.Npc).FirstOrDefault() is { } loose) return loose;
        Target? Near(Target? t) => t is not null && Flat(lead, t.Position) <= AssistRange ? t : null;
        return Near(leader.AttackTarget(now)) ?? Near(bot.AttackTarget(now))
            ?? threats.OrderBy(x => x.Victim == bot ? 0 : x.Victim == leader ? 1 : 2).ThenBy(x => Flat(me, x.Npc.Position)).Select(x => x.Npc).FirstOrDefault();
    }

    /// <summary>
    /// The group is fighting: the bot has a fight, a monster is on one of them, or one of them hit a monster lately
    /// (<see cref="GameSession.AttackTarget"/>). A cast bracket alone (a heal) puts a session in combat, so InCombat is not used.
    /// </summary>
    static bool Fighting(Target? target, List<GameSession> group, List<(Target Npc, GameSession Victim)> threats, long now) =>
        target is not null || threats.Count > 0 || group.Any(s => s.AttackTarget(now) is not null);

    List<byte[]> CastAt(BotRotationStep step, int targetKey, Vector3d at, Vector3d me, long now, bool friendly)
    {
        nextCastAt = now + globalCooldownMs;
        uid = (byte)(uid % 255 + 1);
        if (targetKey != bot.PlayerKey) yaw = Yaw(me, at);
        lastCast = (step.Skill, uid, targetKey, friendly);
        CastsSent++;
        // CANDIDATE: a targeted cast's location is the target's position (the session uses it only for self-centred areas).
        return [BotRequests.CastBegin(), BotRequests.Cast(step.Skill, targetKey, yaw, at, uid), BotRequests.CastEnd()];
    }

    /// <summary>What the bot would cast now against <paramref name="target"/> (null: no fight), looking after its group.</summary>
    public BotChoice? Pick(Target? target, long now)
    {
        var group = Group();
        var threats = leader.WorldPosition is { } lead ? Threats(group, lead) : [];
        return Choose(target, now, group, Fighting(target, group, threats, now));
    }

    /// <summary>
    /// The first rotation entry the bot may cast now: learned, its role, conditions, off cooldown, affordable; a friendly entry
    /// needs a group member that meets its conditions (the lowest HP first for "ally"), an enemy entry the fight's target.
    /// </summary>
    BotChoice? Choose(Target? target, long now, List<GameSession> group, bool fighting)
    {
        if (rotation is null) return null;
        var (hp, hpMax, mp, mpMax, sp, _) = bot.Resource;
        foreach (var step in rotation.Rotation)
        {
            if (step.Level > bot.Level || !bot.Knows(step.Skill) || now < refused.GetValueOrDefault(step.Skill)) continue;
            if (step.Roles is { Length: > 0 } roles && !roles.Contains(Role)) continue;
            var when = step.Condition;
            if (!BotCondition.SupportedTargets.Contains(when.Target)) continue;
            if (when.SelfHpBelow is { } selfBelow && hpMax > 0 && hp * 100.0 / hpMax >= selfBelow) continue;
            if (when.SelfMpAbove is { } mpAbove && mpMax > 0 && mp * 100.0 / mpMax <= mpAbove) continue;
            if (when.InCombat is { } combat && combat != fighting) continue;
            if (data.Skill(step.Skill) is not { } skill || mp < skill.Cost.Mp || sp < skill.Cost.Sp || hp <= skill.Cost.Hp) continue;
            if (now < bot.CooldownUntil(step.Skill)) continue;
            if (when.Friendly)
            {
                if (Ally(step, when, group, now) is { } ally) return new BotChoice(step, ally, null);
                continue;
            }
            if (target is null || !HpHolds(when, target.HpMax > 0 ? target.Hp * 100.0 / target.HpMax : 0)) continue;
            return new BotChoice(step, null, target);
        }
        return null;
    }

    static bool HpHolds(BotCondition when, double pct) =>
        (when.TargetHpBelow is not { } below || pct < below) && (when.TargetHpAbove is not { } above || pct > above);

    /// <summary>The group member a friendly entry lands on: alive (dead for dead_ally), its HP and buff conditions met; the neediest.</summary>
    GameSession? Ally(BotRotationStep step, BotCondition when, List<GameSession> group, long now)
    {
        IEnumerable<GameSession> candidates = when.Target switch
        {
            "self" => [bot],
            "leader" => group.Contains(leader) ? [leader] : [],
            "dead_ally" => group.Where(s => s != bot),
            _ => group,
        };
        var wantDead = when.Target == "dead_ally";
        return candidates
            .Where(s => s.InWorld && s.Dead == wantDead && s.WorldPosition is not null && s.Resource.HpMax > 0)
            .Where(s => wantDead || HpHolds(when, s.Resource.Hp * 100.0 / s.Resource.HpMax))
            .Where(s => when.BuffMissing != true || !s.HasSkillBuff(step.Skill, now))
            .OrderBy(s => wantDead ? 0 : s.Resource.Hp * 100.0 / s.Resource.HpMax)
            .ThenBy(s => s == leader ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>
    /// After a fight: the bot's own drops (its share as the killer, GameSession.Loot.cs) within <see cref="LootSearch"/> are picked
    /// up with the client's 0x5620 request, walking to within <see cref="LootReach"/> first. A drop is asked for once.
    /// </summary>
    List<byte[]>? Loot(Vector3d me, Vector3d lead, long now)
    {
        var waiting = bot.LootableDrops(now).Where(d => !lootTried.Contains(d.Uid) && Flat(me, d.At) <= LootSearch && Flat(lead, d.At) <= AssistRange).ToList();
        if (waiting.Count == 0)
        {
            if (lootTried.Count > 0 && bot.LootableDrops(now).Count == 0) lootTried.Clear();
            return null;
        }
        var nearest = waiting.MinBy(d => Flat(me, d.At));
        if (Flat(me, nearest.At) > LootReach) return MoveToward(me, nearest.At, LootReach / 2, now);
        var uids = waiting.Where(d => Flat(me, d.At) <= LootReach + 150).Select(d => d.Uid).ToList();
        lootTried.UnionWith(uids);
        LootsSent++;
        var requests = StopMoving(me, now);
        requests.Add(BotRequests.Loot(uids));
        return requests;
    }

    List<byte[]> StopMoving(Vector3d me, long now)
    {
        if (!moving) return [];
        moving = false;
        path = null;
        MovesSent++;
        return [BotRequests.Stop(me, yaw, now)];
    }

    /// <summary>One step toward the goal along the navmesh (or straight, without one), sent every <see cref="MoveUpdateMs"/>.</summary>
    List<byte[]> MoveToward(Vector3d me, Vector3d goal, double stopWithin, long now)
    {
        if (Flat(me, goal) <= stopWithin) return StopMoving(me, now);
        if (!moving)
        {
            moving = true;
            lastMoveAt = now;
            yaw = Yaw(me, goal);
            MovesSent++;
            return [BotRequests.Move(start: true, me, yaw, now)];
        }
        if (now - lastMoveAt < MoveUpdateMs) return [];
        var budget = RunSpeed * Math.Min(now - lastMoveAt, 1000) / 1000.0;
        lastMoveAt = now;
        var nav = bot.SharedMap?.Ai.Nav;
        if (path is null || pathIndex >= path.Count || pathGoal is not { } planned || Flat(planned, goal) > 150)
        {
            path = nav?.FindPath(me, goal) ?? [goal];
            pathIndex = 0;
            pathGoal = goal;
        }
        else path[^1] = goal with { Z = path[^1].Z };   // the goal walks; the last leg follows it
        var at = me;
        while (budget > 0 && pathIndex < path.Count)
        {
            var next = path[pathIndex];
            var leg = Flat(at, next);
            if (leg <= budget)
            {
                at = next;
                budget -= leg;
                pathIndex++;
                continue;
            }
            var f = budget / leg;
            at = new Vector3d(at.X + (next.X - at.X) * f, at.Y + (next.Y - at.Y) * f, at.Z + (next.Z - at.Z) * f);
            budget = 0;
        }
        // Stop short of the goal by the wanted distance (a melee bot stands at its range, not on the target).
        if (Flat(at, goal) < stopWithin && Flat(me, goal) > stopWithin)
        {
            var back = (Flat(me, goal) - stopWithin) / Math.Max(1, Flat(me, at));
            at = new Vector3d(me.X + (at.X - me.X) * back, me.Y + (at.Y - me.Y) * back, me.Z + (at.Z - me.Z) * back);
        }
        at = at with { Z = Ground(at, nav) };
        yaw = Yaw(me, at);
        MovesSent++;
        return [BotRequests.Move(start: false, at, yaw, now)];
    }

    double Ground(Vector3d at, NavMesh? nav) =>
        nav?.FloorAt(at.X, at.Y, at.Z) ?? bot.SharedMap?.Ai.Terrain?.GroundAt(at.X, at.Y) ?? at.Z;
}

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

    static double Normalize(double degrees) => (degrees % 360 + 360) % 360;
}

public enum BotMovement { Follow, Stay }

/// <summary>
/// One playerbot's decisions (mod-playerbots style: strategies decide, actions are client requests). Each tick it looks at its
/// leader and the fight and returns the requests to feed: follow the leader along the navmesh, assist (attack what the leader
/// attacks) with the class rotation, or stand. Nothing here touches the world; the session does when it handles the requests.
/// All distances and speeds are OURS.
/// </summary>
public sealed class BotBrain(GameData data, GameSession bot, GameSession leader, BotClassRotation? rotation, int slot, int globalCooldownMs = 1000)
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

    public BotMovement Movement { get; set; } = BotMovement.Follow;
    public bool Assist { get; set; } = true;
    /// <summary>tank, dps or heal (rotation entries may name the roles they belong to).</summary>
    public string Role { get; set; } = rotation?.Roles.FirstOrDefault(r => r != "heal") ?? "dps";
    public int Slot { get; set; } = slot;
    public BotClassRotation? Rotation => rotation;

    /// <summary>Diagnostics: casts sent, accepted, movement messages sent.</summary>
    public int CastsSent { get; private set; }
    public int CastsAccepted { get; private set; }
    public int MovesSent { get; private set; }
    public int? FightingKey { get; private set; }
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
    (int Skill, byte Uid)? lastCast;

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
        if (!leader.InWorld || leader.WorldPosition is not { } lead || leader.MapId != bot.MapId) return StopMoving(me, now);
        var target = Assist ? Fight(now, lead) : null;
        FightingKey = target?.Key;
        if (target is not null)
        {
            var range = rotation?.Range ?? 300;
            if (Flat(me, target.Position) > range) return MoveToward(me, target.Position, range * 0.8, now);
            var requests = StopMoving(me, now);
            requests.AddRange(CastAt(target, me, now));
            return requests;
        }
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
            }
            else refused[cast.Skill] = now + RefusedRetryMs;
            lastCast = null;
            return;
        }
    }

    /// <summary>The leader's fight, or this bot's own one while its target lives (it finishes what it started).</summary>
    Target? Fight(long now, Vector3d lead)
    {
        var target = leader.AttackTarget(now) ?? bot.AttackTarget(now);
        return target is not null && Flat(lead, target.Position) <= AssistRange ? target : null;
    }

    List<byte[]> CastAt(Target target, Vector3d me, long now)
    {
        if (now < nextCastAt || Pick(target, now) is not { } step) return [];
        nextCastAt = now + globalCooldownMs;
        uid = (byte)(uid % 255 + 1);
        yaw = Yaw(me, target.Position);
        lastCast = (step.Skill, uid);
        CastsSent++;
        // CANDIDATE: a targeted cast's location is the target's position (the session uses it only for self-centred areas).
        return [BotRequests.CastBegin(), BotRequests.Cast(step.Skill, target.Key, yaw, target.Position, uid), BotRequests.CastEnd()];
    }

    /// <summary>The first rotation entry the bot may cast now: learned, its role, conditions, off cooldown, affordable.</summary>
    public BotRotationStep? Pick(Target target, long now)
    {
        if (rotation is null) return null;
        var (hp, hpMax, mp, mpMax, sp, _) = bot.Resource;
        foreach (var step in rotation.Rotation)
        {
            if (step.Level > bot.Level || !bot.Knows(step.Skill) || now < refused.GetValueOrDefault(step.Skill)) continue;
            if (step.Roles is { Length: > 0 } roles && !roles.Contains(Role)) continue;
            var when = step.Condition;
            if (!BotCondition.SupportedTargets.Contains(when.Target)) continue;   // heal/buff entries wait for their effects
            var targetPct = target.HpMax > 0 ? target.Hp * 100.0 / target.HpMax : 0;
            if (when.TargetHpBelow is { } below && targetPct >= below) continue;
            if (when.TargetHpAbove is { } above && targetPct <= above) continue;
            if (when.SelfHpBelow is { } selfBelow && hpMax > 0 && hp * 100.0 / hpMax >= selfBelow) continue;
            if (when.SelfMpAbove is { } mpAbove && mpMax > 0 && mp * 100.0 / mpMax <= mpAbove) continue;
            if (when.InCombat is { } combat && combat != bot.InCombat) continue;
            if (data.Skill(step.Skill) is not { } skill || mp < skill.Cost.Mp || sp < skill.Cost.Sp || hp <= skill.Cost.Hp) continue;
            if (now < bot.CooldownUntil(step.Skill)) continue;
            return step;
        }
        return null;
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

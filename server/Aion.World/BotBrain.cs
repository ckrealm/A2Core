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
    /// <summary>A combo follow-up ("after") is open this long after its opener by default (ms, OURS).</summary>
    public const int ComboWindowMs = 3000;
    /// <summary>A "behind" melee bot is in place within this distance of its spot at the monster's back (cm).</summary>
    public const double BehindTolerance = 120;
    /// <summary>A non-tank bot a monster attacks runs to its tank bot when farther than ToTankBeyond, stopping within ToTankWithin (cm).</summary>
    public const double ToTankBeyond = 500, ToTankWithin = 250;
    /// <summary>Gap closers and ranged openers reach this far (cm): the 20 m of their skill text. Farther, the bot walks first.</summary>
    public const double GapCloserReach = 2000;
    /// <summary>A bot stepping out of a boss's area special stops this far outside its reach (cm).</summary>
    public const double EscapeMargin = 200;

    /// <summary>Diagnostics: ticks spent stepping out of a boss's area special.</summary>
    public int Escapes { get; private set; }

    /// <summary>The group's tank bot (alive, in the world), not this bot.</summary>
    GameSession? TankBot(List<GameSession> group) =>
        group.FirstOrDefault(s => s != bot && s.BotRole == "tank" && !s.Dead && s.WorldPosition is not null);

    /// <summary>
    /// Where to stand while a monster fighting the group winds up an area special that would reach the bot: straight away from
    /// the monster, just outside its reach; Hold when it is already just outside (it may still cast, but takes no step). Null when
    /// the bot is well clear or is the special's victim.
    /// </summary>
    (Vector3d Spot, bool Hold)? Escape(Vector3d me, List<(Target Npc, GameSession Victim)> threats)
    {
        if (bot.SharedMap?.Ai is not { } ai) return null;
        foreach (var (npc, victim) in threats)
        {
            if (victim == bot || ai.WindingArea(npc) is not { } reach) continue;
            var d = Flat(me, npc.Position);
            if (d >= reach + EscapeMargin + 100) continue;
            if (d >= reach + 50) return (me, true);   // out of it: no steps (a melee bot would walk back in) until the special lands
            var (dx, dy) = d > 1 ? ((me.X - npc.Position.X) / d, (me.Y - npc.Position.Y) / d) : (Math.Cos(Slot), Math.Sin(Slot));
            return (new Vector3d(npc.Position.X + dx * (reach + EscapeMargin), npc.Position.Y + dy * (reach + EscapeMargin), me.Z), false);
        }
        return null;
    }

    /// <summary>A victim dodges a monster's special this long before it lands (ms, OURS; the dodge evades for GameSession.DodgeEvadeMs).</summary>
    public const int DodgeLeadMs = 300;
    public int Dodges { get; private set; }
    (int Npc, long Due)? dodged;
    /// <summary>The bot's class Dodge (each class has its own, GameSession.IsDodge), or null when it has not learned one.</summary>
    public int? DodgeSkill => Enumerable.Range(11, 8).Select(c => c * 1_000_000 + 100).Where(bot.Knows).Cast<int?>().FirstOrDefault();

    /// <summary>
    /// The dodge request when the bot is the victim of a monster's special landing within <see cref="DodgeLeadMs"/> and it can
    /// dodge (its class Dodge learned, SP, off cooldown); once per special. A short dash straight away from the monster.
    /// </summary>
    List<byte[]>? DodgeSpecial(Vector3d me, List<(Target Npc, GameSession Victim)> threats, long now)
    {
        if (bot.SharedMap?.Ai is not { } ai || DodgeSkill is not { } own || now < bot.CooldownUntil(own) || now < refused.GetValueOrDefault(own)) return null;
        if (data.Skill(own) is not { } dodge || bot.Resource.Sp < dodge.Cost.Sp) return null;
        foreach (var (npc, victim) in threats)
        {
            if (victim != bot || ai.SpecialDueAt(npc) is not { } due || due <= now || due - now > DodgeLeadMs || dodged == (npc.Key, due)) continue;
            dodged = (npc.Key, due);
            Dodges++;
            var d = Math.Max(1, Flat(me, npc.Position));
            var dash = new Vector3d(me.X + (me.X - npc.Position.X) / d * 100, me.Y + (me.Y - npc.Position.Y) / d * 100, me.Z);
            uid = (byte)(uid % 255 + 1);
            return [BotRequests.CastBegin(), BotRequests.Cast(own, bot.PlayerKey, yaw, dash, uid), BotRequests.CastEnd()];
        }
        return null;
    }

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
    (int Skill, byte Uid, int Target, bool Friendly, bool Interrupting)? lastCast;
    /// <summary>This bot's debuffs, dots and stuns on monsters: (skill, NPC key) to when they wear off (from skill-effects.json).</summary>
    readonly Dictionary<(int Skill, int Npc), long> applied = [];
    (int Skill, long At)? lastAccepted;

    /// <summary>Diagnostics: accepted interrupt casts (entries with target_casting), and accepted area casts.</summary>
    public int Interrupts { get; private set; }
    public int AreaCasts { get; private set; }

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
        // Boss mechanics first. The victim of a special about to land dodges through it (a Dodge evades the hits during it,
        // GameSession.GuardHit), even while it is stepping out of another monster's area.
        if (DodgeSpecial(me, threats, now) is { } dodge) return dodge;
        // Out of an area special being wound up (unless the bot is its victim, who takes it anyway).
        var hold = false;
        if (Escape(me, threats) is { } safe)
        {
            if (!safe.Hold)
            {
                Escapes++;
                return MoveToward(me, safe.Spot, ArriveWithin / 2, now);
            }
            hold = true;
        }
        // Held just outside an area special: casts from where the bot stands, no steps.
        List<byte[]> MoveToward(Vector3d from, Vector3d to, double within, long at) => hold ? StopMoving(from, at) : this.MoveToward(from, to, within, at);
        // A monster on a non-tank bot: bring it to the group's tank bot (mod-playerbots' "flee to tank"), so the tank picks it up
        // and the monster is not dragged off to its leash.
        if (Role != "tank" && TankBot(group) is { WorldPosition: { } tankAt } && threats.Any(x => x.Victim == bot) && Flat(me, tankAt) > ToTankBeyond)
            return MoveToward(me, tankAt, ToTankWithin, now);
        var target = Assist ? Fight(now, me, lead, threats) : null;
        FightingKey = target?.Key;
        var choice = now >= nextCastAt ? Choose(target, now, group, Fighting(target, group, threats, now), me, threats) : null;
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
            var range = Range;
            // A gap closer or a ranged opener reaches from farther than the bot's own range: cast it from where the bot stands.
            var distance = Flat(me, target.Position);
            var reachesFromHere = choice is { Enemy: not null, Step.Condition.TargetDistanceAbove: { } far } && distance > far && distance <= GapCloserReach;
            if (!reachesFromHere && distance > range) return MoveToward(me, target.Position, range * 0.8, now);
            // Melee damage dealers work from the monster's back while it is busy with someone else (positional bonuses, no cleaves):
            // they walk round between casts, and never hold a ready cast back for it.
            if (choice is null && BehindSpot(target, range) is { } back && Flat(me, back) > BehindTolerance)
                return MoveToward(me, back, BehindTolerance / 2, now);
            var requests = StopMoving(me, now);
            if (choice is { Enemy: { } enemy })
                requests.AddRange(choice.Step.IsArea
                    ? CastAt(choice.Step, bot.PlayerKey, choice.Step.Area == "self" ? me : enemy.Position, me, now, friendly: false, facing: enemy.Position)
                    : CastAt(choice.Step, enemy.Key, enemy.Position, me, now, friendly: false));
            return requests;
        }
        if (Assist && Loot(me, lead, now) is { } loot) return loot;
        if (Movement == BotMovement.Stay) return StopMoving(me, now);
        var spot = SlotAround(lead);
        if (moving ? Flat(me, spot) > ArriveWithin : Flat(me, lead) > FollowFar) return MoveToward(me, spot, ArriveWithin / 2, now);
        return StopMoving(me, now);
    }

    /// <summary>How close the bot gets to its fight before it casts: the class range, or its role's (a healer keeps its distance).</summary>
    public int Range => rotation?.RangeFor(Role) ?? 300;

    /// <summary>
    /// A "behind" dps bot's spot: at its range behind the monster (opposite its facing) while the monster attacks someone else; null
    /// otherwise (the bot is the victim, it is not a dps bot of a "behind" class, or the monster has no victim yet).
    /// </summary>
    public Vector3d? BehindSpot(Target target, double range)
    {
        if (rotation?.Position != "behind" || Role != "dps" || Victim(target) is not { } victim || victim == bot.PlayerKey) return null;
        var back = (target.Direction + 180) * Math.PI / 180;
        var distance = Math.Max(100, Math.Min(range * 0.7, range - 50));
        return new Vector3d(target.Position.X + distance * Math.Cos(back), target.Position.Y + distance * Math.Sin(back), target.Position.Z);
    }

    /// <summary>The player a monster attacks now (its NpcAi brain's victim), or null.</summary>
    int? Victim(Target target) =>
        bot.SharedMap?.Ai.Brains.GetValueOrDefault(target.Key) is { Engaged: true, VictimIsNpc: false, VictimKey: not 0 } brain ? brain.VictimKey : null;

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
                lastAccepted = (cast.Skill, now);
                if (!cast.Friendly && cast.Target != bot.PlayerKey && EffectMs(cast.Skill) is > 0 and var lasts)
                {
                    if (applied.Count > 64) foreach (var gone in applied.Where(a => a.Value <= now).Select(a => a.Key).ToList()) applied.Remove(gone);
                    applied[(cast.Skill, cast.Target)] = now + lasts;
                }
                if (cast.Interrupting) Interrupts++;
                if (!cast.Friendly && cast.Target == bot.PlayerKey) AreaCasts++;
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

    SkillEffectTable Effects => bot.Options.SkillEffects ?? SkillEffectTable.Embedded;
    double HealRange => Effects.HealRange;

    /// <summary>How long this skill's debuff, dot or stun lasts on a monster (the longest of its enemy effects), 0 without one.</summary>
    int EffectMs(int skill) => Effects.Of(skill) is { Target: "enemy" } set ? set.Effects.Select(e => e.DurationMs).DefaultIfEmpty(0).Max() : 0;

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

    List<byte[]> CastAt(BotRotationStep step, int targetKey, Vector3d at, Vector3d me, long now, bool friendly, Vector3d? facing = null)
    {
        nextCastAt = now + globalCooldownMs;
        uid = (byte)(uid % 255 + 1);
        if (facing is { } face) yaw = Yaw(me, face);
        else if (targetKey != bot.PlayerKey) yaw = Yaw(me, at);
        lastCast = (step.Skill, uid, targetKey, friendly, step.Condition.TargetCasting == true);
        CastsSent++;
        // CANDIDATE: a targeted cast's location is the target's position (the session uses it only for self-centred areas).
        return [BotRequests.CastBegin(), BotRequests.Cast(step.Skill, targetKey, yaw, at, uid), BotRequests.CastEnd()];
    }

    /// <summary>What the bot would cast now against <paramref name="target"/> (null: no fight), looking after its group.</summary>
    public BotChoice? Pick(Target? target, long now)
    {
        var group = Group();
        var threats = leader.WorldPosition is { } lead ? Threats(group, lead) : [];
        return Choose(target, now, group, Fighting(target, group, threats, now), bot.WorldPosition ?? default, threats);
    }

    /// <summary>
    /// The first rotation entry the bot may cast now: learned, its role, conditions, off cooldown, affordable; a friendly entry
    /// needs a group member that meets its conditions (the lowest HP first for "ally"), an enemy entry the fight's target.
    /// </summary>
    BotChoice? Choose(Target? target, long now, List<GameSession> group, bool fighting, Vector3d me, List<(Target Npc, GameSession Victim)> threats)
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
            if (when.SelfMpBelow is { } mpBelow && mpMax > 0 && mp * 100.0 / mpMax >= mpBelow) continue;
            var window = when.AfterWithinMs ?? ComboWindowMs;
            if (when.AfterCritical == true && now - bot.LastCriticalAt > window) continue;
            if (when.AfterBlock == true && now - bot.LastBlockAt > window) continue;
            if (when.AfterEvasion == true && now - bot.LastEvadeAt > window) continue;
            if (when.AfterDodge == true && now - bot.LastDodgeAt > window) continue;
            if (when.After is { Length: > 0 } after && (lastAccepted is not { } last || !after.Contains(last.Skill) || now - last.At > (when.AfterWithinMs ?? ComboWindowMs))) continue;
            if (when.InCombat is { } combat && combat != fighting) continue;
            if (data.Skill(step.Skill) is not { } skill || mp < skill.Cost.Mp || sp < skill.Cost.Sp || hp <= skill.Cost.Hp) continue;
            if (now < bot.CooldownUntil(step.Skill)) continue;
            if (when.Friendly)
            {
                if (Ally(step, when, group, now, threats) is { } ally) return new BotChoice(step, ally, null);
                continue;
            }
            if (target is null || !HpHolds(when, target.HpMax > 0 ? target.Hp * 100.0 / target.HpMax : 0) || !EnemyHolds(step, when, target, now, me, threats)) continue;
            return new BotChoice(step, null, target);
        }
        return null;
    }

    /// <summary>The enemy conditions of an entry against the fight's target (docs/PLAYERBOTS.md).</summary>
    bool EnemyHolds(BotRotationStep step, BotCondition when, Target target, long now, Vector3d me, List<(Target Npc, GameSession Victim)> threats)
    {
        var ai = bot.SharedMap?.Ai;
        if (when.TargetCasting is { } casting && casting != (ai?.CastingSpecial(target) is not null)) return false;
        if (when.TargetStunned is { } stunned && stunned != (target.Stunned(now) || target.Held)) return false;
        if (when.TargetSlowed is { } slowed && slowed != (target.SpeedScale(now) < 1.0)) return false;
        if (when.TargetElite is { } elite && elite != target.Elite) return false;
        if (when.TargetBoss is { } boss && boss != target.Boss) return false;
        if (when.TargetOnSelf is { } onSelf && (Victim(target) is not { } victim || onSelf != (victim == bot.PlayerKey))) return false;
        if (when.TargetEffectMissing == true && now < applied.GetValueOrDefault((step.Skill, target.Key))) return false;
        if (when.TargetHas is { Length: > 0 } has && !has.Any(skill => now < applied.GetValueOrDefault((skill, target.Key)))) return false;
        if (when.TargetDistanceAbove is { } far && Flat(me, target.Position) <= far) return false;
        if (when.EnemiesAtLeast is { } count)
        {
            var centre = step.Area == "self" ? me : target.Position;
            var reach = GameSession.AreaRange;
            var near = threats.Select(x => x.Npc).Append(target).Distinct().Count(n => !n.Dead && Flat(centre, n.Position) <= reach);
            if (near < count) return false;
        }
        return true;
    }

    static bool HpHolds(BotCondition when, double pct) =>
        (when.TargetHpBelow is not { } below || pct < below) && (when.TargetHpAbove is not { } above || pct > above);

    /// <summary>The group member a friendly entry lands on: alive (dead for dead_ally), its HP and buff conditions met; the neediest.</summary>
    GameSession? Ally(BotRotationStep step, BotCondition when, List<GameSession> group, long now, List<(Target Npc, GameSession Victim)> threats)
    {
        var tank = Tank(group, threats);
        IEnumerable<GameSession> candidates = when.Target switch
        {
            "self" => [bot],
            "leader" => group.Contains(leader) ? [leader] : [],
            "tank" => tank is null ? [] : [tank],
            "dead_ally" => group.Where(s => s != bot),
            _ => group,
        };
        var wantDead = when.Target == "dead_ally";
        return candidates
            .Where(s => s.InWorld && s.Dead == wantDead && s.WorldPosition is not null && s.Resource.HpMax > 0)
            .Where(s => wantDead || HpHolds(when, s.Resource.Hp * 100.0 / s.Resource.HpMax))
            .Where(s => when.BuffMissing != true || !s.HasSkillBuff(step.Skill, now))
            // The tank counts TankHealBias points lower: at equal need it is healed first, it takes the hits.
            .OrderBy(s => wantDead ? 0 : s.Resource.Hp * 100.0 / s.Resource.HpMax - (s == tank ? TankHealBias : 0))
            .ThenBy(s => s == leader ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>Heals favour the tank by this many HP points (%).</summary>
    public const double TankHealBias = 10;

    /// <summary>The group's tank: a tank bot (this one too), else the member the most monsters attack now, else nobody.</summary>
    GameSession? Tank(List<GameSession> group, List<(Target Npc, GameSession Victim)> threats) =>
        group.FirstOrDefault(s => s.BotRole == "tank" && !s.Dead)
        ?? threats.GroupBy(x => x.Victim).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault(s => !s.Dead);

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

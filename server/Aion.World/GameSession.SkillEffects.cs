using Aion.Protocol.Native;

namespace Aion.World;

// Class skills beyond their Damage rows: heals, heals over time, buffs, NPC debuffs, damage over time, resurrection, and the NPC
// control effects bots rely on (stun / interrupt, taunt, extra threat; docs/PLAYERBOTS.md), from
// tools/data/skill-effects.json (OURS: class-skills.json carries no heal/abnormal rows yet; see the file's "regenerate"). A cast
// goes through CastReplies like a damage skill (learned, weapon, MP, cooldown), refused when its target is wrong (a heal at a
// monster or an enemy player, a dead or distant ally).
//
// On the wire only what is already known is sent: the healed player's bars (0x8d00 CharPoints, the potions' path) to them, their
// HP (0x8d00 by key) to the caster and those nearby, a HoT's tick as 0x8d00 like the item HoTs, a damage-over-time tick as a
// 0x3804 hit of the skill (CANDIDATE: the official DoT tick is not recorded). A buff without an abnormal_id in the table stays
// server-side: its stats count (Defense, FixingDamage, MPUseDecrease ...) but the client shows no icon (no SkillAbnormal id is
// known for class skills). No heal number is shown (no official heal message is recorded). NPC debuffs (damage taken, slow) are
// server-side too: the slow moves the NPC slower, the client draws it from the move updates.
public sealed partial class GameSession
{
    SkillEffectTable SkillEffectData => options.SkillEffects ?? SkillEffectTable.Embedded;

    /// <summary>A damage-over-time effect this player put on an NPC.</summary>
    sealed class Dot
    {
        public required Target Target { get; init; }
        public required int Skill, EffectId, IntervalMs, PerTick;
        public int TicksLeft;
        public long NextAt;
    }

    readonly List<Dot> dots = [];

    bool DotDue(long now) => dots.Any(d => now >= d.NextAt);

    static int RollRange(int[]? range, Random random) =>
        range is not { Length: > 0 } ? 0 : random.Next(Math.Min(range[0], range[^1]), Math.Max(range[0], range[^1]) + 1);

    /// <summary>The attack heals and dots scale with (Combat.Power, with this player's FixingDamage buffs).</summary>
    int SkillPower() => Combat.Power(data, className, level, equippedWeapon, rng, BuffStat("FixingDamage"));

    /// <summary>
    /// heal = (flat + power x ratio / 10000 + the target's HPMax x hp_ratio / 10000) x the caster's skill-level scale
    /// (progression.json skill_level_damage, the same scale as the skill's damage; all OURS).
    /// </summary>
    int HealAmount(SkillEffect e, int targetHpMax, int skill) =>
        Math.Max(0, (int)Math.Round((RollRange(e.Flat, rng) + (double)SkillPower() * RollRange(e.Ratio, rng) / 10_000 + (double)targetHpMax * RollRange(e.HpRatio, rng) / 10_000)
            * data.Progression.SkillLevelScale(SkillLevel(skill))));

    /// <summary>
    /// The player a friendly skill lands on: another player on the map selected by key (alive, or dead for a resurrection; not an
    /// enemy: the same race or the same party; within heal_range), else this player; null when the selection is a hostile NPC,
    /// an enemy, a player out of reach, or (for a resurrection) nobody dead.
    /// </summary>
    (GameSession Session, WorldPlayer? Player)? FriendlyTarget(int key, bool wantDead)
    {
        if (key != playerKey && worldPlayer?.Map is { } map && map.Players.TryGetValue(key, out var other) && other != worldPlayer)
        {
            var them = other.Session;
            if (other.Hidden || them.resources is null || them.dead != wantDead) return null;
            var partner = Social is { } social && social.PartyOf(worldPlayer) is { } party && party.Members.Contains(other);
            if (!partner && them.profileRace != profileRace) return null;
            var range = SkillEffectData.HealRange;
            if (position is { } at && other.Position.DistanceSquared(at) > range * range) return null;
            return (them, other);
        }
        if (wantDead) return null;
        if (key != playerKey && key >= SharedWorld.FirstPlayerKey) return null;
        if (key != playerKey && targets?.TryGetValue(key, out var npc) == true && npc.Hostile) return null;
        return (this, worldPlayer);
    }

    /// <summary>Whether a cast's target suits its skill-effects entry (skills without one, and damage skills, are the damage path's).</summary>
    bool SkillTargetAllowed(int skill, int targetKey)
    {
        if (SkillEffectData.Of(skill) is not { } set) return true;
        return set.Target switch
        {
            "self" => true,
            "friendly" => FriendlyTarget(targetKey, wantDead: false) is not null,
            "dead_friendly" => FriendlyTarget(targetKey, wantDead: true) is not null,
            "enemy" => data.Skill(skill)?.Damage is { Length: > 0 } || targets?.TryGetValue(targetKey, out var npc) == true && npc.Hostile && !npc.Dead,
            _ => true,
        };
    }

    /// <summary>An accepted cast's skill-effects entry: heals, buffs and resurrection on a friendly player, debuffs and dots on the NPC.</summary>
    List<byte[]> SkillEffectReplies(int skill, int targetKey, long now, Vector3d? location = null)
    {
        if (SkillEffectData.Of(skill) is not { } set || resources is null) return [];
        var replies = new List<byte[]>();
        if (set.Friendly)
        {
            if ((set.Target == "self" ? (this, worldPlayer) : FriendlyTarget(targetKey, set.Target == "dead_friendly")) is not { } to
                || to.Session.resources is not { } theirs) return [];
            // The amounts are the caster's (its power, skill level and random rolls), rolled here on the caster's loop.
            var amounts = set.Effects.Select(e => e.Type switch
            {
                "heal" or "hot" => HealAmount(e, theirs.HpMax, skill),
                "resurrect" => RollRange(e.HpRatio, rng),
                _ => 0,
            }).ToList();
            if (to.Session == this) return ApplyFriendlyEffects(skill, set, amounts, now);
            // Another player: its resources, buffs and death belong to its own loop (WorldTurn), so the effects are applied there
            // (InvokeAsync, as the experience and loot shares are). Its bars go to it; its HP then to the caster and, as a cast
            // packet, to those who see the caster.
            var them = to.Session;
            var key = to.Player?.Key ?? 0;
            var caster = worldPlayer;
            _ = them.InvokeAsync(at =>
            {
                var packets = them.ApplyFriendlyEffects(skill, set, amounts, at);
                if (caster is not null && them.resources is { } r)
                {
                    List<byte[]> hp = [GamePackets.NpcHp(key, r.Hp)];
                    caster.Send(hp);
                    caster.Map?.Broadcast(caster, hp);
                }
                return (true, packets);
            });
            return replies;
        }
        if (targets is null) return [];
        // An area cast (at the caster's own key) puts its effects on every monster the area hit (CombatReplies, AreaRange).
        List<Target> struck = targetKey == playerKey && location is { } at
            ? targets.Values.Where(t => t.Hostile && !t.Dead && t.Position.DistanceSquared(at) <= AreaRange * AreaRange).ToList()
            : targets.TryGetValue(targetKey, out var one) && one.Hostile && !one.Dead ? [one] : [];
        foreach (var npc in struck) replies.AddRange(EnemyEffects(skill, set, npc, now));
        return replies;
    }

    /// <summary>An enemy skill-effects entry on one monster: debuffs, dots, stuns, taunt and threat.</summary>
    List<byte[]> EnemyEffects(int skill, SkillEffectSet set, Target npc, long now)
    {
        var replies = new List<byte[]>();
        var targets = this.targets!;
        foreach (var e in set.Effects)
            switch (e.Type)
            {
                case "debuff":
                    if (e.DamageTaken > 0) (npc.DamageTakenBonus, npc.DamageTakenUntil) = (e.DamageTaken, now + e.DurationMs);
                    if (e.Speed > 0) (npc.SlowRatio, npc.SlowUntil) = (e.Speed, now + e.DurationMs);
                    break;
                case "stun":
                    // Always interrupts a special being wound up; holds the monster unless it is a boss or shook off a stun lately.
                    ai.Interrupt(npc, now);
                    if (npc.Boss)
                    {
                        // A boss is not held by one stun: every BossStaggerStuns stuns break its stagger gauge and it reels for StaggerMs.
                        if (npc.Stunned(now) || ++npc.StaggerStuns < BossStaggerStuns) break;
                        npc.StaggerStuns = 0;
                        npc.StunnedUntil = now + StaggerMs;
                    }
                    else if (e.DurationMs > 0 && now >= npc.StunImmuneUntil)
                    {
                        npc.StunnedUntil = now + e.DurationMs;
                        npc.StunImmuneUntil = now + e.DurationMs * StunImmunityFactor;
                    }
                    break;
                case "interrupt":
                    ai.Interrupt(npc, now);
                    break;
                case "taunt":
                    replies.AddRange(ai.Taunt(targets, npc, now, playerKey));
                    break;
                case "threat":
                    // Extra hate on top of the hit's own (an enmity-raising attack): flat + power x ratio / 10000, a tank bot's multiplier on top.
                    replies.AddRange(ai.Engage(targets, npc, now, playerKey, Threat(Math.Max(1, RollRange(e.Flat, rng) + (int)((long)SkillPower() * RollRange(e.Ratio, rng) / 10_000)))));
                    break;
                case "dot" when e.IntervalMs > 0:
                    dots.RemoveAll(d => d.Target == npc && d.Skill == skill);   // a recast renews it
                    var raw = (RollRange(e.Flat, rng) + (double)SkillPower() * RollRange(e.Ratio, rng) / 10_000) * data.Progression.SkillLevelScale(SkillLevel(skill));
                    dots.Add(new Dot
                    {
                        Target = npc, Skill = skill, EffectId = data.Skill(skill)?.Damage.LastOrDefault()?.EffectId ?? 0, IntervalMs = e.IntervalMs,
                        PerTick = Combat.Mitigate(raw, npc.Defense), TicksLeft = Math.Max(1, e.DurationMs / e.IntervalMs), NextAt = now + e.IntervalMs,
                    });
                    break;
            }
        // A skill with no damage of its own still turns the monster on its caster.
        if (data.Skill(skill)?.Damage is not { Length: > 0 }) replies.AddRange(ai.Engage(targets, npc, now, playerKey, Threat(1)));
        return replies;
    }

    /// <summary>A monster is immune to new stuns for this many times a stun's duration from its start (OURS diminishing return).</summary>
    public const int StunImmunityFactor = 3;
    /// <summary>A boss's stagger (OURS stand-in for the official stagger gauge): this many stuns, then held for StaggerMs.</summary>
    public const int BossStaggerStuns = 3, StaggerMs = 5000;

    /// <summary>A friendly skill's effects on this player, with the amounts the caster rolled (one per effect). On this player's loop.</summary>
    List<byte[]> ApplyFriendlyEffects(int skill, SkillEffectSet set, IReadOnlyList<int> amounts, long now)
    {
        var packets = new List<byte[]>();
        for (var i = 0; i < set.Effects.Length; i++)
        {
            var e = set.Effects[i];
            switch (e.Type)
            {
                case "heal": packets.AddRange(ReceiveHeal(amounts[i])); break;
                case "hot": packets.AddRange(AddSkillBuff(skill, e, amounts[i], now)); break;
                case "buff": packets.AddRange(AddSkillBuff(skill, e, 0, now)); break;
                case "resurrect": packets.AddRange(ReviveBySkill(amounts[i], now)); break;
            }
        }
        return packets;
    }

    /// <summary>A heal lands on this player: HP up to its maximum, the bars (0x8d00) as a potion sends them.</summary>
    List<byte[]> ReceiveHeal(int amount)
    {
        if (resources is not { } r || dead) return [];
        r.Hp = Math.Min(r.HpMax, r.Hp + Math.Max(0, amount));
        return [r.Update()];
    }

    /// <summary>
    /// A class skill's buff on this player (one per skill: a recast replaces it). With an abnormal_id it is announced as the item
    /// buffs are (0x382a); without one it is server-side only. Its stats join the stat sheet; a HoT heals perTick each interval.
    /// </summary>
    List<byte[]> AddSkillBuff(int skill, SkillEffect e, int perTick, long now)
    {
        if (resources is null || dead || e.DurationMs <= 0) return [];
        var replies = new List<byte[]>();
        var speedBefore = BuffStat("MoveSpeed");
        Buff? buff;
        if (e.AbnormalId is { } abnormal) buff = AddBuff(abnormal, e.DurationMs, skill, now, replies);
        else
        {
            replies.AddRange(RemoveBuffs(buffs.Where(b => b.Group == -skill).ToList(), ExpireReplaced));
            buff = new Buff { Uid = ++buffUid, AbnormalId = 0, SkillId = skill, Group = -skill, DurationMs = e.DurationMs, ExpireAt = now + e.DurationMs, Hidden = true };
            buffs.Add(buff);
        }
        if (buff is null) return replies;
        foreach (var (stat, value) in e.Stats ?? []) buff.Stats[stat] = buff.Stats.GetValueOrDefault(stat) + value;
        if (perTick > 0 && e.IntervalMs > 0 && buff.Heals.Count == 0)
        {
            var ticks = Math.Max(1, e.DurationMs / e.IntervalMs);
            buff.Heals.Add(new HealOverTime { EffectId = 0, IntervalMs = e.IntervalMs, PerTick = perTick, Remaining = perTick * ticks, NextAt = now + e.IntervalMs });
        }
        replies.AddRange(BuffStatsChanged(speedBefore));
        return replies;
    }

    /// <summary>A resurrection skill on this dead player: revived where it fell with hpRatio / 10000 of its HP (at least 1).</summary>
    List<byte[]> ReviveBySkill(int hpRatio, long now)
    {
        if (!dead || resources is null) return [];
        var revived = Revive(now, inPlace: true);
        resources.Hp = Math.Clamp((int)((long)resources.HpMax * hpRatio / 10_000), 1, resources.HpMax);
        return [.. revived, resources.Update()];
    }

    /// <summary>Damage-over-time ticks due now; a dot ends with its ticks, its NPC's death, or the NPC no longer on this player's map.</summary>
    List<byte[]> DotTick(long now)
    {
        var replies = new List<byte[]>();
        foreach (var d in dots.ToList())
        {
            if (dead || targets is null || !targets.TryGetValue(d.Target.Key, out var npc) || !ReferenceEquals(npc, d.Target))
            {
                dots.Remove(d);
                continue;
            }
            while (now >= d.NextAt && d.TicksLeft > 0 && !npc.Dead)
            {
                d.TicksLeft--;
                d.NextAt += d.IntervalMs;
                replies.AddRange(HitTarget(npc, d.Skill, [new Hit(d.PerTick, false, d.EffectId)], now));
            }
            if (d.TicksLeft == 0 || npc.Dead) dots.Remove(d);
        }
        return replies;
    }

    /// <summary>The checks: a stat summed over the active buffs, and the damage-over-time effects running.</summary>
    internal int BuffStatOf(string stat) => BuffStat(stat);
    internal int DotCount => dots.Count;
}

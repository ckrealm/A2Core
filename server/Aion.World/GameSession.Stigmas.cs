using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Stigmas, the global way (docs/STIGMAS.md, tools/data/stigmas.json). Each class has its stigma skills on their own tab. They
// are not learned by level: a character from level 22 buys one with Stigma Shards (1 shard, stigma level 1) and raises it to 20
// (1/2/4/8 shards a level by band), and a stigma only casts from a stigma slot (4 open at 22, 27, 32 and 37 on global).
// Shards come from levelling (stigmas.json shard_totals x Aion:Stigmas:ShardRate); nothing about them is stored but the
// stigma levels bought. Reset refunds every shard (OURS: free; global's price is not published).
//
// Messages (CANDIDATE: no official stigma purchase has been captured; the usmap gives the fields):
// - buying and raising: the client's SkillLevelUp_RQ 0x383f {skill}, as for skill points; ESkillAcquireType has Stigma (2) and
//   SkillAcquireData's Stigma rows cost CostStigmaPoint. Learning answers 0x3840, AddSkillList 0x5101 (reason
//   SkillPointLevelUp) and SkillPointUpdate 0x5105 with ESkillPointType StigmaShard (2); a raise answers 0x3840, the level
//   update 0x3813 and the shards.
// - SkillLevelDown_RQ 0x3841 takes one stigma level back; SkillLevelReset_RQ 0x3843 {type Stigma, skill or 0 for all} forgets
//   the stigma (RemoveSkillList 0x5102, the live neighbour of AddSkillList 0x5101 as in the usmap order) and clears its slots.
// - slots: QuickSlotSet_RQ 0x5601 with a stigma skill (EQuickSlotType Stigma 4, or any slot type holding a stigma skill). A
//   stigma beyond the open slots is refused, as the client's MSG_CANNOT_REGISTER_STIGMA_SKILL_BY_OVER_COUNT says.
public sealed partial class GameSession
{
    const byte SkillPointTypeStigmaShard = 2;   // ESkillPointType
    const byte SkillAcquireTypeStigma = 2;      // ESkillAcquireType
    const int QuickSlotTypeStigma = 4;          // EQuickSlotType
    public const ushort RemoveSkillListNt = 0x5102;

    /// <summary>Learned stigmas and their stigma levels.</summary>
    readonly Dictionary<int, int> stigmas = [];

    /// <summary>A playerbot's stigmas, chosen by the server at its level (bots have no stigma window).</summary>
    readonly List<int> botStigmas = [];

    StigmaOptions StigmaRules => options.Stigmas;
    StigmaTable StigmaData => data.Stigmas;

    /// <summary>Stigmas are on and this character's class has some.</summary>
    public bool StigmasOn => StigmaRules.Enabled && StigmaData.ClassStigmas(className).Count > 0;

    public IReadOnlyDictionary<int, int> Stigmas => stigmas;

    /// <summary>A stigma skill of this character's class (stigmas on).</summary>
    bool IsOwnStigma(int skillId) => StigmaRules.Enabled && StigmaData.IsStigmaOf(className, skillId);

    /// <summary>The shards this character has had at its level (levelling grants x ShardRate).</summary>
    public int StigmaShardsTotal => (int)Math.Floor(StigmaData.ShardsAt(level) * StigmaRules.ShardRate + 1e-9);

    public int StigmaShardsSpent => stigmas.Sum(p => StigmaData.Spent(p.Value));

    public int StigmaShardsLeft => Math.Max(0, StigmaShardsTotal - StigmaShardsSpent);

    /// <summary>The stigma slots open at this level (Aion:Stigmas:Slots of them at most).</summary>
    public int StigmaSlotsOpen => StigmasOn ? StigmaData.SlotsAt(level, StigmaRules.Slots) : 0;

    /// <summary>The learned stigmas on a stigma slot now (a bot's are the server's choice).</summary>
    public IReadOnlyList<int> EquippedStigmas => Bot ? botStigmas
        : quickSlots.Values.Select(s => s.SkillId).Where(stigmas.ContainsKey).Distinct().ToList();

    /// <summary>Whether a stigma may be cast: learned and on a slot (unless Aion:Stigmas:RequireSlot is off).</summary>
    bool StigmaCastable(int skillId) =>
        StigmasOn && stigmas.ContainsKey(skillId) && (!StigmaRules.RequireSlot || EquippedStigmas.Contains(skillId));

    /// <summary>The class kit at a level: the level-learned skills, without the stigmas when stigmas are on.</summary>
    IReadOnlyList<(int SkillId, int Level)> LevelKit(string cls, int atLevel)
    {
        var kit = data.LearnedSkills(cls, atLevel);
        return StigmaRules.Enabled ? kit.Where(k => !StigmaData.IsStigmaOf(cls, k.SkillId)).ToList() : kit;
    }

    /// <summary>The learned stigmas for the skill list (skill, stigma level).</summary>
    IEnumerable<(int SkillId, int Level)> StigmaEntries() =>
        StigmasOn ? stigmas.OrderBy(p => p.Key).Select(p => (p.Key, Math.Clamp(p.Value, 1, 255))) : [];

    /// <summary>
    /// A stigma's level expressed on the skill-point level scale that damage and heals use (Progression.SkillLevelScale), so the
    /// stigma's own per-level step (stigmas.json skill_level_damage) applies: 1 + (stigma level - 1) x stigma step / skill step.
    /// </summary>
    int StigmaEquivalentLevel(int stigmaLevel)
    {
        var skillStep = data.Progression.SkillLevelScale(2) - 1;
        var stigmaStep = StigmaData.LevelScale(2) - 1;
        return skillStep <= 0 ? 1 : 1 + (int)Math.Round((stigmaLevel - 1) * stigmaStep / skillStep);
    }

    void LoadStigmas(IReadOnlyList<StigmaState> stored)
    {
        stigmas.Clear();
        foreach (var s in stored)
            if (s.Level >= 1) stigmas[s.SkillId] = Math.Min(s.Level, StigmaData.MaxSkillLevel);
    }

    byte[] StigmaShardUpdate() =>
        PacketWriter.Opcode(SkillPointUpdateNt).Varint(1).U8(1).U8(SkillPointTypeStigmaShard).Varint(StigmaShardsLeft).ToArray();

    byte[] RemoveSkills(IReadOnlyList<int> ids)
    {
        var w = PacketWriter.Opcode(RemoveSkillListNt).Varint(playerKey).U8(ids.Count);
        foreach (var id in ids) w.I32(id);
        return w.ToArray();
    }

    /// <summary>What a stigma's next level needs, or why not: 0 ok, 1 not this class's stigma / at the cap, 2 level or shards short.</summary>
    (int Result, int Next, int Cost) StigmaNext(int skillId)
    {
        if (!StigmasOn || !IsOwnStigma(skillId)) return (1, 0, 0);
        var next = stigmas.GetValueOrDefault(skillId) + 1;
        if (next > Math.Min(StigmaRules.MaxLevel, StigmaData.MaxSkillLevel) || StigmaData.Cost(next) is not { } cost) return (1, next, 0);
        if (level < StigmaData.UnlockLevel || cost > StigmaShardsLeft) return (2, next, cost);
        return (0, next, cost);
    }

    async Task<bool> SaveStigmaAsync(int skillId, int stigmaLevel, CancellationToken ct)
    {
        if (Bot || Store is not { } store || CharacterId is not { } character) return true;
        var saved = await Stored(async () => await store.SetStigmaAsync(character, skillId, stigmaLevel, ct) ? [[]] : []);
        return saved.Count > 0;
    }

    /// <summary>0x383f for a stigma: learn it (level 1) or raise it one level for its Stigma Shards.</summary>
    async Task<List<byte[]>> StigmaLevelUpAsync(int skillId, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelUpRs).U16(result).I32(skillId).ToArray();
        var (result, next, _) = StigmaNext(skillId);
        if (result != 0) return [Reply(result)];
        if (!await SaveStigmaAsync(skillId, next, ct)) return [Reply(1)];
        stigmas[skillId] = next;
        return next == 1
            ? [Reply(0), GamePackets.AddSkillList(playerKey, [(skillId, 1)], SkillPointLevelUp), StigmaShardUpdate()]
            : [Reply(0), SkillLevelUpdate(skillId, next, SkillPointLevelUp), StigmaShardUpdate()];
    }

    /// <summary>0x3841 for a stigma: one stigma level back (not below 1), its shards refunded.</summary>
    async Task<List<byte[]>> StigmaLevelDownAsync(int skillId, CancellationToken ct)
    {
        byte[] Reply(int result) => PacketWriter.Opcode(SkillLevelDownRs).U16(result).I32(skillId).ToArray();
        var current = stigmas.GetValueOrDefault(skillId);
        if (current <= 1) return [Reply(1)];
        if (!await SaveStigmaAsync(skillId, current - 1, ct)) return [Reply(1)];
        stigmas[skillId] = current - 1;
        return [Reply(0), SkillLevelUpdate(skillId, current - 1, SkillPointLevelDown), StigmaShardUpdate()];
    }

    /// <summary>
    /// 0x3843 for stigmas (type Stigma, or a stigma skill): the stigma (every stigma when skill is 0) forgotten, its shards
    /// refunded and its slots cleared. Reply 0x3844 {result, type, skill, modify list {skill → 0}, modify points [StigmaShard]}.
    /// </summary>
    async Task<List<byte[]>> StigmaResetAsync(byte acquireType, int skillId, CancellationToken ct)
    {
        var targets = skillId == 0 ? stigmas.Keys.ToList() : stigmas.ContainsKey(skillId) ? [skillId] : [];
        byte[] Reply(int result, IReadOnlyList<int> reset)
        {
            var w = PacketWriter.Opcode(SkillLevelResetRs).U16(result).U8(acquireType).I32(skillId).Varint((uint)reset.Count);
            foreach (var id in reset) w.I32(id).U8(0);
            return w.Varint(1).U8(SkillPointTypeStigmaShard).I32(StigmaShardsLeft).ToArray();
        }
        if (targets.Count == 0) return [Reply(1, [])];
        var forgotten = new List<int>();
        foreach (var id in targets)
        {
            if (!await SaveStigmaAsync(id, 0, ct)) continue;
            stigmas.Remove(id);
            forgotten.Add(id);
        }
        if (forgotten.Count == 0) return [Reply(1, [])];
        foreach (var slot in quickSlots.Values.Where(s => forgotten.Contains(s.SkillId)).ToList())
            if (Store is not { } store || CharacterId is not { } character
                || (await Stored(async () => await store.SaveQuickSlotAsync(character, slot.Slot, null, ct) ? [[]] : [])).Count > 0)
                quickSlots.Remove(slot.Slot);
        botStigmas.RemoveAll(forgotten.Contains);
        return [Reply(0, forgotten), RemoveSkills(forgotten), StigmaShardUpdate()];
    }

    /// <summary>
    /// Whether a quick slot may hold this skill (0 ok, 1 refused): a stigma must be learned, and a stigma not yet on a slot needs
    /// a free stigma slot. A stigma slot (type 4) only takes a learned stigma.
    /// </summary>
    int StigmaSlotCheck(int slotNumber, int slotType, int skillId)
    {
        if (!StigmasOn) return 0;
        var isStigma = IsOwnStigma(skillId);
        if (!isStigma) return slotType == QuickSlotTypeStigma && skillId != 0 ? 1 : 0;
        if (!stigmas.ContainsKey(skillId)) return 1;
        var others = quickSlots.Values.Where(s => s.Slot != slotNumber).Select(s => s.SkillId).Where(stigmas.ContainsKey).ToHashSet();
        return others.Contains(skillId) || others.Count < StigmaSlotsOpen ? 0 : 1;
    }

    /// <summary>
    /// A playerbot's stigmas (OURS): the class's damage stigmas first, then the rest, as many as its open slots; each bought and
    /// raised with the bot's shards, evenly. Kept in the session only.
    /// </summary>
    void ChooseBotStigmas()
    {
        if (!Bot) return;
        stigmas.Clear();
        botStigmas.Clear();
        if (!StigmasOn) return;
        var picks = StigmaData.ClassStigmas(className)
            .OrderBy(id => data.Skill(id) is { Damage.Length: > 0 } ? 0 : 1).ThenBy(id => id)
            .Take(StigmaSlotsOpen).ToList();
        if (picks.Count == 0 || level < StigmaData.UnlockLevel) return;
        var cap = Math.Min(StigmaRules.MaxLevel, StigmaData.MaxSkillLevel);
        var raised = true;
        while (raised)
        {
            raised = false;
            foreach (var id in picks)
            {
                var next = stigmas.GetValueOrDefault(id) + 1;
                if (next > cap || StigmaData.Cost(next) is not { } cost || cost > StigmaShardsLeft) continue;
                stigmas[id] = next;
                raised = true;
            }
        }
        botStigmas.AddRange(picks.Where(stigmas.ContainsKey));
    }

    /// <summary>The stigma line of the //stigma command: shards, slots and the learned stigmas.</summary>
    public string StigmaSummary()
    {
        if (!StigmaRules.Enabled) return "Stigmas are off on this server (Aion:Stigmas:Enabled).";
        if (!StigmasOn) return "This class has no stigmas.";
        var learnedText = stigmas.Count == 0 ? "none learned"
            : string.Join(", ", stigmas.OrderBy(p => p.Key).Select(p => $"{p.Key} lv{p.Value}{(EquippedStigmas.Contains(p.Key) ? " (slotted)" : "")}"));
        var opens = level < StigmaData.UnlockLevel ? $" The stigma tab opens at level {StigmaData.UnlockLevel}." : "";
        return $"Stigma Shards: {StigmaShardsLeft} of {StigmaShardsTotal}. Slots: {EquippedStigmas.Count}/{StigmaSlotsOpen}. Stigmas: {learnedText}.{opens}";
    }
}

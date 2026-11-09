using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// What playerbots (<see cref="PlayerBots"/>, docs/PLAYERBOTS.md) read from a session, and the one server-side social step they
/// need. A bot is an ordinary session: it moves and casts through <see cref="HandleAsync"/> with the client's own request bodies,
/// so everything others see goes out on the same broadcast paths as for a player. Nothing here sends anything by itself.
/// </summary>
public sealed partial class GameSession
{
    /// <summary>The last NPC this player cast at, and when (the leader's target a bot assists).</summary>
    (int Key, long At) lastAttack;

    /// <summary>OURS: a cast this long ago still names the leader's target (a fight pauses between casts).</summary>
    public const long AssistMemoryMs = 8000;

    /// <summary>The options this session was made with: a bot of this player is made with the same (world, geometry, rates).</summary>
    public GameSessionOptions Options => options;

    /// <summary>Set for a playerbot's session (no connection; driven by <see cref="BotBrain"/>).</summary>
    public bool Bot { get; init; }

    /// <summary>Whether the character knows the skill now (its class kit at its level).</summary>
    public bool Knows(int skillId) => learned.Contains(skillId) || StigmaCastable(skillId);

    /// <summary>When the skill is ready again (0: never used); the cast handler refuses it before (unless NoCool).</summary>
    public long CooldownUntil(int skillId) => noCool ? 0 : cooldowns.GetValueOrDefault(skillId);

    public bool InCombat => inCombat;

    /// <summary>A playerbot's role (tank, dps, heal; null for players), set by its brain: a tank bot looks after the others first.</summary>
    public string? BotRole { get; set; }
    /// <summary>When this player's hit on a monster last landed as a critical (0: never): the window of the crit-only skills.</summary>
    public long LastCriticalAt { get; private set; }

    /// <summary>
    /// OURS: this player's hits count this many times in a monster's hate table (NpcAi.Engage). 1 for players; a tank bot's brain
    /// sets bot-rotations.json tank_hate_multiplier so monsters turn to it. Server-side only: nothing new is sent.
    /// </summary>
    public double HateMultiplier { get; set; } = 1;

    /// <summary>The hate a hit of this much damage adds (at least 1, as NpcAi.Engage counts it).</summary>
    int Threat(int damage) => HateMultiplier == 1 ? damage : (int)Math.Clamp(Math.Round(damage * HateMultiplier), 1, int.MaxValue);

    /// <summary>Whether this player carries the class skill's buff (or heal over time) now.</summary>
    public bool HasSkillBuff(int skillId, long now) => buffs.Any(b => b.SkillId == skillId && b.ExpireAt > now);

    /// <summary>This player's own drops on the ground that it may still pick up (0x5620), with where they lie.</summary>
    public IReadOnlyList<(long Uid, Vector3d At)> LootableDrops(long now) =>
        drops.Values.Where(d => now < d.ExpiresAt).Select(d => (d.Uid, d.At)).ToList();

    /// <summary>Where this player entered its instance copy from (field map, position); null outside a copy.</summary>
    public (int Map, Vector3d At)? InstanceReturn => instanceKey != 0 ? instanceReturn : null;

    /// <summary>Where the character stands: its last accepted position report, else where it entered (a fresh entry has sent none yet).</summary>
    public Vector3d? WorldPosition => position ?? (worldPlayer?.Map is not null ? worldPlayer.Position : spawn?.Position);

    /// <summary>
    /// The living hostile NPC this player is fighting: the target of its last cast within <see cref="AssistMemoryMs"/>, while it is
    /// in combat. Null when it is not fighting anything.
    /// </summary>
    public Target? AttackTarget(long now)
    {
        if (lastAttack.Key == 0 || now - lastAttack.At > AssistMemoryMs || dead) return null;
        return targets?.GetValueOrDefault(lastAttack.Key) is { Dead: false, Hostile: true } target ? target : null;
    }

    /// <summary>
    /// A bot joins its leader's party without the invitation round trip (the leader asked for the bot by command). The messages
    /// are the ones an accepted invitation sends (<see cref="PartyInviteResponse"/>): the leader gets the party (created when it
    /// had none) and every member the new member. False when the leader leads no party it can add to, or the party is full.
    /// Call under the world gate.
    /// </summary>
    public bool JoinPartyAsBot(GameSession leader)
    {
        if (Social is not { } s || worldPlayer is null || leader.worldPlayer is not { } head || s.PartyOf(worldPlayer) is not null) return false;
        var party = s.PartyOf(head);
        if (party is null)
        {
            party = new Party(s.NextId(), head);
            s.Parties[party.Key] = party;
            head.Send(SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Create));
        }
        if (party.Leader != head || party.Members.Count >= Party.Limit) return false;
        party.Members.Add(worldPlayer);
        var member = MemberInfo((byte)(party.Members.Count - 1));
        foreach (var other in party.Others(worldPlayer)) other.Send(SocialPackets.PartyAddMember(member));
        worldPlayer.Send(SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Join));
        return true;
    }

    /// <summary>The party members' sessions (this one included), for bots that look after their group.</summary>
    public IReadOnlyList<GameSession> PartyMembers() =>
        Social is { } s && worldPlayer is not null && s.PartyOf(worldPlayer) is { } party ? party.Members.Select(m => m.Session).ToList() : [this];
}

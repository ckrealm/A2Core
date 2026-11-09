using System.Globalization;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Raid groups (the client's Force: an alliance of parties; Aion:Groups, see GroupOptions). Global AION2's raid Ludra takes
// 10 players, two parties of five. A raid group links parties: each keeps its own party window, loot, heals and buffs; the
// group shares dungeon copies (EnterPartyDungeon) and the raid leader takes in more parties and players. Players form one
// with "/raid" in chat (RaidCommand). With Aion:Groups:ForceWindow the client's Force_* requests are answered and its raid
// window is fed ForceInfo_NT; those opcodes and layouts are CANDIDATE (SocialOpcodes), nothing official was recorded.
public sealed partial class GameSession
{
    /// <summary>A raid invitation stays open this long (OURS).</summary>
    public const long ForceInviteMs = 60_000;

    public const string RaidHelp = "/raid start | invite <name> | accept | decline | leave | kick <name> | leader <name> | disband | list | enter <raid>";

    Force? MyForce => Social is { } s && worldPlayer is not null ? s.ForceOf(worldPlayer) : null;

    /// <summary>
    /// The players this one shares dungeon copies with: its raid group, else its party, else none. Bots count like players
    /// (they are party members).
    /// </summary>
    IEnumerable<WorldPlayer> GroupMates()
    {
        if (Social is not { } s || worldPlayer is null) return [];
        if (s.ForceOf(worldPlayer) is { } force) return force.Members.Where(m => m != worldPlayer).ToList();
        return s.PartyOf(worldPlayer) is { } party ? party.Others(worldPlayer).ToList() : [];
    }

    ForceInfo ForceView(Force force) => new()
    {
        Key = force.Key, LeaderDbId = force.Leader.Session.DbId, LimitMember = options.Groups.ForceSize,
        Parties = force.Parties.Select((p, i) => new ForcePartyInfo(p.Key, (byte)i, p.Limit,
            p.Members.Select((m, n) => m.Session.MemberInfo((byte)n)).ToList())).ToList(),
    };

    /// <summary>Everyone in the group sees it again (only with the raid window; the /raid list text is always there).</summary>
    void ForceChanged(Force force, PartyUpdateReason reason)
    {
        if (!options.Groups.ForceWindow) return;
        var info = SocialPackets.ForceInfo(ForceView(force), reason);
        foreach (var m in force.Members) m.Send(info);
    }

    /// <summary>
    /// After a party of the group lost members or ended: ended or empty parties leave the group, <paramref name="gone"/> are
    /// told they are out, a new raid leader replaces one who left, and a group left with nobody, or one player after a
    /// departure, ends.
    /// </summary>
    void ForceRefresh(Force force, PartyUpdateReason reason, IEnumerable<WorldPlayer> gone)
    {
        if (Social is not { } s || !s.Forces.ContainsKey(force.Key)) return;
        force.Parties.RemoveAll(p => !s.Parties.ContainsKey(p.Key) || p.Members.Count == 0);
        var left = gone.Where(g => !force.Has(g)).ToList();
        if (options.Groups.ForceWindow) foreach (var g in left) g.Send(SocialPackets.ForceLeaveNotice(force.Key, reason));
        if (force.Count == 0 || force.Count == 1 && left.Count > 0)
        {
            EndForce(force);
            return;
        }
        if (!force.Has(force.Leader))
        {
            force.Leader = force.Members.FirstOrDefault(m => !m.Session.Bot) ?? force.Parties[0].Leader;
            if (options.Groups.ForceWindow) foreach (var m in force.Members) m.Send(SocialPackets.ForceNewLeader(force.Leader.Session.DbId));
        }
        ForceChanged(force, reason);
    }

    /// <summary>The group ends: its parties go on alone (a party of one ends too), its pending invitations lapse.</summary>
    void EndForce(Force force)
    {
        if (Social is not { } s) return;
        s.Forces.Remove(force.Key);
        foreach (var key in s.ForceInvites.Where(i => i.Value.ForceKey == force.Key).Select(i => i.Key).ToList()) s.ForceInvites.Remove(key);
        foreach (var party in force.Parties.ToList())
        {
            if (options.Groups.ForceWindow) foreach (var m in party.Members) m.Send(SocialPackets.ForceDisband());
            if (party.Members.Count == 1 && s.Parties.ContainsKey(party.Key)) DisbandParty(party, PartyUpdateReason.LeaveMember);
        }
    }

    /// <summary>
    /// A raid group from this player's party (a party of one is made for a player without one); only the party leader may.
    /// The replies (a new party's PartyInfo) and why it failed.
    /// </summary>
    (Force? Force, List<byte[]> Replies, string? Why) ForceStart()
    {
        if (Social is not { } s || worldPlayer is null) return (null, [], "Enter the world first.");
        if (s.ForceOf(worldPlayer) is { } existing) return (existing, [], null);
        List<byte[]> replies = [];
        var party = s.PartyOf(worldPlayer);
        if (party is null)
        {
            party = NewParty(s, worldPlayer);
            replies.Add(SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Create));
        }
        else if (party.Leader != worldPlayer) return (null, [], $"Only your party leader ({party.Leader.Name}) can start a raid group.");
        var force = new Force(s.NextId(), worldPlayer, party);
        s.Forces[force.Key] = force;
        ForceChanged(force, PartyUpdateReason.PartyToForce);
        return (force, replies, null);
    }

    /// <summary>
    /// The raid leader invites a player by name (a group is started when there is none). A party leader is asked to bring
    /// the whole party; a player without a party gets a seat in a party of the group with room (or a new party). The invitee
    /// answers in the invitation window or with /raid accept. Returns the replies and the text for the inviter.
    /// </summary>
    (bool Sent, List<byte[]> Replies, string Text) ForceInviteByName(string name, long now)
    {
        if (Social is not { } s || worldPlayer is null || World is not { } world) return (false, [], "Enter the world first.");
        if (s.ForceOf(worldPlayer) is { } current && current.Leader != worldPlayer) return (false, [], $"Only the raid leader ({current.Leader.Name}) invites.");
        var invitee = world.FindOnline(CharacterNames.Stored(name.Trim())) ?? world.FindOnline(name.Trim());
        if (invitee is null) return (false, [], $"{name} is not online.");
        if (invitee == worldPlayer) return (false, [], "You are already in your raid group.");
        if (s.ForceOf(invitee) is not null) return (false, [], $"{invitee.Name} is already in a raid group.");
        var theirs = s.PartyOf(invitee);
        if (theirs is not null && theirs.Leader != invitee)
            return (false, [], $"{invitee.Name} is in {theirs.Leader.Name}'s party: invite {theirs.Leader.Name} to bring the whole party.");
        if (theirs is not null && theirs.Members.Contains(worldPlayer)) return (false, [], $"{invitee.Name} is in your party already.");
        var (force, replies, why) = ForceStart();
        if (force is null) return (false, replies, why!);
        if (force.Leader != worldPlayer) return (false, replies, $"Only the raid leader ({force.Leader.Name}) invites.");
        if (ForceRoomFor(force, theirs?.Members.Count ?? 1, theirs is not null) is { } full) return (false, replies, full);
        var expires = now + ForceInviteMs;
        s.ForceInvites[invitee.Key] = new ForceInvite(force.Key, playerKey, expires);
        // The invitation window: the Force one when it is on; otherwise the party one, which only a player without a party
        // can answer (a party leader in a party would be refused by its own client), so party leaders type /raid accept.
        if (options.Groups.ForceWindow) invitee.Send(SocialPackets.ForceInviteNotice(force.Key, DbId, Invitation(), expires));
        else if (theirs is null) invitee.Send(SocialPackets.PartyInviteNotice(force.Key, DbId, Invitation(), expires));
        var who = theirs is null ? invitee.Name : $"{invitee.Name}'s party ({theirs.Members.Count})";
        return (true, replies, $"Invited {who} to the raid group. They accept in the invitation window or type /raid accept (within {ForceInviteMs / 1000} s).");
    }

    /// <summary>Null when the group can take <paramref name="count"/> more players (a whole party when <paramref name="asParty"/>), else why not.</summary>
    string? ForceRoomFor(Force force, int count, bool asParty)
    {
        var groups = options.Groups;
        if (force.Count + count > groups.ForceSize) return $"The raid group is full ({force.Count}/{groups.ForceSize}).";
        if (asParty && force.Parties.Count >= groups.ForceParties) return $"The raid group has {groups.ForceParties} parties already (Aion:Groups:ForceParties).";
        if (!asParty && force.Parties.Count >= groups.ForceParties && force.Parties.All(p => p.Members.Count >= p.Limit || p.Leader.Session.Bot)) return "Every party of the raid group is full.";
        return null;
    }

    /// <summary>
    /// The pending raid invitation accepted: this player's party joins the group (when this one leads it), or this player
    /// takes a seat in a party of the group. Null (and why) when it cannot.
    /// </summary>
    internal List<byte[]>? ForceAccept(long now, out string text)
    {
        text = "";
        if (Social is not { } s || worldPlayer is null) { text = "Enter the world first."; return null; }
        if (!s.ForceInvites.Remove(playerKey, out var invite) || invite.ExpiresMs < now) { text = "You have no raid invitation (or it expired)."; return null; }
        if (!s.Forces.TryGetValue(invite.ForceKey, out var force)) { text = "That raid group has ended."; return null; }
        var inviter = World?.Online.GetValueOrDefault(invite.InviterKey);
        byte[] Told(ForceInviteAnswer answer) => options.Groups.ForceWindow
            ? SocialPackets.ForceInviteResponseNotice(DbId, ServerId, StoredName, answer)
            : SocialPackets.PartyInviteResponseNotice(DbId, ServerId, StoredName, answer == ForceInviteAnswer.Accept ? PartyInviteAnswer.Accept : PartyInviteAnswer.Refusal);
        if (s.ForceOf(worldPlayer) is not null)
        {
            inviter?.Send(Told(ForceInviteAnswer.RefusalAlreadyInForce));
            text = "You are in a raid group already.";
            return null;
        }
        var mine = s.PartyOf(worldPlayer);
        if (mine is not null && mine.Leader != worldPlayer)
        {
            inviter?.Send(Told(ForceInviteAnswer.RefusalAlreadyInParty));
            text = $"You are in {mine.Leader.Name}'s party: only {mine.Leader.Name} can bring it into a raid group.";
            return null;
        }
        if (ForceRoomFor(force, mine?.Members.Count ?? 1, mine is not null) is { } full)
        {
            text = full;
            return null;
        }
        inviter?.Send(Told(ForceInviteAnswer.Accept));
        List<byte[]> replies = [];
        if (mine is not null)
        {
            force.Parties.Add(mine);
            ForceChanged(force, PartyUpdateReason.ForceJoin);
            text = $"Your party joined {force.Leader.Name}'s raid group ({force.Count} players).";
        }
        else if (force.Parties.FirstOrDefault(p => p.Members.Count < p.Limit && !p.Leader.Session.Bot) is { } seat)   // never under a bot leader
        {
            replies.AddRange(JoinParty(seat));
            text = $"You joined {seat.Leader.Name}'s party in {force.Leader.Name}'s raid group ({force.Count} players).";
        }
        else
        {
            var party = NewParty(s, worldPlayer);
            force.Parties.Add(party);
            replies.Add(SocialPackets.PartyInfo(Info(party), PartyUpdateReason.Create));
            ForceChanged(force, PartyUpdateReason.ForceJoin);
            text = $"You lead party {force.Parties.Count} of {force.Leader.Name}'s raid group ({force.Count} players).";
        }
        return replies;
    }

    /// <summary>The pending raid invitation refused: the inviter is told.</summary>
    List<byte[]> ForceDecline(ForceInviteAnswer answer)
    {
        if (Social is not { } s || !s.ForceInvites.Remove(playerKey, out var invite)) return [];
        var told = options.Groups.ForceWindow ? SocialPackets.ForceInviteResponseNotice(DbId, ServerId, StoredName, answer)
            : SocialPackets.PartyInviteResponseNotice(DbId, ServerId, StoredName, PartyInviteAnswer.Refusal);
        World?.Online.GetValueOrDefault(invite.InviterKey)?.Send(told);
        return [];
    }

    /// <summary>This player leaves the raid group, and with it the party it sat in.</summary>
    List<byte[]> ForceLeave(List<byte[]> replies)
    {
        if (MyForce is null) return replies;
        return PartyLeave(PartyUpdateReason.LeaveMember, replies);
    }

    /// <summary>The raid leader removes a member of the group (from its party). Why not, or null.</summary>
    string? ForceKick(WorldPlayer kicked)
    {
        if (Social is not { } s || worldPlayer is null || s.ForceOf(worldPlayer) is not { } force) return "You are not in a raid group.";
        if (force.Leader != worldPlayer) return $"Only the raid leader ({force.Leader.Name}) removes members.";
        if (kicked == worldPlayer) return "Use /raid leave to leave.";
        if (!force.Has(kicked) || s.PartyOf(kicked) is not { } party) return $"{kicked.Name} is not in your raid group.";
        if (options.Groups.ForceWindow) kicked.Send(SocialPackets.ForceKickout(false));
        RemoveFromParty(party, kicked);
        return null;
    }

    /// <summary>The raid leader hands the group to another member. Why not, or null.</summary>
    string? ForceSetLeader(WorldPlayer leader)
    {
        if (Social is not { } s || worldPlayer is null || s.ForceOf(worldPlayer) is not { } force) return "You are not in a raid group.";
        if (force.Leader != worldPlayer) return $"Only the raid leader ({force.Leader.Name}) hands over the lead.";
        if (!force.Has(leader)) return $"{leader.Name} is not in your raid group.";
        if (leader.Session.Bot) return "A bot cannot lead the raid group.";
        force.Leader = leader;
        if (options.Groups.ForceWindow) foreach (var m in force.Members) m.Send(SocialPackets.ForceNewLeader(leader.Session.DbId));
        return null;
    }

    /// <summary>The raid leader ends the group; the parties stay.</summary>
    string? ForceDisband()
    {
        if (Social is not { } s || worldPlayer is null || s.ForceOf(worldPlayer) is not { } force) return "You are not in a raid group.";
        if (force.Leader != worldPlayer) return $"Only the raid leader ({force.Leader.Name}) disbands the group.";
        EndForce(force);
        return null;
    }

    /// <summary>The group as text: each party, its leader first, then the members.</summary>
    string ForceText(Force force)
    {
        var parts = force.Parties.Select((p, i) => $"party {i + 1} ({p.Members.Count}/{p.Limit}): "
            + string.Join(", ", p.Members.Select(m => (m == p.Leader ? "*" : "") + m.Name + (m.Session.Bot ? " (bot)" : ""))));
        return $"Raid group of {force.Leader.Name}, {force.Count}/{options.Groups.ForceSize} players. " + string.Join("; ", parts) + ". * = party leader.";
    }

    /// <summary>The raids (Dungeon.dat type Raid) this host serves, for /raid enter.</summary>
    static IReadOnlyList<(int Id, DungeonInfo Room)> Raids() =>
        MapEventData.DungeonsOfType("Raid").Where(d => MapEventData.Serves(d.Room.Map)).ToList();

    /// <summary>A raid by id, name or a part of its name (case ignored).</summary>
    static (int Id, DungeonInfo Room)? RaidNamed(string text)
    {
        var raids = Raids();
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return raids.FirstOrDefault(r => r.Id == id) is { Room: not null } byId ? byId : null;
        return raids.FirstOrDefault(r => string.Equals(r.Room.Name, text, StringComparison.OrdinalIgnoreCase)) is { Room: not null } exact ? exact
            : raids.FirstOrDefault(r => r.Room.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) is { Room: not null } part ? part : null;
    }

    /// <summary>OURS: the names players know a raid by (global AION2), for the list.</summary>
    static string RaidTitle(DungeonInfo room) => room.Name switch
    {
        "ReforgedAbyss_Normal" => "Ludra (Abyssal Forge)",
        "ErosionPurifier_01" => "Corroded Decontamination Facility (Korea only)",
        "MusphelHolyGrail_01" or "MusphelHolyGrail_02" => "Chalice of Muspel (Korea only)",
        _ => room.Name,
    };

    /// <summary>
    /// /raid enter: into a raid's copy. The official client opens raids through a window the recordings do not hold, so
    /// this is the way in. A member of the group already inside takes this one into the same copy while it has room
    /// (MapEntrance MaxMember, 10 for Ludra); the entry rules (level, item level) apply to everyone but staff.
    /// </summary>
    (List<byte[]> Replies, string Text) RaidEnter(string which, long now)
    {
        var raids = Raids();
        if (which.Length == 0)
            return ([], "Raids: " + string.Join(", ", raids.Select(r => $"{r.Room.Name} = {RaidTitle(r.Room)} (id {r.Id}, {r.Room.MaxMember} players, level {r.Room.MinLevel}, item level {r.Room.ItemLevel})"))
                + ". Use /raid enter <name or id>.");
        if (RaidNamed(which) is not { } raid) return ([], $"No raid called \"{which}\". Use /raid enter to list them.");
        if (instanceKey != 0) return ([], "Leave the dungeon you are in first.");
        if (!IsAdmin && !MeetsEntry(raid.Room, out var why)) return ([], $"{RaidTitle(raid.Room)} refuses you: {why}.");
        var moved = EnterPartyDungeon([raid.Id], now);
        if (moved.Count == 0) return ([], $"Could not enter {RaidTitle(raid.Room)} (a daily entry limit, or you are busy or dead).");
        var mates = GroupMates().Count(m => m.Map is { Instance: not 0 } inside && inside.MapId == raid.Room.Map);
        return (moved, mates > 0 ? $"Joining your group in {RaidTitle(raid.Room)}." : $"Entering {RaidTitle(raid.Room)}. Your raid group joins with /raid enter {raid.Room.Name}; bots follow their leader.");
    }

    /// <summary>"/raid ..." from chat (every player). Runs on this session's game loop; the reply text and packets.</summary>
    public (string Text, List<byte[]> Packets) RaidCommand(string args, long now)
    {
        if (Social is not { } s || worldPlayer is null || World is not { } world) return ("Enter the world first.", []);
        var words = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = words.Length > 0 ? words[0].ToLowerInvariant() : "list";
        var rest = words.Length > 1 ? words[1] : "";
        WorldPlayer? Named() => rest.Length == 0 ? null : world.FindOnline(CharacterNames.Stored(rest)) ?? world.FindOnline(rest);
        switch (verb)
        {
            case "start" or "create" or "form":
            {
                var (started, replies, why) = ForceStart();
                return started is null ? (why!, replies) : (started.Leader == worldPlayer
                    ? $"Raid group started ({started.Count}/{options.Groups.ForceSize}). Invite with /raid invite <name>." : ForceText(started), replies);
            }
            case "invite" or "inv" or "add":
            {
                if (rest.Length == 0) return ("/raid invite <character name>", []);
                var (_, replies, text) = ForceInviteByName(rest, now);
                return (text, replies);
            }
            case "accept" or "join" or "yes":
            {
                var replies = ForceAccept(now, out var text);
                return (text, replies ?? []);
            }
            case "decline" or "refuse" or "no":
                return s.ForceInvites.ContainsKey(playerKey) ? ("Raid invitation declined.", ForceDecline(ForceInviteAnswer.Refusal)) : ("You have no raid invitation.", []);
            case "leave" or "quit":
                return MyForce is null ? ("You are not in a raid group.", []) : ("You left the raid group and your party in it.", ForceLeave([]));
            case "kick" or "remove":
                return Named() is { } kicked ? (ForceKick(kicked) ?? $"{kicked.Name} was removed from the raid group.", []) : ("/raid kick <character name> (online)", []);
            case "leader" or "lead":
                return Named() is { } leader ? (ForceSetLeader(leader) ?? $"{leader.Name} leads the raid group now.", []) : ("/raid leader <character name> (online)", []);
            case "disband" or "end":
                return (ForceDisband() ?? "The raid group ended; the parties stay together.", []);
            case "list" or "info" or "who":
                return (MyForce is { } mine ? ForceText(mine)
                    : s.ForceInvites.TryGetValue(playerKey, out var invite) && s.Forces.TryGetValue(invite.ForceKey, out var inviting)
                        ? $"You are invited to {inviting.Leader.Name}'s raid group: /raid accept or /raid decline."
                        : $"You are not in a raid group. {RaidHelp}", []);
            case "enter" or "go":
            {
                var (replies, text) = RaidEnter(rest, now);
                return (text, replies);
            }
            default:
                return (RaidHelp, []);
        }
    }

    // ---------- The client's Force requests (Aion:Groups:ForceWindow) ----------

    /// <summary>A Force request from the client's raid window, or null when it is not one (or the window is off).</summary>
    List<byte[]>? ForceRequest(byte[] plain, long now)
    {
        var header = Opcode(plain);
        if (!options.Groups.ForceWindow || !SocialOpcodes.ForceRequests.Contains(header) || resources is null) return null;
        WorldPlayer? ByDbId(long dbId) => MyForce?.Members.FirstOrDefault(m => m.Session.DbId == dbId);
        switch (header)
        {
            case SocialOpcodes.ForceCreateRq:
            {
                var (force, replies, _) = ForceStart();
                return [SocialPackets.ForceCreateReply(force is null ? Failed : 0), .. replies];
            }
            case SocialOpcodes.ForceInviteRq:
            {
                if (SocialRequests.ParsePartyInvite(plain) is not { } target) return [];
                var (sent, replies, _) = ForceInviteByName(target.Name, now);
                return [SocialPackets.ForceInviteReply(sent ? 0 : Failed, ServerId, target.Name), .. replies];
            }
            case SocialOpcodes.ForceInviteResponseRq:
            {
                if (SocialRequests.ParseForceInviteResponse(plain) is not { } answer) return [SocialPackets.ForceInviteResponseReply(Failed)];
                if (answer.Response != ForceInviteAnswer.Accept) return [SocialPackets.ForceInviteResponseReply(0), .. ForceDecline(answer.Response)];
                var joined = ForceAccept(now, out _);
                return [SocialPackets.ForceInviteResponseReply(joined is null ? Failed : 0), .. joined ?? []];
            }
            case SocialOpcodes.ForceLeaveRq:
                return MyForce is null ? [SocialPackets.ForceLeaveReply(Failed)] : ForceLeave([SocialPackets.ForceLeaveReply(0)]);
            case SocialOpcodes.ForceKickoutRq:
                return [SocialPackets.ForceKickoutReply(SocialRequests.ParseDbId(plain) is { } kicked && ByDbId(kicked) is { } who && ForceKick(who) is null ? 0 : Failed)];
            case SocialOpcodes.ForceSetLeaderRq:
                return [SocialPackets.ForceSetLeaderReply(SocialRequests.ParseDbId(plain) is { } lead && ByDbId(lead) is { } next && ForceSetLeader(next) is null ? 0 : Failed)];
            default:
                return [];
        }
    }
}

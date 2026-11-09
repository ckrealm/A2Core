using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Groups: group sizes. Global AION2 raised them at its launch (NCSoft's global livestream, 2026-08-08): parties and
/// expedition (Party) dungeons from 4 to 5 players, the first raid from 8 to 10. The 2.0.6.0 client data agrees: 53 of the
/// 55 Party dungeons have MapEntrance MaxMember 5, the four Raid rows (Ludra = ReforgedAbyss_Normal, ...) 10, and the
/// largest matched field events 20. So the defaults are a party of <see cref="PartySize"/> 5 and a raid group (the client's
/// "Force", an alliance of parties) of up to <see cref="ForceParties"/> 4 parties; a raid's dungeon copy still takes only its
/// MaxMember (10 for Ludra). <see cref="ForceWindow"/> sends the client's Force_* messages so its raid window can show the
/// group; their opcodes and layouts are CANDIDATE (usmap layouts, opcodes from the 0x95 run anchored by the OBSERVED
/// SocialActionStart/End_NT 0x9507/0x9508), so it is off by default and raids are formed with the /raid commands.
/// </summary>
public sealed record GroupOptions
{
    public int PartySize { get; init; } = DefaultPartySize;
    public int ForceParties { get; init; } = DefaultForceParties;
    public bool ForceWindow { get; init; }

    public const int DefaultPartySize = 5, DefaultForceParties = 4, MinPartySize = 2, MaxPartySize = 6, MinForceParties = 2, MaxForceParties = 8;
    public static GroupOptions Default { get; } = new();

    /// <summary>The most members a raid group may have.</summary>
    public int ForceSize => PartySize * ForceParties;

    public GroupOptions Validated()
    {
        if (PartySize is < MinPartySize or > MaxPartySize)
            throw new InvalidOperationException($"Aion:Groups:PartySize must be between {MinPartySize} and {MaxPartySize} (got {PartySize}; global AION2 uses 5).");
        if (ForceParties is < MinForceParties or > MaxForceParties)
            throw new InvalidOperationException($"Aion:Groups:ForceParties must be between {MinForceParties} and {MaxForceParties} (got {ForceParties}).");
        return this;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"party {PartySize}, raid group {ForceParties} parties ({ForceSize}), raid window {(ForceWindow ? "on" : "off")}");
}

/// <summary>
/// A raid group (the client's Force) on this host, in memory: parties in the order they joined, and the raid leader. Each
/// party stays a normal <see cref="Party"/> (its own window, loot, heals and buffs); the group shares dungeon copies (raids)
/// and the raid leader takes in more parties and players. Use under the world gate.
/// </summary>
public sealed class Force(long key, WorldPlayer leader, Party first)
{
    public long Key { get; } = key;
    public WorldPlayer Leader { get; set; } = leader;
    public List<Party> Parties { get; } = [first];
    public IEnumerable<WorldPlayer> Members => Parties.SelectMany(p => p.Members);
    public int Count => Parties.Sum(p => p.Members.Count);
    public bool Has(WorldPlayer p) => Parties.Any(x => x.Members.Contains(p));
    /// <summary>The party's number in the group (0-based, join order).</summary>
    public int NumberOf(Party party) => Parties.IndexOf(party);
}

/// <summary>A pending raid invitation of a party leader (their whole party joins): by invited player key.</summary>
public sealed record ForceInvite(long ForceKey, int InviterKey, long ExpiresMs);

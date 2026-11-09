namespace Aion.World;

/// <summary>
/// Aion:Alts (OURS: a server owner's choice for a friends server where players run several characters; docs/friends-server/ALTS.md).
/// Both switches off reproduce the behaviour before them: a warehouse per character and no player mail.
/// <para>
/// <see cref="SharedWarehouse"/>: the Storage NPCs' depot (EItemContainerType kStorage = 2, what ItemMove_RQ and the item list
/// already use) is one warehouse for all of the account's characters on the world (location 'account_storage', migration 040).
/// The client's enum also has kCharacterStorage (15), so kStorage being the account warehouse is CANDIDATE (from the names;
/// official Aion 2 has an account warehouse); container 15 is not served. A character's old per-character warehouse joins the
/// account warehouse at its next load (slots kept where free). Soul-bound items stay out, as in a trade.
/// </para>
/// <para>
/// <see cref="PlayerMail"/>: the client (5.3.2.0) has a write-mail popup and its error strings but no send request in its packet
/// catalogue (only LoadPost/ReadPost/DeletePost/TakePost and RecvPost_NT), so players send mail with chat commands:
/// //mail &lt;name&gt; &lt;subject&gt; | &lt;text&gt;, //sendkinah &lt;name&gt; &lt;amount&gt; [note], //senditem &lt;name&gt; &lt;bag slot&gt; [count].
/// It arrives as an ordinary post (the staff-mail template). <see cref="MailFee"/> Kinah is paid per mail (default 0).
/// </para>
/// </summary>
public sealed record AltRules
{
    public bool SharedWarehouse { get; init; } = true;
    public bool PlayerMail { get; init; } = true;
    /// <summary>Kinah paid by the sender for each player mail (OURS; 0: free).</summary>
    public long MailFee { get; init; }
    /// <summary>Days a player mail waits before it expires (OURS; staff mail keeps its 30 days). Items attached wait with it.</summary>
    public int MailDays { get; init; } = DefaultMailDays;

    public const int DefaultMailDays = 365, MaxMailDays = 3650, MaxSubject = 40, MaxBody = 500;
    public const long MaxMailFee = 1_000_000_000;
    public static AltRules Default { get; } = new();
    /// <summary>Both switches off: the behaviour before Aion:Alts.</summary>
    public static AltRules Off { get; } = new() { SharedWarehouse = false, PlayerMail = false };

    public AltRules Validated()
    {
        if (MailFee is < 0 or > MaxMailFee) throw new InvalidOperationException($"Aion:Alts:MailFee must be between 0 and {MaxMailFee} (got {MailFee}).");
        if (MailDays is < 1 or > MaxMailDays) throw new InvalidOperationException($"Aion:Alts:MailDays must be between 1 and {MaxMailDays} (got {MailDays}).");
        return this;
    }

    public override string ToString() =>
        $"warehouse {(SharedWarehouse ? "shared by the account" : "per character")}, player mail {(PlayerMail ? $"on (fee {MailFee} Kinah, kept {MailDays} days)" : "off")}";
}

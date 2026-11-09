using System.Globalization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Aion:Alts (OURS; AltRules.cs, docs/friends-server/ALTS.md): the account warehouse and player mail.
public sealed partial class GameSession
{
    /// <summary>The host's Aion:Alts switches.</summary>
    public AltRules Alts => options.Alts;

    /// <summary>
    /// The stored rows the item list is laid out from. With SharedWarehouse the character's own warehouse rows give way to the
    /// account's warehouse (its own rows merged in by the store first), held here as location "storage" so the warehouse
    /// container (2) and ItemMove_RQ work as before; off, the character's rows as they are (account rows are not shown).
    /// </summary>
    async Task<IReadOnlyList<ItemState>> InventoryRowsAsync(INativeCharacterStore store, Guid character, CancellationToken ct)
    {
        if (!options.Alts.SharedWarehouse) return await store.InventoryAsync(character, ct);
        var warehouse = await store.AccountWarehouseAsync(character, ct);
        var own = await store.InventoryAsync(character, ct);
        return [.. own.Where(r => r.Location is not ("storage" or "account_storage")), .. warehouse.Select(r => r with { Location = "storage" })];
    }

    /// <summary>The chat commands of player mail (//mail with a '|', //sendkinah, //senditem).</summary>
    public static readonly IReadOnlyList<string> MailCommands = ["mail", "sendkinah", "senditem"];

    public const string MailUsage = "//mail <name> <subject> | <text>, //sendkinah <name> <amount> [note], //senditem <name> <bag slot> [count] (//senditem alone lists your bag)";

    /// <summary>
    /// Player mail by chat command (Aion:Alts:PlayerMail): the client has no send request (see AltRules), so the sender types
    /// the mail and it arrives as an ordinary post (Post.dat 106, as staff mail; the title says who sent it). Bag slots count
    /// from 1 in the order the bag lays items out (wire slot + 1); a number of 1000 or more is an item id (the first bag stack
    /// of it). Returns the reply to show and the sender's inventory changes.
    /// </summary>
    public async Task<(string Message, List<byte[]> Packets)> PlayerMailAsync(string command, string args, CancellationToken ct)
    {
        if (!options.Alts.PlayerMail) return ("Player mail is off on this server.", []);
        if (Store is not { } store || CharacterId is not { } character || CharacterName is not { } me) return ("Mail needs a stored character.", []);
        var words = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fee = options.Alts.MailFee;
        string subject, body;
        PlayerPost post;
        switch (command.ToLowerInvariant())
        {
            case "mail":
            {
                var bar = words.Length == 2 ? words[1].IndexOf('|') : -1;
                if (bar < 0) return ("Usage: //mail <name> <subject> | <text>", []);
                subject = words[1][..bar].Trim();
                body = words[1][(bar + 1)..].Trim();
                if (subject.Length == 0 || body.Length == 0) return ("Write a subject and a text: //mail <name> <subject> | <text>", []);
                post = new PlayerPost(words[0], StaffPostDataId, "");
                break;
            }
            case "sendkinah":
            {
                var rest = words.Length == 2 ? words[1].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
                if (rest.Length == 0 || !long.TryParse(rest[0].Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var kinah) || kinah < 1)
                    return ("Usage: //sendkinah <name> <amount> [note]", []);
                subject = $"{kinah:N0} Kinah";
                body = rest.Length > 1 ? rest[1] : $"{me} sent you {kinah:N0} Kinah.";
                post = new PlayerPost(words[0], StaffPostDataId, "", Kinah: kinah);
                break;
            }
            case "senditem":
            {
                if (words.Length == 0) return (BagListing(), []);
                var rest = words.Length == 2 ? words[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                if (rest.Length is 0 or > 2 || !int.TryParse(rest[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var which) || which < 1)
                    return ("Usage: //senditem <name> <bag slot> [count] (//senditem alone lists your bag)", []);
                long count = 0;
                if (rest.Length == 2 && (!long.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1))
                    return ("The count must be 1 or more.", []);
                var shown = which >= 1000
                    ? inventory.Values.Where(i => i.Container == InventoryItem.Bag && i.ItemId == which).OrderBy(i => i.Slot).FirstOrDefault()
                    : inventory.Values.FirstOrDefault(i => i.Container == InventoryItem.Bag && i.Slot == which - 1);
                if (shown is null || !storedItems.TryGetValue(shown.Key, out var row) || row.Location != "bag")
                    return (which >= 1000 ? $"No item {which} in your bag." : $"Bag slot {which} is empty. //senditem alone lists your bag.", []);
                subject = count > 0 ? $"Item {row.TemplateId} x{count}" : $"Item {row.TemplateId} x{row.Quantity}";
                body = $"{me} sent you an item.";
                post = new PlayerPost(words[0], StaffPostDataId, "", row.InstanceId, row.Revision, count);
                break;
            }
            default: return ("Usage: " + MailUsage, []);
        }
        if (subject.Length > AltRules.MaxSubject) subject = subject[..AltRules.MaxSubject];
        if (body.Length > AltRules.MaxBody) return ($"The text is too long (at most {AltRules.MaxBody} characters).", []);
        // Post.dat 106 shows "{0}" as the title and "{1}" as the body, the parameters split at '|' (CANDIDATE, as staff mail).
        var title = $"[{me}] {subject}".Replace('|', '/');
        post = post with { Parameters = $"{title}|{body.Replace('|', '/')}", Fee = fee, Days = options.Alts.MailDays };
        var sent = await StoredValue(() => store.SendPlayerPostAsync(character, post, ct), new PlayerPostResult("failed"));
        var message = sent.Result switch
        {
            "ok" => $"Mail sent to {post.To}." + (fee > 0 ? $" Fee: {fee:N0} Kinah." : ""),
            "unknown" => $"There is no character named {post.To} on this server.",
            "self" => "You cannot mail yourself.",
            "kinah" => fee > 0 ? $"Not enough Kinah (the mail costs {post.Kinah + fee:N0} with the {fee:N0} fee)." : "Not enough Kinah.",
            "bound" => "Soul-bound items cannot be mailed.",
            "count" => "You do not have that many.",
            "item" => "That item changed; try again.",
            _ => "The mail could not be sent.",
        };
        if (sent is not { Result: "ok", Post: { } stored }) return (message, []);
        // An online recipient sees it at once (as staff mail).
        if (World?.FindOnline(sent.Recipient ?? Guid.Empty) is { } online) online.Send(WritePost(PacketWriter.Opcode(PostRecvNt), stored).ToArray());
        return (message, await InventoryChangesAsync(ct, 0, 0));
    }

    /// <summary>The bag as //senditem counts it: slot (from 1), item id, count; at most 30 lines.</summary>
    string BagListing()
    {
        var bag = inventory.Values.Where(i => i.Container == InventoryItem.Bag).OrderBy(i => i.Slot).ToList();
        if (bag.Count == 0) return "Your bag is empty.";
        var lines = bag.Take(30).Select(i => $"{i.Slot + 1}: {i.ItemId} x{i.Count}");
        return "Bag (slot: item x count): " + string.Join(", ", lines) + (bag.Count > 30 ? $" ... ({bag.Count - 30} more; or use the item id)" : "");
    }
}

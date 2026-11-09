using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// What the market ("//ah", docs/MARKET.md) reads from a session on its game loop, and the mail notice it sends.
public sealed partial class GameSession
{
    /// <summary>The bag's stored items (the market lists from these). Call on the game loop.</summary>
    public IReadOnlyList<ItemState> BagItems() => storedItems.Values.Where(i => i.Location == "bag").ToList();

    /// <summary>Kinah in the bag. Call on the game loop.</summary>
    public long BagKinah() => storedItems.Values.Where(i => i.Location == "bag" && i.TemplateId == KinaItemId).Sum(i => i.Quantity);

    /// <summary>RecvPost_NT for a mail stored elsewhere (a market payout), so an online character sees it at once.</summary>
    public static byte[] PostReceived(PostRecord post) => WritePost(PacketWriter.Opcode(PostRecvNt), post).ToArray();
}

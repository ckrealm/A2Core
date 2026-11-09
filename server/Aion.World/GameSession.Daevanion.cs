using System.Text.Json.Serialization;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

// Daevanion boards (docs/DAEVANION.md). Each class has its boards (DaevanionBoard.dat, opened by the character level) of nodes
// on a grid (DaevanionNode.dat): the Start node (and every bAutoLearn node) opens with the board; any other node opens next to
// an opened one for its CostDaevanionPoint points of the board's EDaevanionPointType, gives its stats (Stat), skill levels
// (SkillLevel) or passive (Passive), and closes again for its ResetGold Kina as long as the opened nodes stay joined to the start.
// A board reset closes them all for the sum. Points: Exp.dat DaevanionPointMap by level (the client knows these from its own
// table) plus the bonus (point items and Aion:Daevanion), up to DaevanionMaxCapacity.
//
// Opcodes: the 0xe2 run (catalogue index + 0xdd07; DaevanionPointUpdate_NT 0xe22e and DaevanionBoardInfo_NT 0xe226 OBSERVED
// at the official world entry, `05 01 0000 02 0000 03 0000 04 0000 05 0000` and `00`): BoardInfo_NT 0xe226 {[{board i32,
// [node i32]}]}, OpenNode_RQ/RS/NT 0xe227/0xe228/0xe229, CloseNode_RQ/RS 0xe22a/0xe22b, ResetBoard_RQ/RS 0xe22c/0xe22d,
// PointUpdate_NT 0xe22e {[{type u8, used u16}]}, UpdateItemDaevanionPoint_NT 0xe22f (bonus, same layout), OpenAllNode_RQ/RS
// 0xe2ee/0xe2ef {board} -> {result, board, [node]}, OpenRouteNode_RQ/RS 0xe2f0/0xe2f1 {board, [node]} -> {result, board, [node]}.
// Every reply's result is u16 as in every observed RS; failures are the client's EResult kDaevanion_* codes. The point width
// (u16) follows the observed entry; the rest is the usmap's (CANDIDATE: no capture of a board in use).
public sealed partial class GameSession
{
    public const ushort DaevanionBoardInfoNt = 0xe226, DaevanionOpenNodeRq = 0xe227, DaevanionOpenNodeRs = 0xe228, DaevanionOpenNodeNt = 0xe229,
        DaevanionCloseNodeRq = 0xe22a, DaevanionCloseNodeRs = 0xe22b, DaevanionResetBoardRq = 0xe22c, DaevanionResetBoardRs = 0xe22d,
        DaevanionPointUpdateNt = 0xe22e, DaevanionItemPointNt = 0xe22f, DaevanionOpenAllRq = 0xe2ee, DaevanionOpenAllRs = 0xe2ef,
        DaevanionOpenRouteRq = 0xe2f0, DaevanionOpenRouteRs = 0xe2f1;

    /// <summary>EResult kDaevanion_* (usmap).</summary>
    public const ushort DaevanionNodeInternalError = 15575, DaevanionNodeInvalidData = 15576, DaevanionNodeNotActiveBoard = 15577,
        DaevanionNodeAlreadyOpen = 15578, DaevanionNodeNotOpen = 15579, DaevanionNodeNeighbourNotOpen = 15580, DaevanionNodeNeighbourOpened = 15581,
        DaevanionNodeNotEnoughPoint = 15582, DaevanionNodeNotEnoughLevel = 15583, DaevanionNodeNotFound = 15584,
        DaevanionBoardInternalError = 15585, DaevanionBoardInvalidData = 15586, DaevanionBoardNotActive = 15587,
        DaevanionBoardNotEnoughLevel = 15588, DaevanionBoardInvalidClass = 15589, DaevanionBoardResetNoMoney = 15590, DaevanionItemOverMaximum = 15591;

    const byte DecreaseDaevanionNodeClose = 71, DecreaseDaevanionBoardReset = 72;   // EDecreaseReason (usmap)
    const string DaevanionSetting = "daevanion";

    sealed record DaevanionState([property: JsonPropertyName("nodes")] int[] Nodes, [property: JsonPropertyName("items")] Dictionary<byte, int> Items);

    readonly HashSet<int> daevanionNodes = [];
    /// <summary>Points from point items, by EDaevanionPointType.</summary>
    readonly Dictionary<byte, int> daevanionItemPoints = [];

    DaevanionOptions Daevanion => options.Daevanion;
    DaevanionTables DaevanionData => options.Daevanion.Tables ?? DaevanionTables.Embedded;
    public IReadOnlySet<int> DaevanionNodes => daevanionNodes;

    void LoadDaevanion(DaevanionState? state)
    {
        daevanionNodes.Clear();
        daevanionItemPoints.Clear();
        if (state is null) return;
        daevanionNodes.UnionWith(state.Nodes.Where(id => DaevanionData.Node(id) is not null));
        foreach (var (type, points) in state.Items) daevanionItemPoints[type] = Math.Max(0, points);
    }

    Task SaveDaevanionAsync(CancellationToken ct) =>
        SaveSettingAsync(DaevanionSetting, new DaevanionState([.. daevanionNodes.Order()], new Dictionary<byte, int>(daevanionItemPoints)), ct);

    // ---------- Points ----------

    int DaevanionLevelPoints(byte type) => DaevanionData.LevelPoints(type, level, Daevanion.LevelPoints);

    /// <summary>The bonus the client adds to its own level points: the item points and Aion:Daevanion (rate and flat extra).</summary>
    public int DaevanionBonus(byte type)
    {
        var rate = Daevanion.PointRate;
        var items = daevanionItemPoints.GetValueOrDefault(type);
        var bonus = (int)Math.Round(items * rate) + (int)Math.Round(DaevanionLevelPoints(type) * Math.Max(0, rate - 1)) + Daevanion.ExtraPoints;
        return Math.Max(0, bonus);
    }

    /// <summary>All the points of a type (level + bonus), capped by DaevanionMaxCapacity when it lists the type.</summary>
    public int DaevanionPoints(byte type)
    {
        var total = DaevanionLevelPoints(type) + DaevanionBonus(type);
        return DaevanionData.Capacity(type) is > 0 and var cap ? Math.Min(total, cap) : total;
    }

    public int DaevanionUsed(byte type) => daevanionNodes.Select(DaevanionData.Node).OfType<DaevanionNode>()
        .Where(n => !n.Fixed && DaevanionData.Board(n.Board)?.PointType == type).Sum(n => n.Cost);

    public int DaevanionLeft(byte type) => DaevanionPoints(type) - DaevanionUsed(type);

    static byte[] PointList(ushort opcode, Func<byte, int> value)
    {
        var w = PacketWriter.Opcode(opcode).Varint(DaevanionTables.PointTypes);
        for (byte t = 1; t <= DaevanionTables.PointTypes; t++) w.U8(t).U16((ushort)Math.Clamp(value(t), 0, ushort.MaxValue));
        return w.ToArray();
    }

    byte[] DaevanionPointUpdate() => PointList(DaevanionPointUpdateNt, DaevanionUsed);
    byte[] DaevanionItemPoints() => PointList(DaevanionItemPointNt, DaevanionBonus);

    byte[] DaevanionBoardInfo()
    {
        var byBoard = daevanionNodes.Select(DaevanionData.Node).OfType<DaevanionNode>().GroupBy(n => n.Board).OrderBy(g => g.Key).ToList();
        var w = PacketWriter.Opcode(DaevanionBoardInfoNt).Varint(byBoard.Count);
        foreach (var g in byBoard)
        {
            w.I32(g.Key).Varint(g.Count());
            foreach (var n in g.OrderBy(n => n.Id)) w.I32(n.Id);
        }
        return w.ToArray();
    }

    /// <summary>The world entry's Daevanion messages: the boards, the used points and (when any) the bonus points.</summary>
    List<byte[]> DaevanionAtEntry()
    {
        OpenFixedNodes();
        var replies = new List<byte[]> { DaevanionPointUpdate(), DaevanionBoardInfo() };
        if (Enumerable.Range(1, DaevanionTables.PointTypes).Any(t => DaevanionBonus((byte)t) > 0)) replies.Add(DaevanionItemPoints());
        return replies;
    }

    // ---------- Boards ----------

    /// <summary>0 when the character can use the board, else the EResult (class, level, Aion:Daevanion).</summary>
    ushort BoardRefusal(DaevanionBoard board)
    {
        if (!Daevanion.Enabled || Daevanion.DisabledBoards.Contains(board.Id) || Daevanion.MaxBoardLevel > 0 && board.NeedLevel > Daevanion.MaxBoardLevel)
            return DaevanionBoardNotActive;
        if (!string.Equals(board.Class, className, StringComparison.OrdinalIgnoreCase)) return DaevanionBoardInvalidClass;
        return level < board.NeedLevel ? DaevanionBoardNotEnoughLevel : (ushort)0;
    }

    public IEnumerable<DaevanionBoard> ActiveBoards() => DaevanionData.Boards.Where(b => BoardRefusal(b) == 0).OrderBy(b => b.Order).ThenBy(b => b.Id);

    /// <summary>Opens the Start and bAutoLearn nodes of every board the character can use; the ids newly opened.</summary>
    List<int> OpenFixedNodes()
    {
        var opened = new List<int>();
        foreach (var board in ActiveBoards())
            foreach (var n in DaevanionData.NodesOf(board.Id))
                if (n.Fixed && n.NeedLevel <= level && daevanionNodes.Add(n.Id)) opened.Add(n.Id);
        return opened;
    }

    /// <summary>After a level-up: the boards and fixed nodes it opens.</summary>
    async Task<List<byte[]>> DaevanionLevelUpAsync(CancellationToken ct)
    {
        var opened = OpenFixedNodes();
        if (opened.Count > 0) await SaveDaevanionAsync(ct);
        var replies = new List<byte[]>();
        if (opened.Count > 0) replies.Add(OpenNodeNt(opened));
        replies.Add(DaevanionPointUpdate());
        if (Daevanion.PointRate != 1) replies.Add(DaevanionItemPoints());   // the level part of the bonus grew with the level
        return replies;
    }

    static byte[] OpenNodeNt(IReadOnlyCollection<int> nodes)
    {
        var w = PacketWriter.Opcode(DaevanionOpenNodeNt).Varint(nodes.Count);
        foreach (var id in nodes) w.I32(id);
        return w.ToArray();
    }

    /// <summary>0 when the node can be opened now, else the EResult.</summary>
    ushort OpenRefusal(DaevanionNode node, Dictionary<byte, int>? spent = null)
    {
        if (DaevanionData.Board(node.Board) is not { } board) return DaevanionNodeInvalidData;
        if (BoardRefusal(board) != 0) return DaevanionNodeNotActiveBoard;
        if (daevanionNodes.Contains(node.Id)) return DaevanionNodeAlreadyOpen;
        if (level < node.NeedLevel) return DaevanionNodeNotEnoughLevel;
        var boardOpen = DaevanionData.NodesOf(board.Id).Any(n => daevanionNodes.Contains(n.Id));
        var hasStart = DaevanionData.NodesOf(board.Id).Any(n => n.Fixed);
        // Next to an opened node; a board without a Start node (not in the client data seen) starts anywhere.
        if (!node.Fixed && !DaevanionData.Neighbours(node).Any(n => daevanionNodes.Contains(n.Id)) && (hasStart || boardOpen))
            return DaevanionNodeNeighbourNotOpen;
        var cost = node.Fixed ? 0 : node.Cost;
        if (cost > DaevanionLeft(board.PointType) - (spent?.GetValueOrDefault(board.PointType) ?? 0)) return DaevanionNodeNotEnoughPoint;
        return 0;
    }

    /// <summary>The stat, skill or passive changes of opened or closed nodes: the stat sheet and passives again.</summary>
    List<byte[]> DaevanionEffects(IEnumerable<int> changed)
    {
        var nodes = changed.Select(DaevanionData.Node).OfType<DaevanionNode>().ToList();
        var replies = new List<byte[]>();
        if (nodes.Any(n => n.Type == DaevanionNodeType.Passive)) replies.AddRange(PassiveAbnormals());
        if (nodes.Any(n => n.Stats.Count > 0)) replies.AddRange(RefreshStats());
        return replies;
    }

    /// <summary>0xe227 DaevanionOpenNode_RQ {node i32} -> 0xe228 {result, node}, 0xe229 the opened node, the used points.</summary>
    async Task<List<byte[]>> DaevanionOpenNodeAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var id = BitConverter.ToInt32(plain, 2);
        byte[] Reply(ushort result) => PacketWriter.Opcode(DaevanionOpenNodeRs).U16(result).I32(id).ToArray();
        if (DaevanionData.Node(id) is not { } node) return [Reply(DaevanionNodeNotFound)];
        if (OpenRefusal(node) is var refused and not 0) return [Reply(refused)];
        daevanionNodes.Add(id);
        await SaveDaevanionAsync(ct);
        return [Reply(0), OpenNodeNt([id]), DaevanionPointUpdate(), .. DaevanionEffects([id]), .. AchieveDaevanion([node])];
    }

    /// <summary>The nodes of a board still joined to its fixed nodes when <paramref name="without"/> are closed.</summary>
    HashSet<int> Joined(int boardId, IReadOnlySet<int> without)
    {
        var open = DaevanionData.NodesOf(boardId).Where(n => daevanionNodes.Contains(n.Id) && !without.Contains(n.Id)).ToDictionary(n => n.Id);
        var seen = new HashSet<int>();
        var queue = new Queue<DaevanionNode>(open.Values.Where(n => n.Fixed));
        if (queue.Count == 0) return [.. open.Keys];   // no start node: nothing to stay joined to
        while (queue.TryDequeue(out var n))
        {
            if (!seen.Add(n.Id)) continue;
            foreach (var next in DaevanionData.Neighbours(n))
                if (open.ContainsKey(next.Id) && !seen.Contains(next.Id)) queue.Enqueue(next);
        }
        return seen;
    }

    /// <summary>Kina for closing nodes (none with Aion:Daevanion:FreeReset): false when the bag is short.</summary>
    async Task<(bool Paid, List<byte[]> Changes)> PayDaevanionAsync(long kina, byte reason, CancellationToken ct)
    {
        if (Daevanion.FreeReset || kina <= 0) return (true, []);
        if (Store is not { } store || CharacterId is not { } character) return (true, []);   // development logins keep no Kina
        if (CostStacks(KinaItemId, kina) is not { } stacks) return (false, []);
        foreach (var (item, revision, count) in stacks)
        {
            var ok = await Stored(async () => await store.ConsumeItemAsync(character, item, revision, count, ct) ? [[]] : []);
            if (ok.Count == 0) return (false, await InventoryChangesAsync(ct, 0, reason));
        }
        return (true, await InventoryChangesAsync(ct, 0, reason));
    }

    /// <summary>0xe22a DaevanionCloseNode_RQ {node i32} -> 0xe22b {result, node}: the node closes for its ResetGold Kina, its points back.</summary>
    async Task<List<byte[]>> DaevanionCloseNodeAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var id = BitConverter.ToInt32(plain, 2);
        byte[] Reply(ushort result) => PacketWriter.Opcode(DaevanionCloseNodeRs).U16(result).I32(id).ToArray();
        if (DaevanionData.Node(id) is not { } node) return [Reply(DaevanionNodeNotFound)];
        if (!daevanionNodes.Contains(id) || node.Fixed) return [Reply(DaevanionNodeNotOpen)];
        var remaining = DaevanionData.NodesOf(node.Board).Count(n => daevanionNodes.Contains(n.Id)) - 1;
        if (Joined(node.Board, new HashSet<int> { id }).Count != remaining) return [Reply(DaevanionNodeNeighbourOpened)];
        var (paid, changes) = await PayDaevanionAsync(node.ResetGold, DecreaseDaevanionNodeClose, ct);
        if (!paid) return [Reply(DaevanionBoardResetNoMoney), .. changes];
        daevanionNodes.Remove(id);
        await SaveDaevanionAsync(ct);
        return [Reply(0), .. changes, DaevanionPointUpdate(), DaevanionBoardInfo(), .. DaevanionEffects([id])];
    }

    /// <summary>0xe22c DaevanionResetBoard_RQ {board i32} -> 0xe22d {result, board}: every node but the fixed ones closes for their Kina.</summary>
    async Task<List<byte[]>> DaevanionResetBoardAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var boardId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(ushort result) => PacketWriter.Opcode(DaevanionResetBoardRs).U16(result).I32(boardId).ToArray();
        if (DaevanionData.Board(boardId) is not { } board) return [Reply(DaevanionBoardInvalidData)];
        if (BoardRefusal(board) is var refused and not 0) return [Reply(refused)];
        var closing = DaevanionData.NodesOf(boardId).Where(n => !n.Fixed && daevanionNodes.Contains(n.Id)).ToList();
        if (closing.Count == 0) return [Reply(0)];
        var (paid, changes) = await PayDaevanionAsync(closing.Sum(n => (long)n.ResetGold), DecreaseDaevanionBoardReset, ct);
        if (!paid) return [Reply(DaevanionBoardResetNoMoney), .. changes];
        daevanionNodes.ExceptWith(closing.Select(n => n.Id));
        await SaveDaevanionAsync(ct);
        return [Reply(0), .. changes, DaevanionPointUpdate(), DaevanionBoardInfo(), .. DaevanionEffects(closing.Select(n => n.Id))];
    }

    /// <summary>Opens nodes one at a time while they can be opened; the ids opened.</summary>
    List<int> OpenWhilePossible(IEnumerable<DaevanionNode> order, bool stopAtFirstRefusal, out ushort lastRefusal)
    {
        lastRefusal = 0;
        var opened = new List<int>();
        foreach (var n in order)
        {
            if (OpenRefusal(n) is var refused and not 0)
            {
                lastRefusal = refused;
                if (stopAtFirstRefusal) break;
                continue;
            }
            daevanionNodes.Add(n.Id);
            opened.Add(n.Id);
        }
        return opened;
    }

    /// <summary>
    /// 0xe2ee DaevanionOpenAllNode_RQ {board i32} -> 0xe2ef {result, board, [node]}: every node the points reach, cheapest first
    /// from the opened ones outwards (OURS order).
    /// </summary>
    async Task<List<byte[]>> DaevanionOpenAllAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 6) return [];
        var boardId = BitConverter.ToInt32(plain, 2);
        byte[] Reply(ushort result, IReadOnlyCollection<int> nodes)
        {
            var w = PacketWriter.Opcode(DaevanionOpenAllRs).U16(result).I32(boardId).Varint(nodes.Count);
            foreach (var id in nodes) w.I32(id);
            return w.ToArray();
        }
        if (DaevanionData.Board(boardId) is not { } board) return [Reply(DaevanionBoardInvalidData, [])];
        if (BoardRefusal(board) is var refused and not 0) return [Reply(refused, [])];
        var opened = new List<int>();
        while (true)
        {
            var next = DaevanionData.NodesOf(boardId).Where(n => OpenRefusal(n) == 0)
                .OrderBy(n => n.Cost).ThenBy(n => n.Row).ThenBy(n => n.Col).FirstOrDefault();
            if (next is null) break;
            daevanionNodes.Add(next.Id);
            opened.Add(next.Id);
        }
        if (opened.Count == 0) return [Reply(DaevanionNodeNotEnoughPoint, [])];
        await SaveDaevanionAsync(ct);
        return [Reply(0, opened), DaevanionPointUpdate(), .. DaevanionEffects(opened),
            .. AchieveDaevanion(opened.Select(DaevanionData.Node).OfType<DaevanionNode>().ToList())];
    }

    /// <summary>0xe2f0 DaevanionOpenRouteNode_RQ {board i32, [node i32]} -> 0xe2f1 {result, board, [node opened]}: the route in order.</summary>
    async Task<List<byte[]>> DaevanionOpenRouteAsync(byte[] plain, CancellationToken ct)
    {
        int boardId;
        var route = new List<int>();
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            boardId = reader.I32();
            var count = reader.Varint();
            for (var i = 0; i < count && i < 512; i++) route.Add(reader.I32());
        }
        catch (ProtocolException) { return []; }
        byte[] Reply(ushort result, IReadOnlyCollection<int> nodes)
        {
            var w = PacketWriter.Opcode(DaevanionOpenRouteRs).U16(result).I32(boardId).Varint(nodes.Count);
            foreach (var id in nodes) w.I32(id);
            return w.ToArray();
        }
        if (DaevanionData.Board(boardId) is not { } board) return [Reply(DaevanionBoardInvalidData, [])];
        if (BoardRefusal(board) is var refused and not 0) return [Reply(refused, [])];
        var nodes = route.Select(DaevanionData.Node).ToList();
        if (nodes.Any(n => n is null || n.Board != boardId)) return [Reply(DaevanionNodeNotFound, [])];
        var opened = OpenWhilePossible(nodes!, stopAtFirstRefusal: true, out var failed);
        if (opened.Count == 0) return [Reply(failed, [])];
        await SaveDaevanionAsync(ct);
        return [Reply(opened.Count == route.Count ? (ushort)0 : failed, opened), DaevanionPointUpdate(), .. DaevanionEffects(opened),
            .. AchieveDaevanion(opened.Select(DaevanionData.Node).OfType<DaevanionNode>().ToList())];
    }

    /// <summary>The achievement objectives of opened nodes (EAchievementObjectiveType DaevanionNodeActivate, ...Grade, ...Type).</summary>
    List<byte[]> AchieveDaevanion(IReadOnlyList<DaevanionNode> opened)
    {
        if (opened.Count == 0) return [];
        var replies = new List<byte[]>();
        replies.AddRange(Achieve("DaevanionNodeActivate", opened.Count));
        foreach (var n in opened)
        {
            replies.AddRange(Achieve("DaevanionNodeActivateGrade", 1, e => e.Values.Length == 0 || e.Has(n.Grade)));
            replies.AddRange(Achieve("DaevanionNodeActivateType", 1, e => e.Values.Length == 0 || e.Has(n.Type.ToString())));
        }
        return replies;
    }

    // ---------- What the nodes give ----------

    IEnumerable<DaevanionNode> OpenedNodes() =>
        daevanionNodes.Select(DaevanionData.Node).OfType<DaevanionNode>().Where(n => DaevanionData.Board(n.Board) is { } b && BoardRefusal(b) == 0);

    /// <summary>A stat from the opened nodes (EStat name).</summary>
    public int DaevanionStat(string stat) =>
        OpenedNodes().Sum(n => n.Stats.Where(s => string.Equals(s.Stat, stat, StringComparison.OrdinalIgnoreCase)).Sum(s => s.Value));

    /// <summary>Every stat of the opened nodes by its wire id (EStat), names the tables do not know left out.</summary>
    Dictionary<int, int> DaevanionStatsById()
    {
        var byId = new Dictionary<int, int>();
        foreach (var (stat, value) in OpenedNodes().SelectMany(n => n.Stats))
            if (DaevanionData.StatId(stat) is { } id) byId[id] = byId.GetValueOrDefault(id) + value;
        return byId;
    }

    /// <summary>The skill levels the opened SkillLevel nodes add to a skill (used in combat; the bought level is unchanged).</summary>
    public int DaevanionSkillBonus(int skill) => OpenedNodes().Sum(n => n.SkillLevels.Where(s => s.Skill == skill).Sum(s => s.Levels));

    /// <summary>The skill level combat uses: the learned or bought level plus the Daevanion levels.</summary>
    int CombatSkillLevel(int skill) => SkillLevel(skill) + DaevanionSkillBonus(skill);

    /// <summary>The passive abnormals of the opened Passive nodes (a skill's Abnormal_Passive rows, else the value as an abnormal id).</summary>
    IEnumerable<int> DaevanionPassiveAbnormals() =>
        OpenedNodes().SelectMany(n => n.Passives).SelectMany(v => Boxes.Passives(v) is { Count: > 0 } abnormals ? abnormals : [v]).Distinct();

    // ---------- Point items ----------

    /// <summary>
    /// GetDaevanionPoint [type or name, amount] adds the amount; Fix_DaevanionPoint [type or name, amount] raises the points to
    /// the amount (refused when already there). The type defaults to DaevanionCrystal (CANDIDATE value order).
    /// </summary>
    async Task<List<byte[]>?> UseDaevanionPointItemAsync(INativeCharacterStore store, Guid character, ItemState row, ItemUse use, bool reply, CancellationToken ct)
    {
        byte type = 1;
        var amount = 0;
        foreach (var v in use.Values)
        {
            var name = v.Split("::")[^1];
            if (Enum.TryParse<DaevanionPointName>(name, out var named) && !int.TryParse(name, out _)) type = (byte)named;
            else if (int.TryParse(v, out var n))
            {
                if (amount == 0 && n is >= 1 and <= DaevanionTables.PointTypes && use.Values.Count > 1 && v == use.Values[0]) type = (byte)n;
                else amount = n;
            }
        }
        if (amount <= 0) return null;
        var fix = use.Effect == "Fix_DaevanionPoint";
        var current = DaevanionLevelPoints(type) + daevanionItemPoints.GetValueOrDefault(type);
        var add = fix ? amount - current : amount;
        if (add <= 0) return null;   // kItem_Use_Fail_Over_Already_FixDaevanionPoint
        if (DaevanionData.Capacity(type) is > 0 and var cap && DaevanionPoints(type) >= cap) return null;   // kDaevanion_Item_Use_Fail_Over_Maximum_Point
        var done = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
        if (done.Count == 0) return null;
        daevanionItemPoints[type] = daevanionItemPoints.GetValueOrDefault(type) + add;
        await SaveDaevanionAsync(ct);
        return [.. Used(row, reply), .. await InventoryChangesAsync(ct, IncreaseItemUse, DecreaseItemUse), DaevanionItemPoints()];
    }

    /// <summary>Daevanion points granted by staff or a reward (not an item): stored with the item points.</summary>
    public async Task<List<byte[]>> GrantDaevanionPointsAsync(byte type, int amount, CancellationToken ct)
    {
        if (type is < 1 or > DaevanionTables.PointTypes || amount == 0) return [];
        daevanionItemPoints[type] = Math.Max(0, daevanionItemPoints.GetValueOrDefault(type) + amount);
        await SaveDaevanionAsync(ct);
        return [DaevanionItemPoints(), DaevanionPointUpdate()];
    }

    enum DaevanionPointName : byte { DaevanionCrystal = 1, ConquestCrystal, BattleCrystal, Season02Crystal01, Season03Crystal01 }
}

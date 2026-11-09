using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aion.World;

/// <summary>
/// Aion:Daevanion (OURS: a server owner's choice; docs/DAEVANION.md). The boards follow the client's own tables; these switches
/// only scale the points and the costs. PointRate multiplies the level and item points (1 = official), ExtraPoints adds a flat
/// amount of every point type, FreeReset waives the Kinah of closing nodes and resetting boards, MaxBoardLevel hides the boards
/// whose NeedLevel is above it (0: none hidden), DisabledBoards hides boards by id. LevelPoints says how Exp.dat's
/// DaevanionPointMap reads: "auto" (cumulative when the values never fall from level to level, else per level), "cumulative"
/// or "perlevel".
/// </summary>
public sealed record DaevanionOptions
{
    public bool Enabled { get; init; } = true;
    public double PointRate { get; init; } = 1;
    public int ExtraPoints { get; init; }
    public bool FreeReset { get; init; }
    public int MaxBoardLevel { get; init; }
    public int[] DisabledBoards { get; init; } = [];
    public string LevelPoints { get; init; } = "auto";
    /// <summary>The board tables; null: the embedded tools/data/daevanion.json.</summary>
    public DaevanionTables? Tables { get; init; }

    public static DaevanionOptions Default { get; } = new();

    public DaevanionOptions Validated()
    {
        if (!double.IsFinite(PointRate) || PointRate is < 0 or > 100) throw Bad($"PointRate must be between 0 and 100 (got {PointRate.ToString(CultureInfo.InvariantCulture)})");
        if (ExtraPoints is < 0 or > 10_000) throw Bad($"ExtraPoints must be between 0 and 10000 (got {ExtraPoints})");
        if (MaxBoardLevel is < 0 or > 1000) throw Bad($"MaxBoardLevel must be between 0 and 1000 (got {MaxBoardLevel})");
        if ((LevelPoints ?? "").Trim().ToLowerInvariant() is not ("auto" or "cumulative" or "perlevel"))
            throw Bad($"LevelPoints must be auto, cumulative or perlevel (got \"{LevelPoints}\")");
        return this with { LevelPoints = LevelPoints!.Trim().ToLowerInvariant(), DisabledBoards = DisabledBoards ?? [] };
    }

    static InvalidOperationException Bad(string what) => new($"Aion:Daevanion:{what}.");
}

/// <summary>EDaevanionNodeType.</summary>
public enum DaevanionNodeType : byte { None, Start, Stat, Passive, SkillLevel }

/// <summary>A board (DaevanionBoard.dat): the class it belongs to, the level that opens it, its point type (EDaevanionPointType).</summary>
public sealed record DaevanionBoard(int Id, string Class, int NeedLevel, int Order, byte PointType, string ResetCurrency, string Title);

/// <summary>
/// A node (DaevanionNode.dat) on its board's grid. Stats, skill levels and passives are read from Value01..Value10.
/// </summary>
public sealed record DaevanionNode(int Id, int Board, int Row, int Col, string Grade, DaevanionNodeType Type, bool AutoLearn, int NeedLevel,
    int ConditionType, string ConditionValue, int Cost, int ResetGold, IReadOnlyList<string> Values, string Title)
{
    public IReadOnlyList<(string Stat, int Value)> Stats { get; init; } = [];
    public IReadOnlyList<(int Skill, int Levels)> SkillLevels { get; init; } = [];
    public IReadOnlyList<int> Passives { get; init; } = [];
    /// <summary>Opened with its board, free and never closed: the Start nodes and the bAutoLearn ones.</summary>
    public bool Fixed => Type == DaevanionNodeType.Start || AutoLearn;
}

/// <summary>The boards from the client's tables (tools/data/daevanion.json, tools/daevanion_data.py).</summary>
public sealed class DaevanionTables
{
    public const int PointTypes = 5;   // EDaevanionPointType 1..5 (DaevanionCrystal .. Season03Crystal01)

    readonly Dictionary<int, DaevanionBoard> boards = [];
    readonly Dictionary<int, DaevanionNode> nodes = [];
    readonly Dictionary<int, List<DaevanionNode>> byBoard = [];
    readonly Dictionary<(int Board, int Row, int Col), DaevanionNode> grid = [];
    readonly Dictionary<byte, int> capacity = [];
    readonly SortedDictionary<int, Dictionary<byte, int>> levelPoints = [];
    readonly Dictionary<string, int> statIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> currencyItems = new(StringComparer.OrdinalIgnoreCase);
    bool? cumulative;

    public IReadOnlyCollection<DaevanionBoard> Boards => boards.Values;
    public DaevanionBoard? Board(int id) => boards.GetValueOrDefault(id);
    public DaevanionNode? Node(int id) => nodes.GetValueOrDefault(id);
    public IReadOnlyList<DaevanionNode> NodesOf(int board) => byBoard.GetValueOrDefault(board) ?? [];
    public int Capacity(byte type) => capacity.GetValueOrDefault(type);
    public int? StatId(string stat) => statIds.TryGetValue(stat, out var id) ? id : null;
    public int? CurrencyItem(string category) => currencyItems.TryGetValue(category, out var id) ? id : null;
    public bool Empty => boards.Count == 0;

    /// <summary>The nodes next to a node on its board (row or column one apart).</summary>
    public IEnumerable<DaevanionNode> Neighbours(DaevanionNode n)
    {
        foreach (var (dr, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            if (grid.TryGetValue((n.Board, n.Row + dr, n.Col + dc), out var next)) yield return next;
    }

    /// <summary>The points of a type a character has from its level (Exp.dat DaevanionPointMap), read as <paramref name="mode"/> says.</summary>
    public int LevelPoints(byte type, int level, string mode = "auto")
    {
        var asCumulative = mode switch { "cumulative" => true, "perlevel" => false, _ => cumulative ??= IsCumulative() };
        var total = 0;
        foreach (var (lvl, points) in levelPoints)
        {
            if (lvl > level) break;
            var v = points.GetValueOrDefault(type);
            total = asCumulative ? Math.Max(total, v) : total + v;
        }
        return total;
    }

    /// <summary>Cumulative when every point type's value never falls from one listed level to the next and some value rises past 1.</summary>
    bool IsCumulative()
    {
        for (byte t = 1; t <= PointTypes; t++)
        {
            var values = levelPoints.Values.Select(p => p.GetValueOrDefault(t)).ToList();
            for (var i = 1; i < values.Count; i++)
                if (values[i] < values[i - 1]) return false;
        }
        return levelPoints.Values.SelectMany(p => p.Values).DefaultIfEmpty(0).Max() > 1;
    }

    public DaevanionTables(JsonElement root)
    {
        if (root.TryGetProperty("stats", out var stats))
            foreach (var s in stats.EnumerateObject()) statIds[s.Name] = s.Value.GetInt32();
        if (root.TryGetProperty("currency_items", out var currencies))
            foreach (var c in currencies.EnumerateObject()) currencyItems[c.Name] = c.Value.GetInt32();
        foreach (var b in root.GetProperty("boards").EnumerateObject())
        {
            var v = b.Value;
            var id = int.Parse(b.Name, CultureInfo.InvariantCulture);
            boards[id] = new DaevanionBoard(id, Str(v, "class"), Int(v, "level"), Int(v, "order"), (byte)Math.Clamp(Int(v, "point", 1), 1, PointTypes),
                Str(v, "reset"), Str(v, "title"));
        }
        foreach (var n in root.GetProperty("nodes").EnumerateObject())
        {
            var v = n.Value;
            var id = int.Parse(n.Name, CultureInfo.InvariantCulture);
            var values = v.TryGetProperty("v", out var vs) ? vs.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            var type = (DaevanionNodeType)Math.Clamp(Int(v, "t"), 0, 4);
            var node = new DaevanionNode(id, Int(v, "b"), Int(v, "r"), Int(v, "c"), Str(v, "g"), type,
                v.TryGetProperty("auto", out var auto) && auto.ValueKind == JsonValueKind.True, Int(v, "lv"), Int(v, "ct"), Str(v, "cv"),
                Int(v, "cost"), Int(v, "gold"), values, Str(v, "n"));
            node = type switch
            {
                DaevanionNodeType.Stat => node with { Stats = ParseStats(values) },
                DaevanionNodeType.SkillLevel => node with { SkillLevels = ParseSkillLevels(values) },
                DaevanionNodeType.Passive => node with { Passives = Numbers(values).Where(x => x >= 1000).ToList() },
                _ => node,
            };
            nodes[id] = node;
            if (!boards.ContainsKey(node.Board)) continue;
            (byBoard.TryGetValue(node.Board, out var list) ? list : byBoard[node.Board] = []).Add(node);
            grid[(node.Board, node.Row, node.Col)] = node;
        }
        if (root.TryGetProperty("capacity", out var caps))
            foreach (var c in caps.EnumerateObject()) capacity[byte.Parse(c.Name, CultureInfo.InvariantCulture)] = c.Value.GetInt32();
        if (root.TryGetProperty("level_points", out var levels))
            foreach (var l in levels.EnumerateObject())
                levelPoints[int.Parse(l.Name, CultureInfo.InvariantCulture)] = l.Value.EnumerateObject()
                    .ToDictionary(p => byte.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetInt32());
    }

    static int Int(JsonElement e, string name, int fallback = 0) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
    static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static readonly Regex Token = new(@"[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z0-9_]+)?|-?\d+", RegexOptions.Compiled);

    static IEnumerable<string> Tokens(IEnumerable<string> values) => values.SelectMany(v => Token.Matches(v).Select(m => m.Value.Split("::")[^1]));

    static IEnumerable<int> Numbers(IEnumerable<string> values) =>
        Tokens(values).Select(t => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? (int?)n : null).OfType<int>();

    /// <summary>
    /// Stat pairs from the values in any of the forms the client writes ("HPMax,100", "EStat::HPMax:100", or the name and the value
    /// in separate columns): each stat name takes the next number after it (CANDIDATE: the exact form is unconfirmed).
    /// </summary>
    internal static List<(string Stat, int Value)> ParseStats(IEnumerable<string> values)
    {
        var stats = new List<(string, int)>();
        string? pending = null;
        foreach (var t in Tokens(values))
        {
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (pending is not null) { stats.Add((pending, n)); pending = null; }
            }
            else pending = t;
        }
        return stats;
    }

    /// <summary>Skill level nodes: each skill id (seven or more digits) with the level count after it (1 when none follows).</summary>
    internal static List<(int Skill, int Levels)> ParseSkillLevels(IEnumerable<string> values)
    {
        var result = new List<(int, int)>();
        int? skill = null;
        foreach (var n in Numbers(values))
        {
            if (n >= 1_000_000)
            {
                if (skill is { } previous) result.Add((previous, 1));
                skill = n;
            }
            else if (skill is { } s) { result.Add((s, Math.Max(1, n))); skill = null; }
        }
        if (skill is { } last) result.Add((last, 1));
        return result;
    }

    public static DaevanionTables Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new DaevanionTables(doc.RootElement);
    }

    static readonly Lazy<DaevanionTables> embedded = new(() =>
    {
        using var stream = typeof(DaevanionTables).Assembly.GetManifestResourceStream("daevanion.json") ?? throw new InvalidOperationException("daevanion.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return new DaevanionTables(doc.RootElement);
    });
    public static DaevanionTables Embedded => embedded.Value;
}

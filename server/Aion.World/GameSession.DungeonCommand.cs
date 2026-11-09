using System.Text;

namespace Aion.World;

/// <summary>
/// One dungeon the /dungeon command offers: its short key, global name, and the Dungeon.dat ids by difficulty key (easy,
/// normal, hard; stage numbers for Transcendence and the Ascension Trial tiers). The names are global's; which client
/// dungeon is which global one is matched by entry level and item level (CONTENT_COMPARISON.md).
/// </summary>
public sealed record DungeonChoice(string Key, string Name, string Kind, IReadOnlyList<(string Difficulty, int Id)> Modes);

/// <summary>The dungeon list of /dungeon (OURS: the client's expedition window, DungeonRoom* and PartyDungeonEnter, is unrecorded).</summary>
public static class DungeonChoices
{
    static (string, int)[] Tiers(int trial, int count) =>
        [.. new[] { "easy", "normal", "hard", "extreme", "insane", "hell", "doom" }.Take(count).Select((name, i) => (name, 910000 + trial * 10 + i + 1))];

    static (string, int)[] Edn(int easy, int normal, int hard) => [("easy", easy), ("normal", normal), ("hard", hard)];

    public static readonly IReadOnlyList<DungeonChoice> All =
    [
        // Global Season 1: the six Expeditions (5 players) with their hard modes.
        new("krao", "Krao Cave", "Expedition", Edn(600002, 600001, 600004)),
        new("urugugu", "Urugugu Canyon", "Expedition", Edn(600011, 600012, 600013)),
        new("firetemple", "Fire Temple", "Expedition", Edn(600021, 600022, 600023)),
        new("draupnir", "Draupnir Cave", "Expedition", Edn(600031, 600032, 600033)),
        new("vakron", "Vakron Sky Island", "Expedition", Edn(600071, 600072, 600073)),
        new("horn", "Ferocious Horn Den", "Expedition", Edn(600091, 600092, 600093)),
        // The two Transcendence dungeons (stages: the client's _G_01/03/06/09 rows).
        new("arkanis", "Shattered Arkanis", "Transcendence", [("easy", 600061), ("1", 600063), ("3", 600065), ("6", 600068), ("9", 610062)]),
        new("deus", "Deus Research Base", "Transcendence", [("easy", 600051), ("1", 600053), ("3", 600055), ("6", 600058), ("9", 610052)]),
        // The raid (10 players).
        new("ludra", "Abyssal Forge: Ludra", "Raid", [("normal", 600082)]),
        // The Ascension Trial (solo): six dungeons, tiers Easy to Doom (Doom only in the last two).
        new("trial1", "Ascension Trial 1", "Trial", Tiers(1, 6)),
        new("trial2", "Ascension Trial 2", "Trial", Tiers(2, 6)),
        new("trial3", "Ascension Trial 3", "Trial", Tiers(3, 6)),
        new("trial4", "Ascension Trial 4", "Trial", Tiers(4, 6)),
        new("trial5", "Ascension Trial 5", "Trial", Tiers(5, 7)),
        new("trial6", "Ascension Trial 6", "Trial", Tiers(6, 7)),
        // The daily resource dungeons (which row is Bio-Research Base, Odylium Repository or Crobahki's is not known).
        new("daily1", "Daily dungeon 1", "Daily", [("normal", 900001)]),
        new("daily2", "Daily dungeon 2", "Daily", [("normal", 900002)]),
        new("daily3", "Daily dungeon 3", "Daily", [("normal", 900003)]),
        // Korea-only (Aion:Dungeons:KoreaOnly).
        new("dramata", "Dramata's Nest", "Expedition", Edn(600041, 600042, 610044)),
        new("cradle", "Cradle of Nihility", "Expedition", Edn(600121, 600122, 600123)),
        new("illusion", "Hall of Illusion", "Expedition", Edn(600131, 600132, 600133)),
        new("azure", "Azure Breath Isle", "Expedition", Edn(600141, 600142, 600143)),
        new("citadel", "Fallen Daeva Citadel", "Expedition", Edn(600151, 600152, 600153)),
        new("sunken", "Sunken Temple", "Transcendence", [("1", 610010), ("2", 610011), ("3", 610012), ("4", 610013)]),
        new("mirror", "Mirror Heart", "Transcendence", [("1", 610020), ("2", 610021), ("3", 610022), ("4", 610023)]),
        new("fafnite", "Fafnite Gully", "Transcendence", [("1", 610070), ("2", 610071), ("3", 610072), ("4", 610073)]),
        new("decontamination", "Corroded Decontamination Facility", "Raid", [("normal", 620011)]),
        new("muspel", "Chalice of Muspel", "Raid", [("1", 620021), ("2", 620022)]),
    ];

    public static DungeonChoice? Find(string key) =>
        All.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(c => c.Name.Replace(" ", "").Replace("'", "").StartsWith(key.Replace(" ", "").Replace("'", ""), StringComparison.OrdinalIgnoreCase));
}

public sealed partial class GameSession
{
    public const string DungeonHelp = "/dungeon lists the dungeons; /dungeon <name> [easy|normal|hard|stage] enters one (your party joins you there).";

    /// <summary>
    /// /dungeon (OURS): lists the dungeons this character may pick, or enters one at a difficulty from anywhere in the open
    /// world (the way back is where the player stood). The usual entry rules apply (level, item level, quest steps, Korea-only,
    /// daily limits); a party member already inside the same dungeon takes the player into that copy.
    /// </summary>
    public (string Reply, List<byte[]> Packets) DungeonCommand(string args, long now)
    {
        if (!DungeonRules.Command) return ("/dungeon is off on this server (Aion:Dungeons:Command).", []);
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || words[0].Equals("list", StringComparison.OrdinalIgnoreCase)) return (DungeonList(), []);
        if (words[0].Equals("help", StringComparison.OrdinalIgnoreCase)) return (DungeonHelp, []);
        if (DungeonChoices.Find(words[0]) is not { } choice) return ($"No dungeon called {words[0]}. {DungeonHelp}", []);
        var wanted = words.Length > 1 ? words[1] : choice.Modes[0].Difficulty;
        var mode = choice.Modes.FirstOrDefault(m => m.Difficulty.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (mode.Id == 0) return ($"{choice.Name} has {string.Join(", ", choice.Modes.Select(m => m.Difficulty))}.", []);
        if (MapEventData.Dungeon(mode.Id) is not { } room) return ($"{choice.Name} ({mode.Difficulty}) is not in this server's data.", []);
        if (instanceKey != 0) return ("Leave this dungeon first.", []);
        if (dead || reloading) return ("Not now.", []);
        if (!IsAdmin && !MeetsEntry(room, out var why)) return ($"{choice.Name} ({mode.Difficulty}): {why}.", []);
        var moved = EnterDungeonById(mode.Id, now);
        return moved.Count > 0 ? ($"Entering {choice.Name} ({mode.Difficulty}).", moved)
            : ($"{choice.Name} ({mode.Difficulty}) can't be entered now (daily limit, or its map is not served).", []);
    }

    string DungeonList()
    {
        var text = new StringBuilder("Dungeons (/dungeon <name> <difficulty>): ");
        foreach (var group in DungeonChoices.All.GroupBy(c => c.Kind))
        {
            var open = group.Where(c => c.Modes.Any(m => MapEventData.Dungeon(m.Id) is { } room && (IsAdmin || DungeonRules.IsOpen(room)))).ToList();
            if (open.Count == 0) continue;
            text.Append(group.Key).Append(": ");
            text.Append(string.Join(", ", open.Select(c =>
            {
                var levels = c.Modes.Select(m => MapEventData.Dungeon(m.Id)).OfType<DungeonInfo>().Select(r => DungeonRules.MinLevelOf(r)).Distinct();
                return $"{c.Key} ({string.Join('/', c.Modes.Select(m => m.Difficulty))}, lv {string.Join('/', levels)})";
            })));
            text.Append(". ");
        }
        if (!DungeonRules.KoreaOnly && !IsAdmin) text.Append("Korea-only dungeons are closed.");
        return text.ToString().TrimEnd();
    }
}

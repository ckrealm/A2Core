using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Dungeons (OURS: a server owner's choice, nothing observed). EntryLimits off (the default) changes nothing. On, a
/// character may enter each limited dungeon <see cref="DailyEntries"/> times per day, the day starting at
/// <see cref="ResetHour"/> (server local time); <see cref="Entries"/> overrides the count per dungeon (key: Dungeon.dat id
/// or name; 0: no limit), and makes any dungeon limited, whatever its type. Without an override the limited types are
/// <see cref="LimitedTypes"/> (the repeatable expeditions; story layers, quest and seal dungeons, field events and the
/// Abyss are never limited). Staff are not limited. The client's data carries no entry cost the server can apply
/// (MapEntrance.dat has level, item level, member cap and quest steps; DungeonCoolTimes are all 0), so there is no
/// ticket or energy switch. <see cref="UntrackedObjectiveSeconds"/> is the delay after which a map-event objective the
/// server cannot follow (waves, puzzles, scores, items) counts as done so the event goes on.
/// </summary>
public sealed record DungeonOptions
{
    public bool EntryLimits { get; init; }
    public int DailyEntries { get; init; } = DefaultDailyEntries;
    /// <summary>Per-dungeon entries a day by Dungeon.dat id or name (Aion:Dungeons:Entries:KraoCave_Hard=1); 0: no limit.</summary>
    public Dictionary<string, int> Entries { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int ResetHour { get; init; } = DefaultResetHour;
    public int UntrackedObjectiveSeconds { get; init; } = (int)(MapEventRun.FallbackMs / 1000);
    /// <summary>
    /// Entry levels (OURS): "global" (the default) replaces the client's placeholder min level 99 with global's entry level
    /// (<see cref="GlobalLevel"/>; the daily dungeons <see cref="GlobalDailyLevel"/>), "client" keeps Dungeon.dat as it is.
    /// Item levels and quest steps stay as the client has them either way.
    /// </summary>
    public string LevelGates { get; init; } = "global";
    /// <summary>The entry level that replaces 99 on global content (global's level cap, Season 1).</summary>
    public int GlobalLevel { get; init; } = 45;
    /// <summary>The daily resource dungeons' entry level on global (Daeva Bio-Research Base: 30).</summary>
    public int GlobalDailyLevel { get; init; } = 30;
    /// <summary>The Korea-only dungeons and raids (<see cref="KoreaOnlyNames"/>): closed when false (the default, as on global).</summary>
    public bool KoreaOnly { get; init; }
    /// <summary>Their entry level when <see cref="KoreaOnly"/> is on (their client rows say 99, a placeholder).</summary>
    public int KoreaOnlyLevel { get; init; } = 45;
    /// <summary>The player command //dungeon (pick a dungeon and its difficulty; the client's expedition window is not served).</summary>
    public bool Command { get; init; } = true;
    /// <summary>Revives each player gets in an Expedition hard mode (global: 3); past them the revive puts the player outside. 0: no limit.</summary>
    public int HardModeRevives { get; init; } = 3;

    /// <summary>The time zone the reset hour is read in (the host's; checks use UTC). Not bound from configuration.</summary>
    internal TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>OURS defaults: three entries a day, reset at 09:00 server time.</summary>
    public const int DefaultDailyEntries = 3, DefaultResetHour = 9, MaxEntries = 1000, MaxUntrackedSeconds = 3600;
    /// <summary>OURS: the Dungeon.dat types limited without an override (expeditions, awakening, daily dungeons, raids).</summary>
    public static readonly IReadOnlySet<string> LimitedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Party", "Awaken", "Daily", "Raid" };
    public static DungeonOptions Default { get; } = new();

    public long UntrackedObjectiveMs => UntrackedObjectiveSeconds * 1000L;

    /// <summary>
    /// The 2.0.6.0 client's dungeons that live global Aion 2 has not opened (Korea/Taiwan only, Nov 2026): the expeditions
    /// Dramata's Nest, Cradle of Nihility, Hall of Illusion, Azure Breath Isle and Fallen Daeva Citadel, the Sunken Temple,
    /// Mirror Heart and Fafnite Gully trials, and the raids Corroded Decontamination Facility and Chalice of Muspel. A
    /// Dungeon.dat name starting with one of these is Korea-only. Everything else the client has is on global.
    /// </summary>
    public static readonly IReadOnlyList<string> KoreaOnlyNames =
        ["DeadDramataNest", "CradleNihility", "PhantomsHall", "BlueBreathIsle", "DaevaCitadel", "SunkenTemple", "MirrorHeart", "FafniteGully", "ErosionPurifier", "MusphelHolyGrail"];

    /// <summary>The client's min level that only stands for "not open yet" (every hard mode and Korea-only row has it).</summary>
    public const int PlaceholderLevel = 99;

    /// <summary>An Expedition hard mode (Dungeon.dat type Party, name *_Hard).</summary>
    public static bool IsHardMode(DungeonInfo room) =>
        room.Type.Equals("Party", StringComparison.OrdinalIgnoreCase) && room.Name.EndsWith("_Hard", StringComparison.OrdinalIgnoreCase);

    public static bool IsKoreaOnly(DungeonInfo room) => KoreaOnlyNames.Any(prefix => room.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    bool ClientGates => string.Equals(LevelGates, "client", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the dungeon may be entered at all (Korea-only content is closed unless <see cref="KoreaOnly"/>).</summary>
    public bool IsOpen(DungeonInfo room) => KoreaOnly || !IsKoreaOnly(room);

    /// <summary>The level a character needs (0: none): the client's, with its placeholder 99 replaced unless the gates are "client".</summary>
    public int MinLevelOf(DungeonInfo room)
    {
        if (room.MinLevel != PlaceholderLevel || ClientGates) return room.MinLevel;
        if (IsKoreaOnly(room)) return KoreaOnlyLevel;
        return room.Type.Equals("Daily", StringComparison.OrdinalIgnoreCase) ? GlobalDailyLevel : GlobalLevel;
    }

    /// <summary>Entries a day allowed into the dungeon, or null when it is not limited (limits off, unlimited type, override 0).</summary>
    public int? LimitOf(int dungeonId, DungeonInfo room)
    {
        if (!EntryLimits) return null;
        int? limit = Override(dungeonId, room.Name) ?? (LimitedTypes.Contains(room.Type) ? DailyEntries : null);
        return limit is > 0 ? limit : null;
    }

    int? Override(int dungeonId, string name)
    {
        foreach (var (key, value) in Entries)
            if (key.Trim() == dungeonId.ToString(CultureInfo.InvariantCulture) || string.Equals(key.Trim(), name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    /// <summary>The counting day of a moment (unix ms): its local date, the day turning at the reset hour ("2026-11-08").</summary>
    public string Period(long nowMs) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), Zone).DateTime.AddHours(-ResetHour).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Checks the values; an override must name a dungeon the server knows (a typo stops the host instead of doing nothing).</summary>
    public DungeonOptions Validated(Func<string, bool>? knownDungeon = null)
    {
        if (DailyEntries is < 0 or > MaxEntries) throw new InvalidOperationException($"Aion:Dungeons:DailyEntries must be between 0 and {MaxEntries} (got {DailyEntries}).");
        if (ResetHour is < 0 or > 23) throw new InvalidOperationException($"Aion:Dungeons:ResetHour must be between 0 and 23 (got {ResetHour}).");
        if (UntrackedObjectiveSeconds is < 1 or > MaxUntrackedSeconds)
            throw new InvalidOperationException($"Aion:Dungeons:UntrackedObjectiveSeconds must be between 1 and {MaxUntrackedSeconds} (got {UntrackedObjectiveSeconds}).");
        if (!ClientGates && !string.Equals(LevelGates, "global", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Aion:Dungeons:LevelGates must be global or client (got {LevelGates}).");
        if (HardModeRevives is < 0 or > MaxEntries) throw new InvalidOperationException($"Aion:Dungeons:HardModeRevives must be between 0 and {MaxEntries} (got {HardModeRevives}).");
        foreach (var (name, value) in new[] { ("GlobalLevel", GlobalLevel), ("GlobalDailyLevel", GlobalDailyLevel), ("KoreaOnlyLevel", KoreaOnlyLevel) })
            if (value is < 1 or > PlaceholderLevel) throw new InvalidOperationException($"Aion:Dungeons:{name} must be between 1 and {PlaceholderLevel} (got {value}).");
        foreach (var (key, value) in Entries)
        {
            if (value is < 0 or > MaxEntries) throw new InvalidOperationException($"Aion:Dungeons:Entries:{key} must be between 0 and {MaxEntries} (got {value}).");
            if (knownDungeon is not null && !knownDungeon(key.Trim())) throw new InvalidOperationException($"Aion:Dungeons:Entries:{key} is not a Dungeon.dat id or name.");
        }
        return this;
    }

    /// <summary>Whether a key names a dungeon of the embedded tables (id or name).</summary>
    public static bool KnownDungeon(string key) =>
        int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? MapEventTables.Embedded.Dungeon(id) is not null : MapEventTables.Embedded.DungeonNamed(key) is not null;

    public override string ToString() => (!EntryLimits
        ? $"entry limits off, untracked objectives done after {UntrackedObjectiveSeconds} s"
        : $"entry limits on: {DailyEntries} a day ({string.Join('/', LimitedTypes)}), {Entries.Count} override(s), reset at {ResetHour:00}:00, untracked objectives done after {UntrackedObjectiveSeconds} s")
        + (ClientGates ? ", client entry levels" : $", global entry levels (99 -> {GlobalLevel}, daily {GlobalDailyLevel})")
        + (HardModeRevives > 0 ? $", {HardModeRevives} revives in hard modes" : ", no hard-mode revive limit")
        + (KoreaOnly ? $", Korea-only dungeons open at {KoreaOnlyLevel}" : ", Korea-only dungeons closed");
}

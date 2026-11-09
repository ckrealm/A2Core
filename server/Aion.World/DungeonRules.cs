using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Dungeons (OURS: a server owner's choice, nothing observed). EntryLimits off (the default) changes nothing. On, a
/// character may enter each limited dungeon <see cref="DailyEntries"/> times per day, the day starting at
/// <see cref="ResetHour"/> (default: the shared Aion:Resets hour and zone, <see cref="ResetClock"/>); <see cref="Entries"/> overrides the count per dungeon (key: Dungeon.dat id
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
    /// <summary>An override of the shared reset hour (Aion:Resets:Hour) for entry counts only; null: the shared hour.</summary>
    public int? ResetHour { get; init; }
    public int UntrackedObjectiveSeconds { get; init; } = (int)(MapEventRun.FallbackMs / 1000);

    /// <summary>The time zone the reset hour is read in (null: the shared clock's; checks use UTC). Not bound from configuration.</summary>
    internal TimeZoneInfo? Zone { get; init; }

    /// <summary>OURS default: three entries a day (the day turns with the shared reset clock, Aion:Resets).</summary>
    public const int DefaultDailyEntries = 3, MaxEntries = 1000, MaxUntrackedSeconds = 3600;
    /// <summary>OURS: the Dungeon.dat types limited without an override (expeditions, awakening, daily dungeons, raids).</summary>
    public static readonly IReadOnlySet<string> LimitedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Party", "Awaken", "Daily", "Raid" };
    public static DungeonOptions Default { get; } = new();

    public long UntrackedObjectiveMs => UntrackedObjectiveSeconds * 1000L;

    /// <summary>The clock entry counts follow: the shared one (<see cref="ResetClock.Current"/>) with this section's overrides.</summary>
    public ResetClock Clock => ResetClock.Current with { Hour = ResetHour ?? ResetClock.Current.Hour, Zone = Zone ?? ResetClock.Current.Zone };
    /// <summary>The hour the count turns at (the override, else the shared hour).</summary>
    public int EffectiveResetHour => ResetHour ?? ResetClock.Current.Hour;

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
    public string Period(long nowMs) => Clock.DayKey(nowMs);

    /// <summary>Checks the values; an override must name a dungeon the server knows (a typo stops the host instead of doing nothing).</summary>
    public DungeonOptions Validated(Func<string, bool>? knownDungeon = null)
    {
        if (DailyEntries is < 0 or > MaxEntries) throw new InvalidOperationException($"Aion:Dungeons:DailyEntries must be between 0 and {MaxEntries} (got {DailyEntries}).");
        if (ResetHour is { } hour && hour is < 0 or > 23) throw new InvalidOperationException($"Aion:Dungeons:ResetHour must be between 0 and 23 (got {ResetHour}).");
        if (UntrackedObjectiveSeconds is < 1 or > MaxUntrackedSeconds)
            throw new InvalidOperationException($"Aion:Dungeons:UntrackedObjectiveSeconds must be between 1 and {MaxUntrackedSeconds} (got {UntrackedObjectiveSeconds}).");
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

    public override string ToString() => !EntryLimits
        ? $"entry limits off, untracked objectives done after {UntrackedObjectiveSeconds} s"
        : $"entry limits on: {DailyEntries} a day ({string.Join('/', LimitedTypes)}), {Entries.Count} override(s), reset at {EffectiveResetHour:00}:00, untracked objectives done after {UntrackedObjectiveSeconds} s";
}

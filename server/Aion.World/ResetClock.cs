using System.Globalization;

namespace Aion.World;

/// <summary>
/// Aion:Resets (OURS: a server owner's choice): the one clock every daily, weekly and monthly reset of the world follows
/// (daily and weekly missions, attendance check-ins, the battle pass week, shop purchase limits, the period collection
/// book, dungeon entry counts). A day starts at <see cref="Hour"/>:00 in <see cref="TimeZone"/> (empty: the host's local
/// zone); a week starts on <see cref="WeeklyDay"/> at that hour; a month on its first day at that hour. Defaults: 05:00,
/// Wednesday (the official weekly day is Wednesday: GlobalSetting reset_week 3, CANDIDATE; the official hour is 07:00 UTC,
/// 16:00 KST). <see cref="Current"/> is set once by the host at startup; the checks set a UTC clock.
/// </summary>
public sealed record ResetClock
{
    public const int DefaultHour = 5;
    public const DayOfWeek DefaultWeeklyDay = DayOfWeek.Wednesday;

    public int Hour { get; init; } = DefaultHour;
    public DayOfWeek WeeklyDay { get; init; } = DefaultWeeklyDay;
    /// <summary>An IANA ("America/Chicago") or Windows ("Central Standard Time") zone id; empty: the host's local zone.</summary>
    public string TimeZone { get; init; } = "";

    /// <summary>The zone the hour is read in (resolved from <see cref="TimeZone"/> by <see cref="Validated"/>). Not bound from configuration.</summary>
    internal TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>The host's clock (Aion:Resets, set at startup). Defaults: 05:00 host local time, weeks from Wednesday.</summary>
    public static ResetClock Current { get; set; } = new();

    /// <summary>A UTC clock (checks: deterministic whatever the machine's zone).</summary>
    public static ResetClock Utc(int hour = DefaultHour, DayOfWeek weeklyDay = DefaultWeeklyDay) =>
        new() { Hour = hour, WeeklyDay = weeklyDay, TimeZone = "UTC", Zone = TimeZoneInfo.Utc };

    /// <summary>Checks the values and resolves the zone; a bad value stops the host at startup.</summary>
    public ResetClock Validated()
    {
        if (Hour is < 0 or > 23) throw new InvalidOperationException($"Aion:Resets:Hour must be between 0 and 23 (got {Hour}).");
        if (!Enum.IsDefined(WeeklyDay)) throw new InvalidOperationException($"Aion:Resets:WeeklyDay must be a day name such as Wednesday (got {WeeklyDay}).");
        if (string.IsNullOrWhiteSpace(TimeZone)) return this with { TimeZone = "", Zone = TimeZoneInfo.Local };
        try { return this with { TimeZone = TimeZone.Trim(), Zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZone.Trim()) }; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException($"Aion:Resets:TimeZone '{TimeZone}' is not a time zone this host knows (use an IANA id such as America/Chicago, or leave it empty for the host's zone).", e);
        }
    }

    /// <summary>The counting day of a moment (unix ms): its local date, the day turning at <see cref="Hour"/>.</summary>
    public DateOnly Day(long nowMs) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), Zone).DateTime.AddHours(-Hour));

    /// <summary>The counting day the week of a moment started on (a <see cref="WeeklyDay"/>).</summary>
    public DateOnly Week(long nowMs)
    {
        var day = Day(nowMs);
        return day.AddDays(-(((int)day.DayOfWeek - (int)WeeklyDay + 7) % 7));
    }

    /// <summary>The first counting day of the month of a moment.</summary>
    public DateOnly Month(long nowMs)
    {
        var day = Day(nowMs);
        return new DateOnly(day.Year, day.Month, 1);
    }

    /// <summary>The moment (unix ms) a counting day starts: <see cref="Hour"/>:00 local (a skipped hour moves on, a repeated one takes the first).</summary>
    public long StartOf(DateOnly day)
    {
        var local = day.ToDateTime(new TimeOnly(Hour, 0), DateTimeKind.Unspecified);
        for (var i = 0; i < 8 && Zone.IsInvalidTime(local); i++) local = local.AddMinutes(30);
        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Max() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }

    public long DayStart(long nowMs) => StartOf(Day(nowMs));
    public long NextDayStart(long nowMs) => StartOf(Day(nowMs).AddDays(1));
    public long WeekStart(long nowMs) => StartOf(Week(nowMs));
    public long NextWeekStart(long nowMs) => StartOf(Week(nowMs).AddDays(7));
    public long MonthStart(long nowMs) => StartOf(Month(nowMs));

    /// <summary>Period keys as stored (shop purchases, dungeon entries): "2026-11-09", "w2026-11-04", "2026-11".</summary>
    public string DayKey(long nowMs) => Day(nowMs).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string WeekKey(long nowMs) => "w" + Week(nowMs).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string MonthKey(long nowMs) => Month(nowMs).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public override string ToString() =>
        $"daily at {Hour:00}:00, weekly on {WeeklyDay} ({(TimeZone.Length == 0 ? $"host zone {Zone.Id}" : Zone.Id)})";
}

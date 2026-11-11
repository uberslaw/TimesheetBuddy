using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Today's totals for the job-code view. Open timers are measured up to the supplied clock
/// so a running row includes the live time.
/// </summary>
public static class DayTotals
{
    public static TimeSpan DurationAt(TimeEntry entry, DateTime utcNow) =>
        WeekCalendar.DurationAt(entry, utcNow);

    public static TimeSpan ForJobCode(IEnumerable<TimeEntry> entries, Guid jobCodeId, DateTime utcNow)
    {
        var total = TimeSpan.Zero;
        foreach (var entry in entries)
        {
            if (entry.JobCodeId != jobCodeId) continue;
            total += DurationAt(entry, utcNow);
        }

        return total;
    }

    public static TimeSpan ForDay(IEnumerable<TimeEntry> entries, DateTime utcNow)
    {
        var total = TimeSpan.Zero;
        foreach (var entry in entries)
            total += DurationAt(entry, utcNow);
        return total;
    }

    public static string FormatClock(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}

using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Today's totals for the job-code view. Open timers are measured up to the supplied clock
/// so a running row includes the live time.
/// </summary>
public static class DayTotals
{
    /// <summary>Standard work day used for the main-window remaining-time line.</summary>
    public static readonly TimeSpan ExpectedWorkDay = TimeSpan.FromHours(7.5);

    public static TimeSpan DurationAt(TimeEntry entry, DateTime utcNow) =>
        WeekCalendar.DurationAt(entry, utcNow);

    public static TimeSpan TimeRemaining(TimeSpan logged) =>
        logged >= ExpectedWorkDay ? TimeSpan.Zero : ExpectedWorkDay - logged;

    public static TimeSpan ForJobCode(IEnumerable<TimeEntry> entries, Guid jobCodeId, DateTime utcNow)
    {
        var total = TimeSpan.Zero;
        foreach (var entry in entries)
        {
            if (entry.IsDeleted || entry.JobCodeId != jobCodeId) continue;
            total += DurationAt(entry, utcNow);
        }

        return total;
    }

    public static TimeSpan ForDay(IEnumerable<TimeEntry> entries, DateTime utcNow)
    {
        var total = TimeSpan.Zero;
        foreach (var entry in entries)
        {
            if (entry.IsDeleted) continue;
            total += DurationAt(entry, utcNow);
        }

        return total;
    }

    /// <summary>
    /// Per-job-code totals for a local calendar day, most time first. Entries without a
    /// code are grouped as "No job code". Open timers count through <paramref name="utcNow"/>.
    /// </summary>
    public static IReadOnlyList<JobCodeDayTotal> ByJobCodeForDay(
        IEnumerable<TimeEntry> entries,
        DateTime dayLocal,
        DateTime utcNow)
    {
        var groups = new Dictionary<string, (string Label, TimeSpan Total)>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.IsDeleted || !TaskTypeSummary.IsOnDay(entry, dayLocal))
                continue;

            var key = entry.JobCodeId is Guid id
                ? "id:" + id.ToString("N")
                : "text:" + (entry.JobCodeText ?? string.Empty).Trim().ToUpperInvariant();
            var label = JobCode.FormatLabelFromStored(entry.JobCodeText);
            if (label == "—")
                label = "No job code";
            if (!groups.TryGetValue(key, out var current))
                groups[key] = (label, DurationAt(entry, utcNow));
            else
                groups[key] = (PreferLabel(current.Label, label), current.Total + DurationAt(entry, utcNow));
        }

        return groups.Values
            .OrderByDescending(g => g.Total)
            .ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => new JobCodeDayTotal(g.Label, g.Total))
            .ToList();
    }

    public static string FormatSummary(IReadOnlyList<JobCodeDayTotal> rows)
    {
        if (rows.Count == 0)
            return "No time logged";

        var width = Math.Max(5, rows.Max(r => r.Label.Length));
        var lines = new List<string>(rows.Count + 2);
        var grand = TimeSpan.Zero;
        foreach (var row in rows)
        {
            grand += row.Total;
            lines.Add($"{row.Label.PadRight(width)}  {FormatClock(row.Total)}");
        }

        lines.Add($"{("Total").PadRight(width)}  {FormatClock(grand)}");
        lines.Add($"{("Time Remaining").PadRight(width)}  {FormatClockHm(TimeRemaining(grand))}");
        return string.Join('\n', lines);
    }

    public static string FormatClock(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    /// <summary>Hours and minutes only (for remaining-time display).</summary>
    public static string FormatClockHm(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}";
    }

    private static string PreferLabel(string existing, string incoming)
    {
        // Prefer the compact suffix — NAME form when available.
        var existingCompact = existing.Contains('—', StringComparison.Ordinal);
        var incomingCompact = incoming.Contains('—', StringComparison.Ordinal);
        if (incomingCompact && !existingCompact) return incoming;
        if (existingCompact && !incomingCompact) return existing;
        return existing.Length >= incoming.Length ? existing : incoming;
    }
}

public readonly record struct JobCodeDayTotal(string Label, TimeSpan Total);

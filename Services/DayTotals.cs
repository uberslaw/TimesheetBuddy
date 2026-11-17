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
            var label = ShortCodeLabel(entry.JobCodeText);
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

        var lines = new List<string>(rows.Count + 1);
        var grand = TimeSpan.Zero;
        foreach (var row in rows)
        {
            grand += row.Total;
            lines.Add($"{row.Label}  {FormatClock(row.Total)}");
        }

        lines.Add($"Total  {FormatClock(grand)}");
        return string.Join('\n', lines);
    }

    public static string FormatClock(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string ShortCodeLabel(string? jobCodeText)
    {
        var text = (jobCodeText ?? string.Empty).Trim();
        if (text.Length == 0)
            return "No job code";

        // Prefer the short code before an em/en dash description.
        var sep = text.IndexOfAny(['—', '–', '-']);
        if (sep > 0)
        {
            var code = text[..sep].Trim();
            if (code.Length > 0)
                return code;
        }

        return text.Length <= 28 ? text : text[..27] + "…";
    }

    private static string PreferLabel(string existing, string incoming) =>
        existing.Length >= incoming.Length ? existing : incoming;
}

public readonly record struct JobCodeDayTotal(string Label, TimeSpan Total);

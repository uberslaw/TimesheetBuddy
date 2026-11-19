using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// One finished or running segment under a task-type row, ordered chronologically.
/// </summary>
public sealed record TaskSegment(
    Guid EntryId,
    DateTime StartUtc,
    TimeSpan Duration,
    bool IsRunning);

/// <summary>
/// One row of the main window's cumulative list: every entry logged on a day that shares
/// a task name, category, and job code.
/// </summary>
public sealed record TaskTypeRow(
    string Key,
    string TaskName,
    string Category,
    Guid? JobCodeId,
    string JobCodeText,
    TimeSpan Total,
    int SegmentCount,
    DateTime LastStartUtc,
    IReadOnlyList<TaskSegment> Segments,
    string Ticket = "");

public static class TaskTypeSummary
{
    public const string DefaultCategory = "General";

    /// <summary>
    /// Task name, category and ticket compare case-insensitively. The job code is its id; an entry
    /// without an id falls back to its code text. Without a ticket the key is the same as before
    /// tickets existed.
    /// </summary>
    public static string KeyOf(string? taskName, string? category, Guid? jobCodeId, string? jobCodeText, string? ticket = null)
    {
        var name = (taskName ?? string.Empty).Trim().ToUpperInvariant();
        var cat = NormalizeCategory(category).ToUpperInvariant();
        var code = jobCodeId is Guid id
            ? "id:" + id.ToString("N")
            : "text:" + (jobCodeText ?? string.Empty).Trim().ToUpperInvariant();
        var key = string.Join('\u001f', name, cat, code);
        var t = TicketRules.Normalize(ticket);
        return t.Length == 0 ? key : key + "\u001fticket:" + t.ToUpperInvariant();
    }

    public static string KeyOf(TimeEntry entry) =>
        KeyOf(entry.TaskName, entry.Category, entry.JobCodeId, entry.JobCodeText, entry.Ticket);

    public static string KeyOf(ActiveSession session) =>
        KeyOf(session.TaskName, session.Category, session.JobCodeId, session.JobCodeText, session.Ticket);

    public static bool IsOnDay(TimeEntry entry, DateTime dayLocal)
    {
        var start = entry.StartUtc.ToLocalTime().Date;
        var end = (entry.EndUtc ?? entry.StartUtc).ToLocalTime().Date;
        return start == dayLocal.Date || end == dayLocal.Date;
    }

    /// <summary>
    /// Rows for <paramref name="dayLocal"/>, most recently started first. An open entry
    /// (the running timer) is measured up to <paramref name="utcNow"/>. Soft-deleted
    /// entries are skipped.
    /// </summary>
    public static List<TaskTypeRow> Build(IEnumerable<TimeEntry> entries, DateTime dayLocal, DateTime utcNow)
    {
        var groups = new Dictionary<string, List<TimeEntry>>();
        var seen = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (entry.IsDeleted || !IsOnDay(entry, dayLocal) || !seen.Add(entry.Id))
                continue;

            var key = KeyOf(entry);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(entry);
        }

        var rows = new List<TaskTypeRow>(groups.Count);
        foreach (var (key, list) in groups)
        {
            var latest = list.MaxBy(e => e.StartUtc)!;
            var total = TimeSpan.Zero;
            var segments = new List<TaskSegment>(list.Count);
            foreach (var entry in list.OrderBy(e => e.StartUtc))
            {
                var duration = DayTotals.DurationAt(entry, utcNow);
                total += duration;
                segments.Add(new TaskSegment(
                    entry.Id,
                    entry.StartUtc,
                    duration,
                    IsRunning: entry.EndUtc is null && entry.ManualMinutes <= 0));
            }

            rows.Add(new TaskTypeRow(
                key,
                latest.TaskName.Trim(),
                NormalizeCategory(latest.Category),
                latest.JobCodeId,
                latest.JobCodeText.Trim(),
                total,
                list.Count,
                latest.StartUtc,
                segments,
                TicketRules.Normalize(latest.Ticket)));
        }

        return rows
            .OrderByDescending(r => r.LastStartUtc)
            .ThenBy(r => r.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? DefaultCategory : category.Trim();
}

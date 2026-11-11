using System.Globalization;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Groups stored entries into Monday–Sunday weeks labeled Monday–Friday.
/// Bucketing uses the local date of <see cref="TimeEntry.StartUtc"/>.
/// </summary>
public static class WeekCalendar
{
    private static readonly CultureInfo Labels = CultureInfo.InvariantCulture;

    public static DateTime MondayOf(DateTime localDate)
    {
        var date = localDate.Date;
        var daysFromMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysFromMonday);
    }

    /// <summary>
    /// Duration used for week totals. Manual minutes win; an open timer uses <paramref name="utcNow"/>.
    /// </summary>
    public static TimeSpan DurationAt(TimeEntry entry, DateTime utcNow)
    {
        if (entry.ManualMinutes > 0)
            return TimeSpan.FromMinutes(entry.ManualMinutes);

        var end = entry.EndUtc ?? utcNow;
        var span = end - entry.StartUtc;
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        var seconds = duration.Seconds;

        if (hours > 0 && minutes == 0 && seconds == 0)
            return $"{hours}h";
        if (hours > 0 && seconds == 0)
            return $"{hours}h {minutes}m";
        if (hours > 0)
            return $"{hours}h {minutes}m {seconds}s";
        if (minutes > 0 && seconds == 0)
            return $"{minutes}m";
        if (minutes > 0)
            return $"{minutes}m {seconds}s";
        if (seconds > 0)
            return $"{seconds}s";
        return "0m";
    }

    public static IReadOnlyList<WorkWeek> Collect(
        IEnumerable<TimeEntry> entries,
        DateTime nowLocal,
        DateTime? utcNow = null)
    {
        var clockUtc = utcNow ?? DateTime.UtcNow;
        var grouped = new Dictionary<DateTime, List<TimeEntry>>();

        foreach (var entry in entries)
        {
            var monday = MondayOf(entry.StartUtc.ToLocalTime());
            if (!grouped.TryGetValue(monday, out var list))
            {
                list = [];
                grouped[monday] = list;
            }

            list.Add(entry);
        }

        var currentMonday = MondayOf(nowLocal);
        if (!grouped.ContainsKey(currentMonday))
            grouped[currentMonday] = [];

        return grouped
            .OrderBy(pair => pair.Key)
            .Select(pair => BuildWeek(pair.Key, pair.Value, clockUtc))
            .ToList();
    }

    private static WorkWeek BuildWeek(DateTime monday, List<TimeEntry> entries, DateTime utcNow)
    {
        var friday = monday.AddDays(4);
        var days = new List<WeekDay>(7);
        for (var offset = 0; offset < 7; offset++)
        {
            var date = monday.AddDays(offset);
            var tasks = entries
                .Where(entry => entry.StartUtc.ToLocalTime().Date == date)
                .OrderBy(entry => entry.StartUtc)
                .Select(entry => ToRow(entry, utcNow))
                .ToList();

            days.Add(new WeekDay
            {
                Date = date,
                Heading = date.ToString("dddd d MMM yyyy", Labels),
                IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                Tasks = tasks
            });
        }

        var total = TimeSpan.Zero;
        foreach (var entry in entries)
            total += DurationAt(entry, utcNow);

        var taskCount = entries.Count;
        var hours = FormatDuration(total);
        return new WorkWeek
        {
            Monday = monday.Date,
            Friday = friday.Date,
            Label = FormatRange(monday, friday),
            TaskCount = taskCount,
            Total = total,
            HoursDisplay = hours,
            Summary = taskCount == 1 ? $"1 task · {hours}" : $"{taskCount} tasks · {hours}",
            Days = days
        };
    }

    public static string FormatRange(DateTime monday, DateTime friday) =>
        $"{monday.ToString("ddd d MMM yyyy", Labels)} – {friday.ToString("ddd d MMM yyyy", Labels)}";

    private static WeekTaskRow ToRow(TimeEntry entry, DateTime utcNow)
    {
        var startLocal = entry.StartUtc.ToLocalTime();
        var endLocal = entry.EndUtc?.ToLocalTime();
        var duration = DurationAt(entry, utcNow);
        var running = entry.EndUtc is null && entry.ManualMinutes <= 0;
        var time = running
            ? $"{startLocal:HH:mm}–running"
            : endLocal is DateTime end
                ? $"{startLocal:HH:mm}–{end:HH:mm}"
                : FormatDuration(duration);

        return new WeekTaskRow
        {
            DateDisplay = startLocal.ToString("ddd d MMM yyyy", Labels),
            TimeDisplay = time,
            TaskName = string.IsNullOrWhiteSpace(entry.TaskName) ? "(untitled)" : entry.TaskName.Trim(),
            JobCode = entry.JobCodeText?.Trim() ?? string.Empty,
            Category = entry.Category?.Trim() ?? string.Empty,
            Notes = entry.Notes?.Trim() ?? string.Empty,
            Duration = duration,
            DurationDisplay = FormatDuration(duration),
            IsRunning = running
        };
    }
}

public sealed class WorkWeek
{
    public required DateTime Monday { get; init; }
    public required DateTime Friday { get; init; }
    public required string Label { get; init; }
    public required int TaskCount { get; init; }
    public required TimeSpan Total { get; init; }
    public required string HoursDisplay { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<WeekDay> Days { get; init; }

    /// <summary>Monday–Friday always, plus Saturday/Sunday only when they have entries.</summary>
    public IEnumerable<WeekDay> BrowseDays =>
        Days.Where(day => !day.IsWeekend || day.Tasks.Count > 0);
}

public sealed class WeekDay
{
    public required DateTime Date { get; init; }
    public required string Heading { get; init; }
    public required bool IsWeekend { get; init; }
    public required IReadOnlyList<WeekTaskRow> Tasks { get; init; }
    public string EmptyMessage => Tasks.Count == 0 ? "No entries" : string.Empty;
}

public sealed class WeekTaskRow
{
    public required string DateDisplay { get; init; }
    public required string TimeDisplay { get; init; }
    public required string TaskName { get; init; }
    public required string JobCode { get; init; }
    public required string Category { get; init; }
    public required string Notes { get; init; }
    public required TimeSpan Duration { get; init; }
    public required string DurationDisplay { get; init; }
    public required bool IsRunning { get; init; }

    public string Meta
    {
        get
        {
            var code = string.IsNullOrWhiteSpace(JobCode) ? "—" : JobCode;
            var category = string.IsNullOrWhiteSpace(Category) ? "—" : Category;
            return $"{code}  ·  {category}  ·  {DurationDisplay}";
        }
    }
}

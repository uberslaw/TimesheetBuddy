using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Maths for the catch-up prompt over the last X minutes (X is the existing
/// catch-up interval, <see cref="AppState.CatchUpIntervalMinutes"/>).
/// </summary>
/// <remarks>
/// Window is [now − X minutes, now] as UTC instants (the same moment as local now).
/// Accounted time is the union of each stored entry's overlap with that window.
/// A running timer is included once, from the earlier of the entry start and the
/// session start through now (the current elapsed value). That open entry is not
/// added a second time from <see cref="ActiveSession"/>.
/// When an entry has both <see cref="TimeEntry.StartUtc"/> and <see cref="TimeEntry.EndUtc"/>,
/// that interval is used (manual minutes are not added on top). If <c>EndUtc</c> is
/// missing and the entry is the active session, the end is now. Otherwise a positive
/// <see cref="TimeEntry.ManualMinutes"/> is anchored at <c>StartUtc</c>.
/// Unaccounted = the uncovered gap, in whole minutes: under 30 seconds is 0 so a
/// few seconds of drift does not open the dialog; otherwise round to the nearest
/// minute (midpoint away from zero) and clamp to 0…X.
/// </remarks>
public static class UnaccountedTime
{
    public static readonly TimeSpan GapIgnoredBelow = TimeSpan.FromSeconds(30);

    public static UnaccountedWindow Measure(
        IEnumerable<TimeEntry>? entries,
        ActiveSession? activeSession,
        int windowMinutes,
        DateTime nowUtc)
    {
        nowUtc = AsUtc(nowUtc);
        if (windowMinutes <= 0)
        {
            return new UnaccountedWindow
            {
                WindowStartUtc = nowUtc,
                WindowEndUtc = nowUtc,
                WindowMinutes = 0,
                AccountedMinutes = 0,
                UnaccountedMinutes = 0,
                Gaps = []
            };
        }

        var windowEnd = nowUtc;
        var windowStart = windowEnd.AddMinutes(-windowMinutes);
        var covered = new List<UtcInterval>();
        var activeIncluded = false;

        foreach (var entry in entries ?? [])
        {
            if (entry is null) continue;

            var isActive = activeSession is not null && entry.Id == activeSession.EntryId;
            if (!TryEntryInterval(entry, isActive, activeSession, nowUtc, out var start, out var end))
                continue;

            if (isActive) activeIncluded = true;
            AddClipped(covered, start, end, windowStart, windowEnd);
        }

        if (activeSession is not null && !activeIncluded)
            AddClipped(covered, AsUtc(activeSession.StartedUtc), nowUtc, windowStart, windowEnd);

        var merged = Merge(covered);
        var accounted = TimeSpan.Zero;
        foreach (var span in merged)
            accounted += span.Duration;

        var windowLength = TimeSpan.FromMinutes(windowMinutes);
        if (accounted > windowLength) accounted = windowLength;
        var gap = windowLength - accounted;
        if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;

        int unaccounted;
        if (gap < GapIgnoredBelow)
            unaccounted = 0;
        else
            unaccounted = (int)Math.Round(gap.TotalSeconds / 60.0, MidpointRounding.AwayFromZero);

        if (unaccounted < 0) unaccounted = 0;
        if (unaccounted > windowMinutes) unaccounted = windowMinutes;

        return new UnaccountedWindow
        {
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            WindowMinutes = windowMinutes,
            AccountedMinutes = windowMinutes - unaccounted,
            UnaccountedMinutes = unaccounted,
            Gaps = GapsInside(windowStart, windowEnd, merged)
        };
    }

    public static CatchUpPromptKind Decide(UnaccountedWindow window, bool timerRunning) =>
        Decide(window.UnaccountedMinutes, timerRunning);

    public static CatchUpPromptKind Decide(int unaccountedMinutes, bool timerRunning)
    {
        if (unaccountedMinutes >= 1)
            return CatchUpPromptKind.AssignTasks;
        if (timerRunning)
            return CatchUpPromptKind.StillWorking;
        return CatchUpPromptKind.None;
    }

    public static string AssignPromptText(int unaccountedMinutes) =>
        $"{unaccountedMinutes} unaccounted for minutes, would you like to assign tasks to them?";

    /// <summary>
    /// Places assignments in the uncovered gaps, latest gap first, without
    /// overlapping time that is already logged. The first slice sits closest to now.
    /// A slice that cannot fit on the clock keeps its assigned minutes on the
    /// last segment; the stored interval stays inside the real gap.
    /// </summary>
    public static List<TimeEntry> Place(IReadOnlyList<CatchUpSlice> slices, UnaccountedWindow window)
    {
        var gaps = window.Gaps
            .Where(g => g.EndUtc > g.StartUtc)
            .Select(g => (Start: g.StartUtc, End: g.EndUtc))
            .ToList();

        var entries = new List<TimeEntry>();
        foreach (var slice in slices)
        {
            if (slice.Minutes <= 0) continue;

            var need = TimeSpan.FromMinutes(slice.Minutes);
            var segments = new List<(DateTime Start, DateTime End)>();
            for (var i = gaps.Count - 1; i >= 0 && need > TimeSpan.Zero; i--)
            {
                var available = gaps[i].End - gaps[i].Start;
                if (available <= TimeSpan.Zero) continue;

                var take = need < available ? need : available;
                var start = gaps[i].End - take;
                segments.Add((start, gaps[i].End));
                gaps[i] = (gaps[i].Start, start);
                need -= take;
            }

            if (segments.Count == 0) continue;
            segments.Reverse();

            var minutesLeft = slice.Minutes;
            for (var s = 0; s < segments.Count; s++)
            {
                var seg = segments[s];
                if (seg.End <= seg.Start) continue;

                var isLast = s == segments.Count - 1;
                int minutes;
                if (isLast)
                {
                    minutes = minutesLeft;
                }
                else
                {
                    minutes = (int)Math.Round((seg.End - seg.Start).TotalMinutes, MidpointRounding.AwayFromZero);
                    if (minutes < 1) minutes = 1;
                    if (minutes > minutesLeft - 1) minutes = Math.Max(1, minutesLeft - 1);
                }

                if (minutes <= 0) continue;
                minutesLeft -= minutes;

                entries.Add(new TimeEntry
                {
                    StartUtc = AsUtc(seg.Start),
                    EndUtc = AsUtc(seg.End),
                    TaskName = string.IsNullOrWhiteSpace(slice.TaskName) ? "Catch-up entry" : slice.TaskName.Trim(),
                    Category = string.IsNullOrWhiteSpace(slice.Category) ? "General" : slice.Category.Trim(),
                    JobCodeId = slice.JobCodeId,
                    JobCodeText = slice.JobCodeText?.Trim() ?? string.Empty,
                    Notes = slice.Notes?.Trim() ?? string.Empty,
                    ManualMinutes = minutes,
                    Source = "fifteen"
                });
            }
        }

        return entries;
    }

    private static bool TryEntryInterval(
        TimeEntry entry,
        bool isActive,
        ActiveSession? activeSession,
        DateTime nowUtc,
        out DateTime startUtc,
        out DateTime endUtc)
    {
        startUtc = AsUtc(entry.StartUtc);

        if (entry.EndUtc is DateTime end)
        {
            endUtc = AsUtc(end);
        }
        else if (isActive)
        {
            if (activeSession is not null)
            {
                var started = AsUtc(activeSession.StartedUtc);
                if (started < startUtc) startUtc = started;
            }

            endUtc = nowUtc;
        }
        else if (entry.ManualMinutes > 0)
        {
            endUtc = startUtc.AddMinutes(entry.ManualMinutes);
        }
        else
        {
            endUtc = startUtc;
            return false;
        }

        return endUtc > startUtc;
    }

    private static void AddClipped(
        List<UtcInterval> covered,
        DateTime start,
        DateTime end,
        DateTime windowStart,
        DateTime windowEnd)
    {
        var clippedStart = start > windowStart ? start : windowStart;
        var clippedEnd = end < windowEnd ? end : windowEnd;
        if (clippedEnd > clippedStart)
            covered.Add(new UtcInterval(AsUtc(clippedStart), AsUtc(clippedEnd)));
    }

    private static List<UtcInterval> Merge(List<UtcInterval> intervals)
    {
        if (intervals.Count == 0) return intervals;

        intervals.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        var merged = new List<UtcInterval>(intervals.Count);
        var current = intervals[0];
        for (var i = 1; i < intervals.Count; i++)
        {
            var next = intervals[i];
            if (next.StartUtc <= current.EndUtc)
            {
                if (next.EndUtc > current.EndUtc)
                    current = new UtcInterval(current.StartUtc, next.EndUtc);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    private static List<UtcInterval> GapsInside(
        DateTime windowStart,
        DateTime windowEnd,
        List<UtcInterval> covered)
    {
        var gaps = new List<UtcInterval>();
        var cursor = windowStart;
        foreach (var span in covered)
        {
            if (span.StartUtc > cursor)
                gaps.Add(new UtcInterval(cursor, span.StartUtc));
            if (span.EndUtc > cursor) cursor = span.EndUtc;
        }

        if (cursor < windowEnd)
            gaps.Add(new UtcInterval(cursor, windowEnd));

        return gaps;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public enum CatchUpPromptKind
{
    None = 0,
    AssignTasks = 1,
    StillWorking = 2
}

public sealed class UnaccountedWindow
{
    public DateTime WindowStartUtc { get; init; }
    public DateTime WindowEndUtc { get; init; }
    public int WindowMinutes { get; init; }
    public int AccountedMinutes { get; init; }
    public int UnaccountedMinutes { get; init; }
    public IReadOnlyList<UtcInterval> Gaps { get; init; } = [];
}

public readonly record struct UtcInterval(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

public sealed record CatchUpSlice
{
    public string TaskName { get; init; } = string.Empty;
    public string Category { get; init; } = "General";
    public Guid? JobCodeId { get; init; }
    public string JobCodeText { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public int Minutes { get; init; }
}

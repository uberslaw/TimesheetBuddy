using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Rules for the "No Task Selected" timer: time between stopping a task and starting the next.
/// </summary>
/// <remarks>
/// A run starts when the user stops a task and ends when any task starts (or the app closes).
/// Runs under <see cref="MinimumRun"/> are dropped. Longer runs become
/// <see cref="UnassignedSegment"/>s, stored apart from the time entries, so no total, list,
/// week summary or export counts them until they are assigned. Open time is the pending
/// segments (plus the running run once it reaches a minute) minus anything an entry already
/// covers; assignments are placed inside it latest first and then removed from it.
/// </remarks>
public static class NoTaskTime
{
    public const string Label = "No Task Selected";
    public const string AssignedSource = "no-task";
    public static readonly TimeSpan MinimumRun = TimeSpan.FromMinutes(1);

    /// <summary>Ends a run at <paramref name="endUtc"/>. Returns true when it was long enough to keep.</summary>
    public static bool Close(List<UnassignedSegment> pending, DateTime? startedUtc, DateTime endUtc)
    {
        if (startedUtc is not DateTime started)
            return false;

        var start = AsUtc(started);
        var end = AsUtc(endUtc);
        if (end - start < MinimumRun)
            return false;

        pending.Add(new UnassignedSegment { StartUtc = start, EndUtc = end });
        return true;
    }

    /// <summary>
    /// Unassigned time not already covered by an entry, merged and in clock order. The running
    /// run counts once it reaches a minute, or always when <paramref name="includeShortRun"/> is set.
    /// </summary>
    public static List<UtcInterval> Open(
        IEnumerable<UnassignedSegment>? pending,
        DateTime? runningStartUtc,
        IEnumerable<TimeEntry>? entries,
        DateTime nowUtc,
        bool includeShortRun = false)
    {
        var now = AsUtc(nowUtc);
        var spans = Spans(pending, null, now);
        if (runningStartUtc is DateTime running)
        {
            var start = AsUtc(running);
            if (now > start && (includeShortRun || now - start >= MinimumRun))
                spans.Add(new UtcInterval(start, now));
        }

        var covered = new List<UtcInterval>();
        foreach (var entry in entries ?? [])
        {
            if (entry is null || entry.IsDeleted) continue;
            var start = AsUtc(entry.StartUtc);
            var end = entry.EndUtc is DateTime e
                ? AsUtc(e)
                : entry.ManualMinutes > 0 ? start.AddMinutes(entry.ManualMinutes) : now;
            if (end > start)
                covered.Add(new UtcInterval(start, end));
        }

        return Subtract(Merge(spans), Merge(covered));
    }

    /// <summary>Pending and running spans as they are, for the catch-up check to treat as known time.</summary>
    public static List<UtcInterval> Spans(IEnumerable<UnassignedSegment>? pending, DateTime? runningStartUtc, DateTime nowUtc)
    {
        var now = AsUtc(nowUtc);
        var spans = new List<UtcInterval>();
        foreach (var segment in pending ?? [])
        {
            if (segment is null) continue;
            var start = AsUtc(segment.StartUtc);
            var end = AsUtc(segment.EndUtc);
            if (end > start)
                spans.Add(new UtcInterval(start, end));
        }

        if (runningStartUtc is DateTime running && now > AsUtc(running))
            spans.Add(new UtcInterval(AsUtc(running), now));
        return spans;
    }

    public static TimeSpan Total(IEnumerable<UtcInterval> spans)
    {
        var total = TimeSpan.Zero;
        foreach (var span in spans)
            if (span.EndUtc > span.StartUtc)
                total += span.Duration;
        return total;
    }

    /// <summary>Whole minutes on offer in the dialog (rounded down, so an assignment always fits).</summary>
    public static int WholeMinutes(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? 0 : (int)Math.Floor(duration.TotalMinutes);

    public static bool ShouldPrompt(IEnumerable<UtcInterval> open) => WholeMinutes(Total(open)) >= 1;

    /// <summary>The part of <paramref name="spans"/> on a local calendar day.</summary>
    public static TimeSpan TotalForDay(IEnumerable<UtcInterval> spans, DateTime dayLocal)
    {
        var dayStart = DateTime.SpecifyKind(dayLocal.Date, DateTimeKind.Local).ToUniversalTime();
        var dayEnd = DateTime.SpecifyKind(dayLocal.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime();
        var total = TimeSpan.Zero;
        foreach (var span in spans)
        {
            var start = span.StartUtc > dayStart ? span.StartUtc : dayStart;
            var end = span.EndUtc < dayEnd ? span.EndUtc : dayEnd;
            if (end > start)
                total += end - start;
        }

        return total;
    }

    public static string PromptText(int minutes) =>
        minutes == 1
            ? $"1 minute was logged as {Label}. Where should it go?"
            : $"{minutes} minutes were logged as {Label}. Where should they go?";

    /// <summary>Null when the slices can be assigned; otherwise the reason they cannot.</summary>
    public static string? Validate(IReadOnlyList<CatchUpSlice> slices, IReadOnlyList<UtcInterval> open)
    {
        var available = WholeMinutes(Total(open));
        if (slices.Any(s => s.Minutes < 0))
            return "Minutes cannot be negative.";

        var assigned = slices.Sum(s => s.Minutes);
        if (assigned <= 0)
            return "Assign at least 1 minute, or choose Not now.";
        if (assigned > available)
            return available == 1
                ? "Only 1 minute is waiting to be assigned."
                : $"Only {available} minutes are waiting to be assigned.";
        return null;
    }

    /// <summary>
    /// Entries for the slices, inside the open spans, latest first, never overlapping each other
    /// or anything already logged. The first slice sits closest to now.
    /// </summary>
    public static List<TimeEntry> Place(IReadOnlyList<CatchUpSlice> slices, IReadOnlyList<UtcInterval> open)
    {
        var placed = UnaccountedTime.Place(slices, new UnaccountedWindow { Gaps = open });
        foreach (var entry in placed)
            entry.Source = AssignedSource;
        return placed;
    }

    /// <summary>
    /// What stays pending once the assigned entries are logged: the old segments minus every
    /// entry. Leftovers under a minute in total are dropped, since they would never be asked about.
    /// </summary>
    public static List<UnassignedSegment> Remaining(
        IEnumerable<UnassignedSegment> pending,
        IEnumerable<TimeEntry> entries,
        DateTime nowUtc)
    {
        var open = Open(pending, null, entries, nowUtc);
        if (WholeMinutes(Total(open)) < 1)
            return [];

        return open
            .Select(span => new UnassignedSegment { StartUtc = span.StartUtc, EndUtc = span.EndUtc })
            .ToList();
    }

    private static List<UtcInterval> Merge(List<UtcInterval> spans)
    {
        if (spans.Count == 0) return spans;

        spans.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        var merged = new List<UtcInterval>(spans.Count);
        var current = spans[0];
        for (var i = 1; i < spans.Count; i++)
        {
            var next = spans[i];
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

    private static List<UtcInterval> Subtract(List<UtcInterval> spans, List<UtcInterval> covered)
    {
        var result = new List<UtcInterval>();
        foreach (var span in spans)
        {
            var cursor = span.StartUtc;
            foreach (var cover in covered)
            {
                if (cover.EndUtc <= cursor) continue;
                if (cover.StartUtc >= span.EndUtc) break;
                if (cover.StartUtc > cursor)
                    result.Add(new UtcInterval(cursor, cover.StartUtc));
                if (cover.EndUtc > cursor)
                    cursor = cover.EndUtc;
                if (cursor >= span.EndUtc) break;
            }

            if (cursor < span.EndUtc)
                result.Add(new UtcInterval(cursor, span.EndUtc));
        }

        return result;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

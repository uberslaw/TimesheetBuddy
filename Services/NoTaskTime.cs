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
/// covers; assignments are placed inside it latest first and then removed from it. The
/// Today-by-task No Task Selected row moves or discards a whole day's open time the same way
/// (<see cref="MoveDay"/>, <see cref="DiscardFromDay"/>), so both paths read and shrink one store.
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
        var (dayStart, dayEnd) = DayBounds(dayLocal);
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

    /// <summary>Where the Today-by-task No Task Selected row's time goes on Move Time….</summary>
    public sealed record MoveTarget(string TaskName, string Category, Guid? JobCodeId, string JobCodeText, string Ticket = "");

    public sealed class DayEditResult
    {
        public bool Applied { get; init; }
        public string? Error { get; init; }
        public List<UnassignedSegment> Pending { get; init; } = [];
        public DateTime? RunningStartUtc { get; init; }
        public List<TimeEntry> Added { get; init; } = [];
        public TimeSpan Amount { get; init; }
    }

    public const string MovedNote = "Moved from No Task Selected";

    /// <summary>
    /// The No Task Selected time the Today-by-task row shows for a day: pending plus the running run
    /// from its first second, minus anything an entry covers, clipped to that local day.
    /// </summary>
    public static List<UtcInterval> ForDay(
        IEnumerable<UnassignedSegment>? pending,
        DateTime? runningStartUtc,
        IEnumerable<TimeEntry>? entries,
        DateTime dayLocal,
        DateTime nowUtc) =>
        ClipToDay(Open(pending, runningStartUtc, entries, nowUtc, includeShortRun: true), dayLocal);

    public static List<UtcInterval> ClipToDay(IEnumerable<UtcInterval> spans, DateTime dayLocal)
    {
        var (dayStart, dayEnd) = DayBounds(dayLocal);
        var clipped = new List<UtcInterval>();
        foreach (var span in spans)
        {
            var start = span.StartUtc > dayStart ? span.StartUtc : dayStart;
            var end = span.EndUtc < dayEnd ? span.EndUtc : dayEnd;
            if (end > start)
                clipped.Add(new UtcInterval(start, end));
        }

        return clipped;
    }

    /// <summary>
    /// Move Time… on the No Task Selected row: every open span of the day becomes a finished entry for
    /// <paramref name="target"/> with the span's own start and end, and that time leaves pending. A
    /// running run on the day is closed at now for this (whatever its length) and restarts from now, so
    /// the same seconds can never be assigned again by the interval prompt.
    /// </summary>
    public static DayEditResult MoveDay(
        IEnumerable<UnassignedSegment>? pending,
        DateTime? runningStartUtc,
        IEnumerable<TimeEntry> entries,
        DateTime dayLocal,
        DateTime nowUtc,
        MoveTarget target)
    {
        var now = AsUtc(nowUtc);
        var logged = entries.ToList();
        var spans = ForDay(pending, runningStartUtc, logged, dayLocal, now);
        var total = Total(spans);
        if (total <= TimeSpan.Zero)
            return new DayEditResult { Error = $"There is no {Label} time on this day to move." };

        var name = string.IsNullOrWhiteSpace(target.TaskName) ? "Untitled task" : target.TaskName.Trim();
        var category = string.IsNullOrWhiteSpace(target.Category) ? "General" : target.Category.Trim();
        var (folded, running) = FoldRunning(pending, runningStartUtc, dayLocal, now);
        var added = spans
            .Select(s => new TimeEntry
            {
                StartUtc = s.StartUtc,
                EndUtc = s.EndUtc,
                TaskName = name,
                Category = category,
                JobCodeId = target.JobCodeId,
                JobCodeText = target.JobCodeText?.Trim() ?? string.Empty,
                Ticket = TicketRules.Normalize(target.Ticket),
                Notes = MovedNote,
                Source = AssignedSource
            })
            .ToList();

        return new DayEditResult
        {
            Applied = true,
            Pending = Remaining(folded, logged.Concat(added), now),
            RunningStartUtc = running,
            Added = added,
            Amount = total
        };
    }

    /// <summary>
    /// Discard on the No Task Selected row: drops <paramref name="amount"/> of the day's open time, newest
    /// first (null = all of it). Nothing is logged. More than the day holds is refused, never capped.
    /// </summary>
    public static DayEditResult DiscardFromDay(
        IEnumerable<UnassignedSegment>? pending,
        DateTime? runningStartUtc,
        IEnumerable<TimeEntry> entries,
        DateTime dayLocal,
        DateTime nowUtc,
        TimeSpan? amount)
    {
        var now = AsUtc(nowUtc);
        var logged = entries.ToList();
        var spans = ForDay(pending, runningStartUtc, logged, dayLocal, now);
        var available = Total(spans);
        if (available <= TimeSpan.Zero)
            return new DayEditResult { Error = $"There is no {Label} time on this day to discard." };

        var take = amount ?? available;
        if (take <= TimeSpan.Zero)
            return new DayEditResult { Error = "Enter at least 1 minute to discard." };
        if (take > available)
            return new DayEditResult { Error = OverDiscardMessage(available) };

        var drop = new List<UtcInterval>();
        var left = take;
        for (var i = spans.Count - 1; i >= 0 && left > TimeSpan.Zero; i--)
        {
            var span = spans[i];
            var cut = left < span.Duration ? left : span.Duration;
            drop.Add(new UtcInterval(span.EndUtc - cut, span.EndUtc));
            left -= cut;
        }

        var (folded, running) = FoldRunning(pending, runningStartUtc, dayLocal, now);
        var kept = Subtract(Merge(Spans(folded, null, now)), Merge(drop))
            .Select(s => new UnassignedSegment { StartUtc = s.StartUtc, EndUtc = s.EndUtc });

        return new DayEditResult
        {
            Applied = true,
            Pending = Remaining(kept, logged, now),
            RunningStartUtc = running,
            Amount = take
        };
    }

    public static string OverDiscardMessage(TimeSpan available)
    {
        var wholeMinutes = (int)Math.Floor(available.TotalMinutes);
        return $"You can discard at most {DayTotals.FormatCompact(available)} of {Label} time on this day " +
               $"({wholeMinutes / 60} h {wholeMinutes % 60} min in whole minutes).";
    }

    /// <summary>
    /// A copy of pending with a running run that touches the day closed at now (no 1-minute rule), and
    /// the running start to keep: now when it was folded, otherwise unchanged.
    /// </summary>
    private static (List<UnassignedSegment> Pending, DateTime? RunningStartUtc) FoldRunning(
        IEnumerable<UnassignedSegment>? pending,
        DateTime? runningStartUtc,
        DateTime dayLocal,
        DateTime now)
    {
        var copy = (pending ?? [])
            .Where(s => s is not null)
            .Select(s => new UnassignedSegment { StartUtc = AsUtc(s.StartUtc), EndUtc = AsUtc(s.EndUtc) })
            .ToList();
        if (runningStartUtc is not DateTime running)
            return (copy, null);

        var start = AsUtc(running);
        var (dayStart, dayEnd) = DayBounds(dayLocal);
        if (now <= start || start >= dayEnd || now <= dayStart)
            return (copy, start);

        copy.Add(new UnassignedSegment { StartUtc = start, EndUtc = now });
        return (copy, now);
    }

    private static (DateTime Start, DateTime End) DayBounds(DateTime dayLocal) =>
        (DateTime.SpecifyKind(dayLocal.Date, DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(dayLocal.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime());

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

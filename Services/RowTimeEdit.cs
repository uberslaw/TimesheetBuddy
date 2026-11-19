using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Right-click edits on a Today-by-task row: removing time from its finished segments and
/// moving all of its time to another job code.
/// A row is every non-deleted entry on the day with the row's task-type key.
/// </summary>
public static class RowTimeEdit
{
    public sealed class RemoveResult
    {
        public bool Applied { get; init; }
        public string? Error { get; init; }
        public TimeSpan Removable { get; init; }
        public int Shortened { get; init; }
        public int Deleted { get; init; }
    }

    public static List<TimeEntry> RowEntries(IEnumerable<TimeEntry> entries, DateTime dayLocal, string rowKey) =>
        entries
            .Where(e => !e.IsDeleted &&
                        TaskTypeSummary.IsOnDay(e, dayLocal) &&
                        TaskTypeSummary.KeyOf(e) == rowKey)
            .ToList();

    /// <summary>The live timer segment: never trimmed, and not part of the removable time.</summary>
    public static bool IsRunning(TimeEntry entry, Guid? runningEntryId) =>
        entry.Id == runningEntryId || (entry.EndUtc is null && entry.ManualMinutes <= 0);

    /// <summary>Finished time on the row that Remove may take.</summary>
    public static TimeSpan RemovableTime(IEnumerable<TimeEntry> rowEntries, Guid? runningEntryId)
    {
        var total = TimeSpan.Zero;
        foreach (var entry in rowEntries)
        {
            if (!IsRunning(entry, runningEntryId))
                total += WeekCalendar.DurationAt(entry, entry.EndUtc ?? entry.StartUtc);
        }

        return total;
    }

    /// <summary>
    /// Takes <paramref name="amount"/> off the row's finished segments, most recent first. A timer
    /// segment is shortened by moving its start later; a manual one loses minutes from
    /// <see cref="TimeEntry.ManualMinutes"/>. Segments that reach zero are removed from
    /// <paramref name="entries"/>. More than the finished time is refused, never capped.
    /// </summary>
    public static RemoveResult Remove(
        IList<TimeEntry> entries,
        DateTime dayLocal,
        string rowKey,
        Guid? runningEntryId,
        TimeSpan amount)
    {
        var row = RowEntries(entries, dayLocal, rowKey);
        var finished = row.Where(e => !IsRunning(e, runningEntryId)).ToList();
        var removable = RemovableTime(finished, runningEntryId);

        if (amount <= TimeSpan.Zero)
            return new RemoveResult { Error = "Enter at least 1 minute to remove.", Removable = removable };
        if (row.Count == 0)
            return new RemoveResult { Error = "That task no longer has time on this day.", Removable = removable };
        if (amount > removable)
            return new RemoveResult { Error = OverRemovalMessage(removable), Removable = removable };

        var left = amount;
        var shortened = 0;
        var deleted = 0;
        foreach (var entry in finished
                     .OrderByDescending(e => e.EndUtc ?? e.StartUtc)
                     .ThenByDescending(e => e.StartUtc))
        {
            if (left <= TimeSpan.Zero) break;

            var duration = WeekCalendar.DurationAt(entry, entry.EndUtc ?? entry.StartUtc);
            if (duration <= TimeSpan.Zero) continue;

            if (left >= duration)
            {
                entries.Remove(entry);
                left -= duration;
                deleted++;
                continue;
            }

            if (entry.ManualMinutes > 0)
                TrimManual(entry, left);
            else
                entry.StartUtc += left;
            left = TimeSpan.Zero;
            shortened++;
        }

        return new RemoveResult
        {
            Applied = true,
            Removable = removable,
            Shortened = shortened,
            Deleted = deleted
        };
    }

    public sealed class MoveResult
    {
        public bool Applied { get; init; }
        public string? Error { get; init; }
        public int Moved { get; init; }
        public string? NewKey { get; init; }
        public ActiveSession? ActiveSession { get; init; }
        public bool ActiveMutated { get; init; }
    }

    /// <summary>True when an entry's code (id, or text when it has no id) is the target.</summary>
    public static bool IsSameCode(Guid? jobCodeId, string? jobCodeText, Guid? targetId, string? targetText)
    {
        if (jobCodeId is not null || targetId is not null)
            return jobCodeId == targetId;
        return string.Equals(
            (jobCodeText ?? string.Empty).Trim(),
            (targetText ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-points every entry of the row on <paramref name="dayLocal"/> to the target code (id and
    /// text), so the row joins any row with the same task, category and target code. Other days are
    /// untouched. A running segment keeps running; the session is re-pointed with it.
    /// </summary>
    public static MoveResult Move(
        IList<TimeEntry> entries,
        ActiveSession? active,
        DateTime dayLocal,
        string rowKey,
        Guid? targetId,
        string targetText)
    {
        var row = RowEntries(entries, dayLocal, rowKey);
        if (row.Count == 0)
            return new MoveResult { Error = "That task no longer has time on this day." };

        var text = targetText?.Trim() ?? string.Empty;
        if (row.All(e => IsSameCode(e.JobCodeId, e.JobCodeText, targetId, text)))
            return new MoveResult { Error = "That row is already on this job code." };

        foreach (var entry in row)
        {
            entry.JobCodeId = targetId;
            entry.JobCodeText = text;
        }

        var nextActive = active;
        var activeMutated = false;
        if (active is not null && row.Any(e => e.Id == active.EntryId))
        {
            nextActive = new ActiveSession
            {
                EntryId = active.EntryId,
                TaskName = active.TaskName,
                Category = active.Category,
                JobCodeId = targetId,
                JobCodeText = text,
                StartedUtc = active.StartedUtc
            };
            activeMutated = true;
        }

        return new MoveResult
        {
            Applied = true,
            Moved = row.Count,
            NewKey = TaskTypeSummary.KeyOf(row[0]),
            ActiveSession = nextActive,
            ActiveMutated = activeMutated
        };
    }

    public static string OverRemovalMessage(TimeSpan removable)
    {
        var wholeMinutes = (int)Math.Floor(removable.TotalMinutes);
        if (wholeMinutes < 1)
            return "This row has no finished time to remove. The running timer's time cannot be removed.";

        return $"You can remove at most {DayTotals.FormatCompact(removable)} from this row " +
               $"({wholeMinutes / 60} h {wholeMinutes % 60} min in whole minutes). " +
               "The running timer's time cannot be removed.";
    }

    private static void TrimManual(TimeEntry entry, TimeSpan take)
    {
        var minutes = TimeSpan.FromMinutes(entry.ManualMinutes);
        var keep = minutes - take;
        if (take.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            entry.ManualMinutes = (int)keep.TotalMinutes;
            if (entry.EndUtc is DateTime end && end - keep > entry.StartUtc)
                entry.StartUtc = end - keep;
            return;
        }

        // Part of a minute cannot live in ManualMinutes, so keep the exact rest as a clock span.
        var stop = entry.EndUtc ?? entry.StartUtc + minutes;
        entry.ManualMinutes = 0;
        entry.EndUtc = stop;
        entry.StartUtc = stop - keep;
    }
}

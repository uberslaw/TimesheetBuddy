using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Splits a running timer around a screen-lock interval so those minutes are counted once.
/// Keeping them on the current code leaves the open entry untouched.
/// </summary>
public static class LockIntervalSplit
{
    public const string BreakTaskName = "Break";
    public const string BreakCategory = "Break";

    public enum Choice
    {
        KeepOnCurrent,
        AssignToJobCode,
        Break
    }

    public sealed class Snapshot
    {
        public Guid EntryId { get; init; }
        public DateTime LockedAtUtc { get; init; }
        public string TaskName { get; init; } = string.Empty;
        public string Category { get; init; } = "General";
        public Guid? JobCodeId { get; init; }
        public string JobCodeText { get; init; } = string.Empty;
        public string Ticket { get; init; } = string.Empty;

        public static Snapshot FromSession(ActiveSession session, DateTime lockedAtUtc) => new()
        {
            EntryId = session.EntryId,
            LockedAtUtc = lockedAtUtc,
            TaskName = session.TaskName,
            Category = string.IsNullOrWhiteSpace(session.Category) ? "General" : session.Category,
            JobCodeId = session.JobCodeId,
            JobCodeText = session.JobCodeText ?? string.Empty,
            Ticket = session.Ticket ?? string.Empty
        };
    }

    public sealed class Result
    {
        public bool Applied { get; init; }
        public bool Mutated { get; init; }
        public ActiveSession? ContinuedSession { get; init; }
    }

    public static bool MeetsMinimumLock(TimeSpan lockedFor) =>
        lockedFor >= TimeSpan.FromSeconds(30);

    public static int ReportedMinutes(TimeSpan lockedFor)
    {
        var rounded = (int)Math.Round(lockedFor.TotalMinutes, MidpointRounding.AwayFromZero);
        return Math.Max(1, rounded);
    }

    public static string FormatLockClock(TimeSpan lockedFor)
    {
        if (lockedFor < TimeSpan.Zero)
            lockedFor = TimeSpan.Zero;
        return $"{(int)lockedFor.TotalHours:00}:{lockedFor.Minutes:00}:{lockedFor.Seconds:00}";
    }

    public static string PromptMessage(TimeSpan lockedFor)
    {
        var minutes = ReportedMinutes(lockedFor);
        var span = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        return $"The screen was locked for {span}, how would you like to assign the time?";
    }

    /// <summary>Last-active row label: job code, category, task name.</summary>
    public static string FormatActiveLabel(Snapshot snapshot)
    {
        var code = JobCode.FormatLabelFromStored(snapshot.JobCodeText);
        if (code == "—")
            code = "No job code";
        var category = string.IsNullOrWhiteSpace(snapshot.Category) ? "General" : snapshot.Category.Trim();
        var task = string.IsNullOrWhiteSpace(snapshot.TaskName) ? "Untitled task" : snapshot.TaskName.Trim();
        task = TicketRules.Label(snapshot.Ticket, task);
        return $"{code}, {category}, {task}";
    }

    /// <summary>Legacy wording helper kept for older call sites/tests.</summary>
    public static string PromptMessage(int minutes, string currentJobCode)
    {
        var span = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        var code = string.IsNullOrWhiteSpace(currentJobCode) ? "the current task" : currentJobCode.Trim();
        return $"The screen was locked for {span}, assign those minutes to {code}, to another code, or add to \"break\" time?";
    }

    public sealed class AssignmentSlice
    {
        public string TaskName { get; init; } = string.Empty;
        public string Category { get; init; } = "General";
        public Guid? JobCodeId { get; init; }
        public string JobCodeText { get; init; } = string.Empty;
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Places explicit slices inside the lock window. Empty slices keep all time on the
    /// last active code. Leftover lock time after the slices also stays on that code.
    /// </summary>
    public static Result ApplyAssignments(
        IList<TimeEntry> entries,
        ActiveSession? active,
        Snapshot snapshot,
        DateTime unlockedAtUtc,
        IReadOnlyList<AssignmentSlice> slices)
    {
        var usable = slices
            .Where(s => s.Duration > TimeSpan.Zero && !string.IsNullOrWhiteSpace(s.TaskName))
            .ToList();
        if (usable.Count == 0)
            return Apply(entries, active, snapshot, unlockedAtUtc, Choice.KeepOnCurrent, null);

        var original = entries.FirstOrDefault(entry => entry.Id == snapshot.EntryId);
        if (!CanSplit(active, original, snapshot))
            return new Result();

        if (unlockedAtUtc < snapshot.LockedAtUtc)
            return new Result();

        var lockStart = snapshot.LockedAtUtc < original!.StartUtc
            ? original.StartUtc
            : snapshot.LockedAtUtc;
        var lockEnd = unlockedAtUtc < lockStart ? lockStart : unlockedAtUtc;
        if (lockEnd <= lockStart)
            return new Result { Applied = true };

        original.EndUtc = lockStart;
        var cursor = lockStart;
        var remaining = lockEnd - lockStart;

        foreach (var slice in usable)
        {
            if (remaining <= TimeSpan.Zero) break;
            var take = slice.Duration <= remaining ? slice.Duration : remaining;
            if (take <= TimeSpan.Zero) continue;

            entries.Add(new TimeEntry
            {
                StartUtc = cursor,
                EndUtc = cursor + take,
                TaskName = slice.TaskName.Trim(),
                Category = string.IsNullOrWhiteSpace(slice.Category) ? "General" : slice.Category.Trim(),
                JobCodeId = slice.JobCodeId,
                JobCodeText = slice.JobCodeText?.Trim() ?? string.Empty,
                ManualMinutes = Math.Max(1, (int)Math.Round(take.TotalMinutes)),
                Source = "lock"
            });
            cursor += take;
            remaining -= take;
        }

        if (remaining > TimeSpan.Zero)
        {
            entries.Add(new TimeEntry
            {
                StartUtc = cursor,
                EndUtc = lockEnd,
                TaskName = original.TaskName,
                Category = original.Category,
                JobCodeId = original.JobCodeId,
                JobCodeText = original.JobCodeText,
                Ticket = original.Ticket,
                Source = string.IsNullOrWhiteSpace(original.Source) ? "timer" : original.Source
            });
            cursor = lockEnd;
        }

        var continued = new TimeEntry
        {
            StartUtc = lockEnd,
            EndUtc = null,
            TaskName = original.TaskName,
            Category = original.Category,
            JobCodeId = original.JobCodeId,
            JobCodeText = original.JobCodeText,
            Ticket = original.Ticket,
            Source = string.IsNullOrWhiteSpace(original.Source) ? "timer" : original.Source
        };

        if (original.EndUtc <= original.StartUtc)
            entries.Remove(original);

        entries.Add(continued);

        return new Result
        {
            Applied = true,
            Mutated = true,
            ContinuedSession = new ActiveSession
            {
                EntryId = continued.Id,
                TaskName = continued.TaskName,
                Category = continued.Category,
                JobCodeId = continued.JobCodeId,
                JobCodeText = continued.JobCodeText ?? string.Empty,
                Ticket = continued.Ticket ?? string.Empty,
                StartedUtc = continued.StartUtc
            }
        };
    }

    public static Result Apply(
        IList<TimeEntry> entries,
        ActiveSession? active,
        Snapshot snapshot,
        DateTime unlockedAtUtc,
        Choice choice,
        JobCode? target)
    {
        var original = entries.FirstOrDefault(entry => entry.Id == snapshot.EntryId);
        if (!CanSplit(active, original, snapshot))
            return new Result();

        if (choice == Choice.KeepOnCurrent)
            return new Result { Applied = true };

        if (choice == Choice.AssignToJobCode && target is null)
            return new Result();

        if (unlockedAtUtc < snapshot.LockedAtUtc)
            return new Result();

        var lockStart = snapshot.LockedAtUtc < original!.StartUtc
            ? original.StartUtc
            : snapshot.LockedAtUtc;
        var lockEnd = unlockedAtUtc < lockStart ? lockStart : unlockedAtUtc;
        if (lockEnd <= lockStart)
            return new Result { Applied = true };

        original.EndUtc = lockStart;

        var lockEntry = choice == Choice.Break
            ? BreakEntry(lockStart, lockEnd)
            : AssignedEntry(original, target!, lockStart, lockEnd);

        var continued = new TimeEntry
        {
            StartUtc = lockEnd,
            EndUtc = null,
            TaskName = original.TaskName,
            Category = original.Category,
            JobCodeId = original.JobCodeId,
            JobCodeText = original.JobCodeText,
            Ticket = original.Ticket,
            Source = string.IsNullOrWhiteSpace(original.Source) ? "timer" : original.Source
        };

        if (original.EndUtc <= original.StartUtc)
            entries.Remove(original);

        entries.Add(lockEntry);
        entries.Add(continued);

        return new Result
        {
            Applied = true,
            Mutated = true,
            ContinuedSession = new ActiveSession
            {
                EntryId = continued.Id,
                TaskName = continued.TaskName,
                Category = continued.Category,
                JobCodeId = continued.JobCodeId,
                JobCodeText = continued.JobCodeText ?? string.Empty,
                Ticket = continued.Ticket ?? string.Empty,
                StartedUtc = continued.StartUtc
            }
        };
    }

    private static bool CanSplit(ActiveSession? active, TimeEntry? original, Snapshot snapshot)
    {
        if (active is null || active.EntryId != snapshot.EntryId)
            return false;
        if (original is null || original.EndUtc is not null || original.ManualMinutes > 0)
            return false;
        return true;
    }

    private static TimeEntry BreakEntry(DateTime lockStart, DateTime lockEnd) => new()
    {
        StartUtc = lockStart,
        EndUtc = lockEnd,
        TaskName = BreakTaskName,
        Category = BreakCategory,
        JobCodeId = null,
        JobCodeText = string.Empty,
        Source = "timer"
    };

    private static TimeEntry AssignedEntry(TimeEntry original, JobCode target, DateTime lockStart, DateTime lockEnd) =>
        new()
        {
            StartUtc = lockStart,
            EndUtc = lockEnd,
            TaskName = TaskNameFor(target),
            Category = string.IsNullOrWhiteSpace(original.Category) ? "General" : original.Category,
            JobCodeId = target.Id,
            JobCodeText = target.Display,
            Source = "timer"
        };

    private static string TaskNameFor(JobCode code)
    {
        if (!string.IsNullOrWhiteSpace(code.Description))
            return code.Description.Trim();
        return string.IsNullOrWhiteSpace(code.Code) ? "Untitled task" : code.Code.Trim();
    }
}

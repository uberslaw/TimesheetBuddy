using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Moves every segment of a task-type row onto other tasks, preserving each
/// portion's start/end times, then removes the source row by identity change.
/// </summary>
public static class TaskTypeReassign
{
    public sealed class Assignment
    {
        public Guid EntryId { get; init; }
        public string TaskName { get; init; } = string.Empty;
        public string Category { get; init; } = TaskTypeSummary.DefaultCategory;
        public Guid? JobCodeId { get; init; }
        public string JobCodeText { get; init; } = string.Empty;
    }

    public sealed class Result
    {
        public bool Applied { get; init; }
        public string? Error { get; init; }
        public ActiveSession? ActiveSession { get; init; }
        public bool ActiveMutated { get; init; }
    }

    public static Result Apply(
        IList<TimeEntry> entries,
        ActiveSession? active,
        DateTime viewingDay,
        string sourceKey,
        IReadOnlyList<Assignment> assignments)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
            return Fail("No task selected.");

        if (assignments is null || assignments.Count == 0)
            return Fail("Assign every time portion to another task.");

        var sourceEntries = entries
            .Where(e => !e.IsDeleted &&
                        TaskTypeSummary.IsOnDay(e, viewingDay) &&
                        TaskTypeSummary.KeyOf(e) == sourceKey)
            .ToList();

        if (sourceEntries.Count == 0)
            return Fail("That task no longer has time on this day.");

        if (assignments.Count != sourceEntries.Count)
            return Fail("Assign every time portion before deleting the task.");

        var byId = sourceEntries.ToDictionary(e => e.Id);
        if (assignments.Select(a => a.EntryId).Distinct().Count() != assignments.Count)
            return Fail("Each time portion can only be assigned once.");

        foreach (var assignment in assignments)
        {
            if (!byId.ContainsKey(assignment.EntryId))
                return Fail("A time portion is missing or already moved.");

            var name = assignment.TaskName?.Trim() ?? string.Empty;
            if (name.Length == 0)
                return Fail("Every portion needs a destination task name.");

            var category = string.IsNullOrWhiteSpace(assignment.Category)
                ? TaskTypeSummary.DefaultCategory
                : assignment.Category.Trim();
            var destKey = TaskTypeSummary.KeyOf(
                name,
                category,
                assignment.JobCodeId,
                assignment.JobCodeText);
            if (destKey == sourceKey)
                return Fail("Choose a different task for each portion — the source task is being deleted.");
        }

        ActiveSession? nextActive = active;
        var activeMutated = false;

        foreach (var assignment in assignments)
        {
            var entry = byId[assignment.EntryId];
            var name = assignment.TaskName.Trim();
            var category = string.IsNullOrWhiteSpace(assignment.Category)
                ? TaskTypeSummary.DefaultCategory
                : assignment.Category.Trim();
            var codeText = assignment.JobCodeText?.Trim() ?? string.Empty;

            entry.TaskName = name;
            entry.Category = category;
            entry.JobCodeId = assignment.JobCodeId;
            entry.JobCodeText = codeText;

            if (nextActive is not null && nextActive.EntryId == entry.Id)
            {
                nextActive = new ActiveSession
                {
                    EntryId = entry.Id,
                    TaskName = name,
                    Category = category,
                    JobCodeId = assignment.JobCodeId,
                    JobCodeText = codeText,
                    StartedUtc = nextActive.StartedUtc
                };
                activeMutated = true;
            }
        }

        return new Result
        {
            Applied = true,
            ActiveSession = nextActive,
            ActiveMutated = activeMutated
        };
    }

    private static Result Fail(string error) => new() { Error = error };
}

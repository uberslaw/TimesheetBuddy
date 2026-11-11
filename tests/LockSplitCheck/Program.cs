using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (condition)
    {
        Console.WriteLine("PASS  " + message);
        return;
    }

    failures.Add(message);
    Console.WriteLine("FAIL  " + message);
}

var dev = new JobCode { Code = "DEV", Description = "Software development" };
var other = new JobCode { Code = "MTG", Description = "Meetings" };
var start = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
var lockedAt = start.AddMinutes(40);
var unlockedAt = start.AddMinutes(55);
var now = start.AddMinutes(60);

TimeEntry OpenEntry() => new()
{
    StartUtc = start,
    TaskName = "Feature work",
    Category = "Development",
    JobCodeId = dev.Id,
    JobCodeText = dev.Display,
    Source = "timer"
};

ActiveSession SessionFor(TimeEntry entry) => new()
{
    EntryId = entry.Id,
    TaskName = entry.TaskName,
    Category = entry.Category,
    JobCodeId = entry.JobCodeId,
    JobCodeText = entry.JobCodeText,
    StartedUtc = entry.StartUtc
};

List<TimeEntry> WithManual(TimeEntry open)
{
    var manual = new TimeEntry
    {
        StartUtc = start.AddHours(-2),
        EndUtc = start.AddHours(-2).AddMinutes(30),
        TaskName = "Earlier",
        Category = "Development",
        JobCodeId = dev.Id,
        JobCodeText = dev.Display,
        ManualMinutes = 30,
        Source = "manual"
    };
    return [manual, open];
}

TimeSpan Day(IEnumerable<TimeEntry> entries) => DayTotals.ForDay(entries, now);
TimeSpan Code(IEnumerable<TimeEntry> entries, Guid id) => DayTotals.ForJobCode(entries, id, now);

// Prompt wording and the one-minute floor.
Check(!LockIntervalSplit.MeetsMinimumLock(TimeSpan.FromSeconds(59)), "59 seconds does not prompt");
Check(LockIntervalSplit.MeetsMinimumLock(TimeSpan.FromMinutes(1)), "exactly one minute prompts");
Check(LockIntervalSplit.ReportedMinutes(TimeSpan.FromMinutes(1)) == 1, "one minute is reported as 1");
Check(LockIntervalSplit.ReportedMinutes(TimeSpan.FromSeconds(90)) == 2, "90 seconds reports as 2 minutes");
Check(
    LockIntervalSplit.PromptMessage(12, "DEV — Software development") ==
    "The screen was locked for 12 minutes, assign those minutes to DEV — Software development, to another code, or add to \"break\" time?",
    "prompt names the current code and the three choices");
Check(
    LockIntervalSplit.PromptMessage(1, "DEV") ==
    "The screen was locked for 1 minute, assign those minutes to DEV, to another code, or add to \"break\" time?",
    "a single minute stays singular");

// Keep leaves the running entry intact, lock minutes included.
var keptEntry = OpenEntry();
var kept = new List<TimeEntry> { keptEntry };
var keptSession = SessionFor(keptEntry);
var keptResult = LockIntervalSplit.Apply(
    kept, keptSession, LockIntervalSplit.Snapshot.FromSession(keptSession, lockedAt),
    unlockedAt, LockIntervalSplit.Choice.KeepOnCurrent, null);
Check(keptResult.Applied && !keptResult.Mutated && keptResult.ContinuedSession is null, "keep confirms without editing");
Check(kept.Count == 1 && keptEntry.EndUtc is null && keptEntry.Id == keptSession.EntryId, "keep leaves the open entry");
Check(DayTotals.DurationAt(keptEntry, now) == TimeSpan.FromMinutes(60), "keep still covers the lock interval");

// Assign moves only the lock slice onto the other code.
var assignedEntry = OpenEntry();
var assigned = WithManual(assignedEntry);
var assignedSession = SessionFor(assignedEntry);
var assignedResult = LockIntervalSplit.Apply(
    assigned, assignedSession, LockIntervalSplit.Snapshot.FromSession(assignedSession, lockedAt),
    unlockedAt, LockIntervalSplit.Choice.AssignToJobCode, other);
Check(assignedResult.Applied && assignedResult.Mutated, "assign applies");
var continued = assigned.Single(entry => entry.Id == assignedResult.ContinuedSession!.EntryId);
var lockSlice = assigned.Single(entry => entry.JobCodeId == other.Id);
Check(assignedEntry.EndUtc == lockedAt, "time before the lock stays on the original entry");
Check(DayTotals.DurationAt(assignedEntry, now) == TimeSpan.FromMinutes(40), "original slice is 40 minutes");
Check(lockSlice.StartUtc == lockedAt && lockSlice.EndUtc == unlockedAt, "lock slice is the locked interval");
Check(lockSlice.TaskName == "Meetings" && lockSlice.JobCodeText == other.Display, "lock slice uses the chosen code");
Check(continued.EndUtc is null && continued.StartUtc == unlockedAt, "timer continues from unlock");
Check(continued.JobCodeId == dev.Id && continued.TaskName == "Feature work", "continued entry stays on the original code");
Check(assignedResult.ContinuedSession!.EntryId != assignedEntry.Id, "session no longer points at the closed entry");
Check(continued.EndUtc is null, "the session entry is still open");
Check(Code(assigned, dev.Id) == TimeSpan.FromMinutes(75), "original code is pre-lock plus live time plus the earlier manual entry");
Check(Code(assigned, other.Id) == TimeSpan.FromMinutes(15), "the other code has only the lock interval");
Check(Day(assigned) == TimeSpan.FromMinutes(90), "day total is the original hour plus the manual 30, with no double count");

// Break is its own finished entry and still counts in the day, not on the job code.
var breakEntry = OpenEntry();
var broken = new List<TimeEntry> { breakEntry };
var breakSession = SessionFor(breakEntry);
var breakResult = LockIntervalSplit.Apply(
    broken, breakSession, LockIntervalSplit.Snapshot.FromSession(breakSession, lockedAt),
    unlockedAt, LockIntervalSplit.Choice.Break, null);
var breakSlice = broken.Single(entry => entry.TaskName == "Break");
Check(breakResult.Applied, "break applies");
Check(breakSlice.Category == "Break" && breakSlice.JobCodeId is null && breakSlice.JobCodeText == string.Empty, "break is not a job code");
Check(breakSlice.EndUtc == unlockedAt && breakSlice.StartUtc == lockedAt, "break covers the lock interval");
Check(Code(broken, dev.Id) == TimeSpan.FromMinutes(45), "break minutes are not also on the original code");
Check(Day(broken) == TimeSpan.FromMinutes(60), "break still counts in today's total");
Check(breakResult.ContinuedSession!.JobCodeId == dev.Id, "after break the timer continues on the original code");

// A lock that starts as the timer starts does not leave a zero-length original entry.
var immediate = OpenEntry();
immediate.StartUtc = lockedAt;
var immediateSession = SessionFor(immediate);
var immediateEntries = new List<TimeEntry> { immediate };
var immediateResult = LockIntervalSplit.Apply(
    immediateEntries, immediateSession, LockIntervalSplit.Snapshot.FromSession(immediateSession, lockedAt),
    unlockedAt, LockIntervalSplit.Choice.Break, null);
Check(immediateResult.Applied && !immediateEntries.Contains(immediate), "empty pre-lock slice is not stored");
Check(immediateResult.ContinuedSession is not null &&
      immediateEntries.Any(entry => entry.Id == immediateResult.ContinuedSession.EntryId && entry.EndUtc is null),
    "session points at the entry that is still running");
Check(DayTotals.ForDay(immediateEntries, now) == TimeSpan.FromMinutes(20), "immediate lock still sums to lock plus time since unlock");

// Already closed, or a different session: do not mutate and do not retarget the session.
var closed = OpenEntry();
closed.EndUtc = unlockedAt;
var closedSession = SessionFor(closed);
var closedEntries = new List<TimeEntry> { closed };
var closedResult = LockIntervalSplit.Apply(
    closedEntries, closedSession, LockIntervalSplit.Snapshot.FromSession(closedSession, lockedAt),
    unlockedAt, LockIntervalSplit.Choice.AssignToJobCode, other);
Check(!closedResult.Applied && closedEntries.Count == 1 && closed.EndUtc == unlockedAt, "a finished entry is left alone");

var switched = OpenEntry();
var switchedEntries = new List<TimeEntry> { switched };
var switchedSession = SessionFor(switched);
switchedSession.EntryId = Guid.NewGuid();
var switchedResult = LockIntervalSplit.Apply(
    switchedEntries, switchedSession, LockIntervalSplit.Snapshot.FromSession(SessionFor(switched), lockedAt),
    unlockedAt, LockIntervalSplit.Choice.Break, null);
Check(!switchedResult.Applied && switched.EndUtc is null && switchedEntries.Count == 1, "a switched session is not split");

var manualRunning = OpenEntry();
manualRunning.ManualMinutes = 60;
var manualEntries = new List<TimeEntry> { manualRunning };
var manualSession = SessionFor(manualRunning);
var manualResult = LockIntervalSplit.Apply(
    manualEntries, manualSession, LockIntervalSplit.Snapshot.FromSession(manualSession, lockedAt),
    unlockedAt, LockIntervalSplit.Choice.Break, null);
Check(!manualResult.Applied && manualEntries.Count == 1, "manual minutes are not split by the clock");

// Tracker: prompt only when the same timer was running and the lock lasted at least a minute.
var tracker = new ScreenLockTracker();
var running = SessionFor(OpenEntry());
tracker.NoteLock(null, lockedAt);
Check(!tracker.TryTakeUnlock(running, unlockedAt, out _, out _), "a lock while idle does not prompt");

tracker.NoteLock(running, lockedAt);
Check(!tracker.TryTakeUnlock(running, lockedAt.AddSeconds(59), out _, out _), "tracker ignores a lock under one minute");

tracker.NoteLock(running, lockedAt);
Check(!tracker.TryTakeUnlock(null, unlockedAt, out _, out _), "tracker ignores unlock after the timer stopped");

tracker.NoteLock(running, lockedAt);
var replaced = SessionFor(OpenEntry());
Check(!tracker.TryTakeUnlock(replaced, unlockedAt, out _, out _), "tracker ignores unlock after the entry changed");

tracker.NoteLock(running, lockedAt);
tracker.NoteLock(running, lockedAt.AddMinutes(5));
Check(tracker.TryTakeUnlock(running, lockedAt.AddMinutes(2), out var snap, out var lockedFor), "a one-minute lock prompts");
Check(snap!.LockedAtUtc == lockedAt && lockedFor == TimeSpan.FromMinutes(2), "the first lock time is kept");
Check(!tracker.TryTakeUnlock(running, unlockedAt, out _, out _), "the pending lock is consumed once");

// Today's row total includes the live timer. The footer total includes every entry.
var live = new TimeEntry
{
    StartUtc = now.AddMinutes(-10),
    JobCodeId = dev.Id,
    JobCodeText = dev.Display,
    TaskName = "Feature work",
    Source = "timer"
};
var loose = new TimeEntry
{
    StartUtc = now.AddMinutes(-20),
    EndUtc = now.AddMinutes(-5),
    TaskName = "Notes",
    Category = "General",
    Source = "manual",
    ManualMinutes = 15
};
Check(DayTotals.ForJobCode([live], dev.Id, live.StartUtc) == TimeSpan.Zero, "a timer at its start contributes nothing yet");
Check(DayTotals.ForJobCode([live], dev.Id, live.StartUtc.AddSeconds(90)) == TimeSpan.FromSeconds(90), "the row total includes the live timer");
Check(DayTotals.ForDay([live, loose], now) == TimeSpan.FromMinutes(10) + TimeSpan.FromMinutes(15), "today's total includes entries with no job code");
Check(DayTotals.FormatClock(TimeSpan.FromSeconds(90)) == "00:01:30", "totals show a ticking clock");

// First open lists active codes. A saved id list is the customized order and does not invent rows.
var inactive = new JobCode { Code = "OLD", Description = "Retired", IsActive = false };
var extra = new JobCode { Code = "SUP", Description = "Support" };
var codes = new List<JobCode> { dev, inactive, extra };
var firstOpen = JobCodeViewList.Resolve(codes, null);
Check(firstOpen.Count == 2 && firstOpen[0].Id == dev.Id && firstOpen[1].Id == extra.Id, "first open shows active codes");
var custom = JobCodeViewList.Resolve(codes, [extra.Id, inactive.Id, Guid.NewGuid(), extra.Id]);
Check(custom.Count == 2 && custom[0].Id == extra.Id && custom[1].Id == inactive.Id, "saved ids keep order and skip unknown codes");
Check(JobCodeViewList.Resolve(codes, []).Count == 0, "removing every row leaves an empty view");

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} check(s) failed.");
    return 1;
}

Console.WriteLine("All lock and job-code view checks passed.");
return 0;

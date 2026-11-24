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

var folder = Path.Combine(Path.GetTempPath(), "TimesheetBuddyNoTaskCheck-" + Guid.NewGuid().ToString("N"));
AppState NewState() => new(new SettingsStore(folder));
static DateTime Utc(int h, int m, int s = 0) => new(2026, 11, 18, h, m, s, DateTimeKind.Utc);
static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd) => aStart < bEnd && bStart < aEnd;

try
{
    // Launch: nothing runs.
    var state = NewState();
    Check(!state.IsNoTaskRunning && state.Settings.UnassignedSegments.Count == 0, "launch does not start No Task Selected");

    // A user stop starts No Task Selected at the stop moment.
    state.StartOrSwitch("Feature work", "Development", null, "DEV");
    var first = state.Settings.Entries.Single();
    state.StopActive(save: true);
    Check(state.IsNoTaskRunning, "stop starts No Task Selected");
    Check(state.NoTaskStartedUtc == first.EndUtc, "No Task Selected starts when the task ended");
    Check(state.StatusText == NoTaskTime.Label, "status reads No Task Selected");
    state.Tick();
    Check(state.ElapsedDisplay == "0", "counter shows the idle elapsed time");

    // Start under a minute later: idle ends and is dropped.
    state.StartOrSwitch("Feature work", "Development", null, "DEV");
    Check(!state.IsNoTaskRunning, "start ends No Task Selected");
    Check(state.Settings.UnassignedSegments.Count == 0, "a run under 1 minute is discarded");

    // A run of 1 minute or more stays pending.
    state.StopActive(save: true);
    state.Settings.NoTaskStartedUtc = DateTime.UtcNow.AddMinutes(-5);
    state.Tick();
    Check(state.Elapsed >= TimeSpan.FromMinutes(5), "counter follows the running idle time");
    state.StartOrSwitch("Email", "Admin", null, "ADMIN");
    Check(state.Settings.UnassignedSegments.Count == 1, "a run of 1 minute or more is kept as pending");
    var pendingSegment = state.Settings.UnassignedSegments[0];
    Check(Math.Abs((pendingSegment.EndUtc - pendingSegment.StartUtc).TotalMinutes - 5) < 0.1, "pending keeps the run's span");
    Check(state.Settings.Entries.All(e => e.TaskName != NoTaskTime.Label), "unassigned time is not stored as an entry");

    // Closing the app finishes the task and does not start idle.
    state.StopAllForShutdown();
    Check(state.ActiveSession is null && !state.IsNoTaskRunning, "close does not start No Task Selected");
    Check(state.Settings.Entries.All(e => e.EndUtc is not null), "close finishes the running task");

    // Idle running at close is finished, saved and not resumed.
    var reopened = NewState();
    reopened.StartOrSwitch("Email", "Admin", null, "ADMIN");
    reopened.StopActive(save: true);
    reopened.Settings.NoTaskStartedUtc = DateTime.UtcNow.AddMinutes(-3);
    var before = reopened.Settings.UnassignedSegments.Count;
    reopened.StopAllForShutdown();
    Check(reopened.Settings.UnassignedSegments.Count == before + 1 && reopened.Settings.NoTaskStartedUtc is null,
        "idle running at close is closed into pending");
    var afterRestart = NewState();
    Check(!afterRestart.IsNoTaskRunning, "idle does not resume after restart");
    Check(afterRestart.Settings.UnassignedSegments.Count == before + 1, "pending idle survives a restart");

    // A start left behind by a crash is dropped on load.
    afterRestart.Settings.NoTaskStartedUtc = DateTime.UtcNow.AddHours(-2);
    afterRestart.Persist();
    var crashed = NewState();
    Check(!crashed.IsNoTaskRunning && crashed.Settings.UnassignedSegments.Count == before + 1,
        "a leftover running idle start is not resumed");
    var json = File.ReadAllText(Path.Combine(folder, "settings.json"));
    Check(!json.Contains("NaN", StringComparison.Ordinal), "settings.json has no NaN");

    // Totals, the task list, week summaries (and so exports) exclude unassigned time.
    var now = DateTime.UtcNow;
    var today = DateTime.Today;
    var dayTotal = DayTotals.ForDay(crashed.EntriesForDay(today), now);
    var byCode = DayTotals.ByJobCodeForDay(crashed.Settings.Entries, today, now);
    var rows = TaskTypeSummary.Build(crashed.Settings.Entries, today, now);
    var weeks = WeekCalendar.Collect(crashed.Settings.Entries, DateTime.Now, now);
    Check(crashed.NoTaskTotalForDay(today, now) >= TimeSpan.FromMinutes(4.9), "No Task Selected row has today's pending time");
    Check(dayTotal < TimeSpan.FromMinutes(1), "band total excludes unassigned time");
    Check(byCode.All(r => r.Label != NoTaskTime.Label) && byCode.Sum(r => r.Total.TotalSeconds) < 60, "job-code totals exclude unassigned time");
    Check(rows.All(r => r.TaskName != NoTaskTime.Label), "Today by task has no No Task Selected row");
    Check(weeks.Sum(w => w.Total.TotalSeconds) < 60 &&
          weeks.SelectMany(w => w.Days).SelectMany(d => d.Tasks).All(t => t.TaskName != NoTaskTime.Label),
        "week summaries and exports exclude unassigned time");
    Check(DayTotals.TimeRemaining(dayTotal) > TimeSpan.FromHours(7.4), "time remaining ignores unassigned time");

    // Catch-up: pending and running idle count as known, so nothing is asked twice.
    var catchUp = NewState();
    catchUp.Settings.Entries.Clear();
    catchUp.Settings.UnassignedSegments.Clear();
    catchUp.StartOrSwitch("Email", "Admin", null, "ADMIN");
    catchUp.StopActive(save: true);
    catchUp.Settings.Entries.Clear();
    catchUp.Settings.NoTaskStartedUtc = DateTime.UtcNow.AddMinutes(-(catchUp.CatchUpIntervalMinutes + 5));
    Check(catchUp.MeasureCatchUpWindow().UnaccountedMinutes == 0, "running idle is not unaccounted time");
    Check(NoTaskTime.ShouldPrompt(catchUp.OpenNoTaskTime()), "running idle of a minute or more prompts");
    catchUp.Settings.NoTaskStartedUtc = DateTime.UtcNow.AddSeconds(-40);
    Check(!NoTaskTime.ShouldPrompt(catchUp.OpenNoTaskTime()), "running idle under a minute does not prompt");

    // Pure placement: inside the idle spans, latest first, never over logged time.
    var pending = new List<UnassignedSegment>
    {
        new() { StartUtc = Utc(9, 0), EndUtc = Utc(9, 10) },
        new() { StartUtc = Utc(9, 20), EndUtc = Utc(9, 30) }
    };
    var logged = new List<TimeEntry>
    {
        new() { StartUtc = Utc(9, 25), EndUtc = Utc(9, 27), TaskName = "Added time", ManualMinutes = 2 }
    };
    var open = NoTaskTime.Open(pending, null, logged, Utc(10, 0));
    Check(NoTaskTime.WholeMinutes(NoTaskTime.Total(open)) == 18, "logged time inside an idle span is not offered again");
    CatchUpSlice[] slices =
    [
        new() { TaskName = "A", Category = "General", Minutes = 6 },
        new() { TaskName = "B", Category = "General", Minutes = 4 }
    ];
    Check(NoTaskTime.Validate(slices, open) is null, "10 of 18 minutes is valid");
    var placed = NoTaskTime.Place(slices, open);
    Check(placed.All(p => open.Any(s => p.StartUtc >= s.StartUtc && p.EndUtc <= s.EndUtc)), "assigned minutes land inside idle spans");
    var allBlocks = placed.Concat(logged).ToList();
    var overlap = false;
    for (var i = 0; i < allBlocks.Count; i++)
        for (var j = i + 1; j < allBlocks.Count; j++)
            overlap |= Overlaps(allBlocks[i].StartUtc, allBlocks[i].EndUtc!.Value, allBlocks[j].StartUtc, allBlocks[j].EndUtc!.Value);
    Check(!overlap, "assigned minutes do not overlap each other or existing entries");
    Check(placed.Where(p => p.TaskName == "A").Sum(p => p.ManualMinutes) == 6 &&
          placed.Where(p => p.TaskName == "B").Sum(p => p.ManualMinutes) == 4, "each task gets its minutes");
    Check(placed.Where(p => p.TaskName == "A").Max(p => p.EndUtc) == Utc(9, 30), "first slice sits closest to now");
    Check(placed.All(p => p.Source == NoTaskTime.AssignedSource), "assigned entries are marked");
    var left = NoTaskTime.Remaining(pending, logged.Concat(placed), Utc(10, 0));
    Check(left.Count == 1 && left[0].StartUtc == Utc(9, 0) && left[0].EndUtc == Utc(9, 8), "partial assignment leaves the earliest 8 minutes pending");
    Check(NoTaskTime.Validate([new CatchUpSlice { TaskName = "A", Minutes = 19 }], open) is not null, "more than pending is rejected");
    Check(NoTaskTime.Validate([new CatchUpSlice { TaskName = "A", Minutes = 0 }], open) is not null, "zero minutes is rejected");

    var withSeconds = new List<UnassignedSegment> { new() { StartUtc = Utc(9, 0), EndUtc = Utc(9, 5, 40) } };
    var openSeconds = NoTaskTime.Open(withSeconds, null, [], Utc(10, 0));
    var allPlaced = NoTaskTime.Place([new CatchUpSlice { TaskName = "A", Minutes = 5 }], openSeconds);
    Check(NoTaskTime.Remaining(withSeconds, allPlaced, Utc(10, 0)).Count == 0, "assigning every whole minute clears the seconds left over");

    // AppState: partial assignment, rejection, and a running idle timer.
    var assign = NewState();
    assign.Settings.Entries.Clear();
    assign.Settings.UnassignedSegments.Clear();
    var nowUtc = DateTime.UtcNow;
    assign.Settings.UnassignedSegments.Add(new UnassignedSegment { StartUtc = nowUtc.AddMinutes(-60), EndUtc = nowUtc.AddMinutes(-40) });
    var error = assign.AssignNoTaskTime([new CatchUpSlice { TaskName = "A", Minutes = 21 }]);
    Check(error is not null && assign.Settings.Entries.Count == 0 && assign.Settings.UnassignedSegments.Count == 1,
        "an over-assignment saves nothing");
    error = assign.AssignNoTaskTime(
    [
        new CatchUpSlice { TaskName = "A", Category = "General", Minutes = 5 },
        new CatchUpSlice { TaskName = LockIntervalSplit.BreakTaskName, Category = LockIntervalSplit.BreakCategory, Minutes = 3 }
    ]);
    Check(error is null, "a valid split is saved");
    Check(NoTaskTime.WholeMinutes(NoTaskTime.Total(assign.OpenNoTaskTime())) == 12, "the rest stays pending");
    var breakEntry = assign.Settings.Entries.Single(e => e.TaskName == "Break");
    Check(breakEntry.Category == "Break" && breakEntry.JobCodeId is null && breakEntry.JobCodeText.Length == 0, "Break has no job code");
    Check(DayTotals.ForDay(assign.Settings.Entries, DateTime.UtcNow) == TimeSpan.FromMinutes(8), "assigned minutes now count as logged");

    assign.Settings.UnassignedSegments.Clear();
    assign.StartOrSwitch("Email", "Admin", null, "ADMIN");
    assign.StopActive(save: true);
    assign.Settings.NoTaskStartedUtc = DateTime.UtcNow.AddMinutes(-10);
    error = assign.AssignNoTaskTime([new CatchUpSlice { TaskName = "A", Minutes = 4 }]);
    Check(error is null, "running idle can be assigned");
    Check(assign.IsNoTaskRunning && DateTime.UtcNow - assign.NoTaskStartedUtc!.Value < TimeSpan.FromSeconds(5),
        "idle restarts from now after assigning");
    Check(NoTaskTime.WholeMinutes(NoTaskTime.Total(assign.OpenNoTaskTime())) == 5, "the unassigned rest of the closed run stays pending");

    // Today-by-task No Task Selected row (pure): Move keeps the original clock times, never twice.
    var rowDay = Utc(9, 30).ToLocalTime().Date;
    var rowTarget = new NoTaskTime.MoveTarget("Review", "Development", null, "DEV");
    var rowNow = Utc(9, 50, 30);
    var rowSpans = NoTaskTime.ForDay(pending, Utc(9, 50), logged, rowDay, rowNow);
    Check(NoTaskTime.Total(rowSpans) == TimeSpan.FromSeconds(18 * 60 + 30) && rowSpans.Count == 4,
        "the row shows pending minus logged time plus the running run from its first second");
    var moved = NoTaskTime.MoveDay(pending, Utc(9, 50), logged, rowDay, rowNow, rowTarget);
    Check(moved.Applied && moved.Added.Count == 4 && moved.Amount == NoTaskTime.Total(rowSpans), "Move logs every open span");
    Check(moved.Added.Select(a => (a.StartUtc, a.EndUtc!.Value)).SequenceEqual(rowSpans.Select(s => (s.StartUtc, s.EndUtc))),
        "moved entries keep the spans' own start and end times");
    Check(moved.Added.All(a => a.TaskName == "Review" && a.Category == "Development" && a.JobCodeText == "DEV" &&
                               a.Source == NoTaskTime.AssignedSource && a.ManualMinutes == 0),
        "moved entries carry the target task and are marked as assigned idle time");
    Check(moved.Pending.Count == 0 && moved.RunningStartUtc == rowNow, "nothing stays pending and the running run restarts from now");
    var afterMove = logged.Concat(moved.Added).ToList();
    var reopen = NoTaskTime.Open(moved.Pending, moved.RunningStartUtc, afterMove, rowNow);
    Check(NoTaskTime.Total(reopen) == TimeSpan.Zero && NoTaskTime.Validate([new CatchUpSlice { TaskName = "A", Minutes = 1 }], reopen) is not null,
        "moved time is not offered to the interval prompt again");
    var overlapAfterMove = false;
    for (var i = 0; i < afterMove.Count; i++)
        for (var j = i + 1; j < afterMove.Count; j++)
            overlapAfterMove |= Overlaps(afterMove[i].StartUtc, afterMove[i].EndUtc!.Value, afterMove[j].StartUtc, afterMove[j].EndUtc!.Value);
    Check(!overlapAfterMove, "moved entries do not overlap logged time");
    Check(!NoTaskTime.MoveDay([], null, logged, rowDay, rowNow, rowTarget).Applied, "Move with no idle time on the day is refused");

    var otherDay = rowDay.AddDays(-1);
    var olderPending = new List<UnassignedSegment>
    {
        new() { StartUtc = Utc(9, 0).AddDays(-1), EndUtc = Utc(9, 30).AddDays(-1) },
        new() { StartUtc = Utc(9, 0), EndUtc = Utc(9, 10) }
    };
    var movedOlder = NoTaskTime.MoveDay(olderPending, Utc(9, 50), [], otherDay, rowNow, rowTarget);
    Check(movedOlder.Applied && movedOlder.Amount == TimeSpan.FromMinutes(30) && movedOlder.RunningStartUtc == Utc(9, 50),
        "moving an older day leaves today's running run alone");
    Check(movedOlder.Pending.Count == 1 && movedOlder.Pending[0].StartUtc == Utc(9, 0), "moving an older day keeps today's pending time");

    var midnightLocal = DateTime.SpecifyKind(rowDay, DateTimeKind.Local).ToUniversalTime();
    var acrossMidnight = new List<UnassignedSegment> { new() { StartUtc = midnightLocal.AddMinutes(-20), EndUtc = midnightLocal.AddMinutes(15) } };
    var movedAcross = NoTaskTime.MoveDay(acrossMidnight, null, [], rowDay, midnightLocal.AddHours(2), rowTarget);
    Check(movedAcross.Applied && movedAcross.Added.Single().StartUtc == midnightLocal && movedAcross.Amount == TimeSpan.FromMinutes(15),
        "a span across midnight moves only the displayed day's part");
    Check(movedAcross.Pending.Count == 1 && movedAcross.Pending[0].EndUtc == midnightLocal, "the previous day's part stays pending");

    // Discard (pure): newest first, refused when over, nothing logged.
    var discarded = NoTaskTime.DiscardFromDay(pending, null, logged, rowDay, Utc(10, 0), TimeSpan.FromMinutes(5));
    Check(discarded.Applied && discarded.Added.Count == 0, "discard logs nothing");
    var leftAfterDiscard = NoTaskTime.Open(discarded.Pending, null, logged, Utc(10, 0));
    Check(NoTaskTime.Total(leftAfterDiscard) == TimeSpan.FromMinutes(13) && leftAfterDiscard[^1].EndUtc == Utc(9, 23),
        "discarding 5 minutes takes the newest open minutes");
    Check(NoTaskTime.DiscardFromDay(pending, null, logged, rowDay, Utc(10, 0), null).Pending.Count == 0, "discard all clears the day");
    var overDiscard = NoTaskTime.DiscardFromDay(pending, null, logged, rowDay, Utc(10, 0), TimeSpan.FromMinutes(19));
    Check(!overDiscard.Applied && overDiscard.Error is not null, "discarding more than the day holds is refused");
    var discardRunning = NoTaskTime.DiscardFromDay([], Utc(9, 55), [], rowDay, Utc(10, 0), TimeSpan.FromMinutes(2));
    Check(discardRunning.Applied && discardRunning.RunningStartUtc == Utc(10, 0) &&
          discardRunning.Pending.Count == 1 && discardRunning.Pending[0].EndUtc == Utc(9, 58),
        "discarding part of a running run keeps its earlier part pending and restarts it from now");

    // AppState: Move and Discard on the row keep the band, the prompt and the store in step.
    var rowState = NewState();
    rowState.Settings.Entries.Clear();
    rowState.Settings.UnassignedSegments.Clear();
    rowState.StartOrSwitch("Email", "Admin", null, "ADMIN");
    rowState.StopActive(save: true);
    rowState.Settings.Entries.Clear();
    var rowStateNow = DateTime.UtcNow;
    rowState.Settings.UnassignedSegments.Add(new UnassignedSegment { StartUtc = rowStateNow.AddMinutes(-20), EndUtc = rowStateNow.AddMinutes(-10) });
    rowState.Settings.NoTaskStartedUtc = rowStateNow.AddMinutes(-5);
    var shown = NoTaskTime.Total(rowState.NoTaskSpansForViewingDay());
    Check(Math.Abs(shown.TotalMinutes - 15) < 0.1, "the row total is pending plus running idle");
    Check(rowState.MoveNoTaskTime(rowTarget) is null, "Move on the row is saved");
    Check(rowState.NoTaskTotalForDay(DateTime.Today, DateTime.UtcNow) < TimeSpan.FromSeconds(3), "the band's No Task Selected total drops to zero");
    Check(!NoTaskTime.ShouldPrompt(rowState.OpenNoTaskTime()) &&
          rowState.AssignNoTaskTime([new CatchUpSlice { TaskName = "A", Minutes = 1 }]) is not null,
        "the interval prompt cannot assign moved time again");
    Check(rowState.IsNoTaskRunning && DateTime.UtcNow - rowState.NoTaskStartedUtc!.Value < TimeSpan.FromSeconds(5),
        "No Task Selected keeps running from now");
    var reviewRow = TaskTypeSummary.Build(rowState.Settings.Entries, DateTime.Today, DateTime.UtcNow).Single();
    Check(reviewRow.TaskName == "Review" && reviewRow.SegmentCount == 2 && Math.Abs(reviewRow.Total.TotalMinutes - 15) < 0.1,
        "the moved time shows as a Today-by-task row with one segment per span");
    Check(Math.Abs(DayTotals.ForDay(rowState.Settings.Entries, DateTime.UtcNow).TotalMinutes - 15) < 0.1, "moved time counts in Total");
    var rowReloaded = NewState();
    Check(rowReloaded.Settings.UnassignedSegments.Count == 0 && rowReloaded.Settings.Entries.Count(e => e.TaskName == "Review") == 2,
        "the move survives a reload");

    var discardState = NewState();
    discardState.Settings.Entries.Clear();
    discardState.Settings.UnassignedSegments.Clear();
    discardState.Settings.UnassignedSegments.Add(new UnassignedSegment { StartUtc = DateTime.UtcNow.AddMinutes(-25), EndUtc = DateTime.UtcNow.AddMinutes(-5) });
    Check(discardState.AssignNoTaskTime([new CatchUpSlice { TaskName = "B", Minutes = 10 }]) is null, "the prompt assigns part first");
    var entriesBefore = discardState.Settings.Entries.Count;
    Check(discardState.DiscardNoTaskTime(TimeSpan.FromHours(2)) is not null, "discarding more than the row holds changes nothing");
    Check(discardState.DiscardNoTaskTime(TimeSpan.FromMinutes(4)) is null &&
          Math.Abs(NoTaskTime.Total(discardState.OpenNoTaskTime()).TotalMinutes - 6) < 0.1,
        "discard takes only the prompt's leftover, newest first");
    Check(discardState.DiscardNoTaskTime(null) is null && discardState.Settings.UnassignedSegments.Count == 0 &&
          discardState.Settings.Entries.Count == entriesBefore, "discard all clears pending and logs nothing");

    // Add with a ticket: ends idle, stores the ticket on the entry and session, labels show it.
    var ticketed = NewState();
    ticketed.StartOrSwitch("Email", "Admin", null, "ADMIN");
    ticketed.StopActive(save: true);
    ticketed.StartOrSwitch("Reset password", "Support", null, "03 — SERVICE-NOW", " INC0012345 ");
    var ticketEntry = ticketed.Settings.Entries.Single(e => e.Id == ticketed.ActiveSession!.EntryId);
    Check(!ticketed.IsNoTaskRunning, "Add with a ticket ends No Task Selected");
    Check(ticketEntry.Ticket == "INC0012345" && ticketed.ActiveSession!.Ticket == "INC0012345", "the ticket is stored trimmed on the entry and session");
    Check(ticketed.StatusText == "Tracking: INC0012345 · Reset password", "status shows ticket · task name");
    Check(ticketed.ActiveTaskLabel == "INC0012345 · Reset password", "active task label shows the ticket");
    ticketed.StopActive(save: true);
    Check(ticketed.ResumeLastTask() && ticketed.ActiveSession!.Ticket == "INC0012345", "resuming the last task keeps its ticket");
    var renameKey = TaskTypeSummary.KeyOf(ticketed.ActiveSession!);
    var renamedKey = ticketed.RenameTaskTypeOnViewingDay(renameKey, "Reset MFA", "Support");
    Check(renamedKey is not null && ticketed.ActiveSession!.Ticket == "INC0012345" && renamedKey == TaskTypeSummary.KeyOf(ticketed.ActiveSession),
        "renaming a ticket row keeps the ticket on the running session");
    var reloaded = NewState();
    Check(reloaded.Settings.Entries.Any(e => e.Ticket == "INC0012345"), "the ticket survives a save and reload");

    var legacyFolder = Path.Combine(folder, "legacy");
    Directory.CreateDirectory(legacyFolder);
    File.WriteAllText(Path.Combine(legacyFolder, "settings.json"), """
        {
          "entries": [
            { "id": "6f1c1f0e-1111-4c1d-9a51-111111111111", "startUtc": "2026-11-18T09:00:00Z", "endUtc": null,
              "taskName": "Old", "category": "General", "jobCodeText": "DEV", "source": "timer" }
          ],
          "activeSession": { "entryId": "6f1c1f0e-1111-4c1d-9a51-111111111111", "taskName": "Old",
            "category": "General", "jobCodeText": "DEV", "startedUtc": "2026-11-18T09:00:00Z" }
        }
        """);
    var legacy = new SettingsStore(legacyFolder).Load();
    Check(legacy.Entries.Count == 1 && legacy.Entries[0].Ticket == "" && legacy.ActiveSession?.Ticket == "",
        "settings.json without tickets loads with empty tickets");
}
finally
{
    try { Directory.Delete(folder, recursive: true); } catch { }
}

if (failures.Count == 0)
{
    Console.WriteLine("All No Task Selected checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

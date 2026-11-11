using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

var failed = 0;
var now = new DateTime(2026, 11, 11, 15, 0, 0, DateTimeKind.Utc);

void Check(bool condition, string name)
{
    if (condition)
    {
        Console.WriteLine($"  ok  {name}");
        return;
    }

    failed++;
    Console.WriteLine($"FAIL  {name}");
}

UnaccountedWindow Measure(IEnumerable<TimeEntry> entries, ActiveSession? session, int minutes, DateTime? at = null) =>
    UnaccountedTime.Measure(entries, session, minutes, at ?? now);

void CheckWindow(UnaccountedWindow window, int expectedUnaccounted, string name)
{
    Check(window.UnaccountedMinutes == expectedUnaccounted, $"{name}: unaccounted {window.UnaccountedMinutes} == {expectedUnaccounted}");
    Check(window.AccountedMinutes + window.UnaccountedMinutes == window.WindowMinutes,
        $"{name}: accounted {window.AccountedMinutes} + unaccounted {window.UnaccountedMinutes} == {window.WindowMinutes}");
}

(TimeEntry Entry, ActiveSession Session) Running(DateTime started)
{
    var entry = new TimeEntry
    {
        StartUtc = started,
        EndUtc = null,
        TaskName = "Feature work",
        Source = "timer"
    };
    var session = new ActiveSession
    {
        EntryId = entry.Id,
        StartedUtc = started,
        TaskName = entry.TaskName
    };
    return (entry, session);
}

bool Overlaps(DateTime a0, DateTime a1, DateTime b0, DateTime b1) => a0 < b1 && b0 < a1;

Console.WriteLine("prompt choice");
Check(UnaccountedTime.Decide(0, timerRunning: true) == CatchUpPromptKind.StillWorking,
    "covered + timer → still working");
Check(UnaccountedTime.Decide(0, timerRunning: false) == CatchUpPromptKind.None,
    "covered + no timer → nothing");
Check(UnaccountedTime.Decide(5, timerRunning: true) == CatchUpPromptKind.AssignTasks,
    "gap + timer → assign, not still working");
Check(UnaccountedTime.Decide(15, timerRunning: false) == CatchUpPromptKind.AssignTasks,
    "gap + no timer → assign");
Check(UnaccountedTime.AssignPromptText(5) ==
      "5 unaccounted for minutes, would you like to assign tasks to them?",
    "assign prompt wording");

Console.WriteLine("windows");

var whole = Running(now.AddMinutes(-15));
var wholeWindow = Measure([whole.Entry], whole.Session, 15);
CheckWindow(wholeWindow, 0, "timer covered all 15 minutes");
Check(UnaccountedTime.Decide(wholeWindow, timerRunning: true) == CatchUpPromptKind.StillWorking,
    "full timer chooses still working");

var ten = Running(now.AddMinutes(-10));
var tenWindow = Measure([ten.Entry], ten.Session, 15);
CheckWindow(tenWindow, 5, "timer 10 of 15");
Check(UnaccountedTime.Decide(tenWindow, timerRunning: true) == CatchUpPromptKind.AssignTasks,
    "partial timer chooses assign");
Check(tenWindow.Gaps.Count == 1, "partial timer has one gap");
Check(tenWindow.Gaps.Count == 1 && tenWindow.Gaps[0].StartUtc == now.AddMinutes(-15)
      && tenWindow.Gaps[0].EndUtc == now.AddMinutes(-10),
    "partial timer gap is the first 5 minutes");

var manual = new TimeEntry
{
    StartUtc = now.AddMinutes(-15),
    EndUtc = now,
    ManualMinutes = 15,
    TaskName = "Already logged",
    Source = "manual"
};
var manualWindow = Measure([manual], null, 15);
CheckWindow(manualWindow, 0, "manual entry covers the window");
Check(UnaccountedTime.Decide(manualWindow, timerRunning: false) == CatchUpPromptKind.None,
    "full manual entry shows nothing");

var empty = Measure([], null, 15);
CheckWindow(empty, 15, "no timer and no entries");
Check(UnaccountedTime.Decide(empty, timerRunning: false) == CatchUpPromptKind.AssignTasks,
    "empty window chooses assign");

var doubled = Measure([ten.Entry], ten.Session, 15);
CheckWindow(doubled, 5, "active entry is not counted twice");

var driftedEntry = new TimeEntry
{
    StartUtc = now.AddMinutes(-10),
    EndUtc = null,
    TaskName = "Started earlier",
    Source = "timer"
};
var earlierSession = new ActiveSession
{
    EntryId = driftedEntry.Id,
    StartedUtc = now.AddMinutes(-15),
    TaskName = driftedEntry.TaskName
};
CheckWindow(Measure([driftedEntry], earlierSession, 15), 0,
    "elapsed longer than the entry start is still accounted once");

var longTimer = Running(now.AddHours(-2));
CheckWindow(Measure([longTimer.Entry], longTimer.Session, 15), 0, "timer longer than the window");

var missingRow = new ActiveSession { EntryId = Guid.NewGuid(), StartedUtc = now.AddMinutes(-15), TaskName = "Orphan session" };
CheckWindow(Measure([], missingRow, 15), 0, "elapsed counts when the entry row is missing");

var shortSession = new ActiveSession { EntryId = Guid.NewGuid(), StartedUtc = now.AddMinutes(-10), TaskName = "Orphan session" };
CheckWindow(Measure([], shortSession, 15), 5, "10 minutes of elapsed with no row");

var manualPartial = new TimeEntry
{
    StartUtc = now.AddMinutes(-5),
    EndUtc = now,
    ManualMinutes = 100,
    TaskName = "Short interval, large manual minutes",
    Source = "manual"
};
CheckWindow(Measure([manualPartial], null, 15), 10,
    "both endpoints win over ManualMinutes");

var outside = new TimeEntry
{
    StartUtc = now.AddHours(-3),
    EndUtc = now.AddHours(-2),
    TaskName = "Yesterday-ish",
    Source = "manual"
};
CheckWindow(Measure([outside], null, 15), 15, "entry outside the window");

var straddling = new TimeEntry
{
    StartUtc = now.AddMinutes(-20),
    EndUtc = now.AddMinutes(-10),
    TaskName = "Straddle",
    Source = "timer"
};
CheckWindow(Measure([straddling], null, 15), 10, "five minutes of overlap from before the window");

var overlapA = new TimeEntry { StartUtc = now.AddMinutes(-15), EndUtc = now.AddMinutes(-8), TaskName = "A", Source = "manual" };
var overlapB = new TimeEntry { StartUtc = now.AddMinutes(-12), EndUtc = now.AddMinutes(-5), TaskName = "B", Source = "manual" };
CheckWindow(Measure([overlapA, overlapB], null, 15), 5, "overlapping entries are a union, not a sum");

var early = new TimeEntry { StartUtc = now.AddMinutes(-15), EndUtc = now.AddMinutes(-10), TaskName = "Early", Source = "manual" };
var late = new TimeEntry { StartUtc = now.AddMinutes(-4), EndUtc = now, TaskName = "Late", Source = "manual" };
CheckWindow(Measure([early, late], null, 15), 6, "gap in the middle");

var orphanOpen = new TimeEntry { StartUtc = now.AddMinutes(-10), EndUtc = null, ManualMinutes = 0, TaskName = "Dangling", Source = "timer" };
CheckWindow(Measure([orphanOpen], null, 15), 15, "open entry that is not the active session is ignored");

var anchored = new TimeEntry
{
    StartUtc = now.AddMinutes(-10),
    EndUtc = null,
    ManualMinutes = 15,
    TaskName = "Anchored manual",
    Source = "manual"
};
CheckWindow(Measure([anchored], null, 15), 5, "ManualMinutes anchors an open non-active entry at StartUtc");

Console.WriteLine("drift");
var drift10 = Running(now.AddSeconds(-(15 * 60 - 10)));
CheckWindow(Measure([drift10.Entry], drift10.Session, 15), 0, "10 second gap is 0");
var drift29 = Running(now.AddSeconds(-(15 * 60 - 29)));
CheckWindow(Measure([drift29.Entry], drift29.Session, 15), 0, "29 second gap is 0");
var drift30 = Running(now.AddSeconds(-(15 * 60 - 30)));
CheckWindow(Measure([drift30.Entry], drift30.Session, 15), 1, "30 second gap rounds to 1 minute");
var drift40 = Running(now.AddSeconds(-(15 * 60 - 40)));
CheckWindow(Measure([drift40.Entry], drift40.Session, 15), 1, "40 second gap rounds to 1 minute");
var drift90 = Running(now.AddSeconds(-(15 * 60 - 90)));
CheckWindow(Measure([drift90.Entry], drift90.Session, 15), 2, "90 second gap rounds to 2 minutes");

var fiveEmpty = Measure([], null, 5);
CheckWindow(fiveEmpty, 5, "interval of 5 with nothing logged");

Console.WriteLine("placement");
var fillGap = UnaccountedTime.Place(
    [new CatchUpSlice { TaskName = "Email", Category = "Admin", Notes = "inbox", Minutes = 5 }],
    tenWindow);
Check(fillGap.Count == 1, "one slice in the 5 minute gap");
Check(fillGap.Count == 1 && fillGap[0].StartUtc == now.AddMinutes(-15) && fillGap[0].EndUtc == now.AddMinutes(-10),
    "full gap is placed before the running timer");
Check(fillGap.Count == 1 && fillGap[0].ManualMinutes == 5 && fillGap[0].Source == "fifteen",
    "placed row keeps the assigned minutes and source");
Check(fillGap.Count == 1 && fillGap[0].Notes == "inbox" && fillGap[0].TaskName == "Email",
    "task name and comment are kept");
Check(fillGap.All(e => !Overlaps(e.StartUtc, e.EndUtc!.Value, ten.Entry.StartUtc, now)),
    "placed row does not overlap the running timer");

var afterFill = Measure([ten.Entry, fillGap[0]], ten.Session, 15);
CheckWindow(afterFill, 0, "after assigning the gap, nothing is left unaccounted");

var split = UnaccountedTime.Place(
[
    new CatchUpSlice { TaskName = "A", Minutes = 3 },
    new CatchUpSlice { TaskName = "B", Minutes = 2 }
], tenWindow);
Check(split.Count == 2, "two slices");
Check(split[0].TaskName == "A" && split[0].StartUtc == now.AddMinutes(-13) && split[0].EndUtc == now.AddMinutes(-10),
    "first slice sits at the recent end of the gap");
Check(split[1].TaskName == "B" && split[1].StartUtc == now.AddMinutes(-15) && split[1].EndUtc == now.AddMinutes(-13),
    "second slice fills the earlier remainder");
Check(split.Sum(s => s.ManualMinutes) == 5, "split minutes add up");
Check(split.All(e => !Overlaps(e.StartUtc, e.EndUtc!.Value, ten.Entry.StartUtc, now)),
    "split rows do not overlap the timer");

var partialAssign = UnaccountedTime.Place(
    [new CatchUpSlice { TaskName = "Only some", Minutes = 10 }],
    empty);
Check(partialAssign.Count == 1 && partialAssign[0].StartUtc == now.AddMinutes(-10) && partialAssign[0].EndUtc == now,
    "assigning fewer than the window ends at now");
var leftOver = Measure(partialAssign, null, 15);
CheckWindow(leftOver, 5, "unassigned minutes stay unaccounted");

var wholeAssign = UnaccountedTime.Place(
    [new CatchUpSlice { TaskName = "All of it", Minutes = 15 }],
    empty);
Check(wholeAssign.Count == 1 && wholeAssign[0].StartUtc == now.AddMinutes(-15) && wholeAssign[0].EndUtc == now,
    "assigning the whole empty window covers it");

var middle = Measure([new TimeEntry { StartUtc = now.AddMinutes(-8), EndUtc = now.AddMinutes(-3), TaskName = "Middle", Source = "manual" }], null, 15);
var across = UnaccountedTime.Place(
    [new CatchUpSlice { TaskName = "Around", Minutes = 10 }],
    middle);
Check(across.Count == 2, "a slice splits across the covered middle");
Check(across.Sum(s => s.ManualMinutes) == 10, "split slice still totals the assigned minutes");
Check(across.All(e => !Overlaps(e.StartUtc, e.EndUtc!.Value, now.AddMinutes(-8), now.AddMinutes(-3))),
    "split slice does not overlap the middle entry");
Check(across.All(e => e.StartUtc >= now.AddMinutes(-15) && e.EndUtc <= now),
    "split slice stays inside the window");

var clipped = Measure([drift40.Entry], drift40.Session, 15);
var clippedPlace = UnaccountedTime.Place(
    [new CatchUpSlice { TaskName = "Drift fill", Minutes = clipped.UnaccountedMinutes }],
    clipped);
Check(clippedPlace.Count == 1, "rounded gap still places");
Check(clippedPlace.Count == 1 && clippedPlace[0].ManualMinutes == 1, "rounded minute is what was assigned");
Check(clippedPlace.Count == 1 && !Overlaps(clippedPlace[0].StartUtc, clippedPlace[0].EndUtc!.Value, drift40.Entry.StartUtc, now),
    "rounded placement does not overlap the timer");

Console.WriteLine(failed == 0 ? "ALL PASSED" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;

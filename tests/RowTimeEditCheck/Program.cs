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

var day = new DateTime(2026, 11, 11);
var dev = Guid.NewGuid();

static DateTime Local(int h, int m, int s = 0, int dayOfMonth = 11) =>
    new DateTime(2026, 11, dayOfMonth, h, m, s, DateTimeKind.Local).ToUniversalTime();

TimeEntry Timer(string task, DateTime start, DateTime? end, Guid? codeId = null, string codeText = "DEV — Development") => new()
{
    TaskName = task,
    Category = "Development",
    JobCodeId = codeId ?? dev,
    JobCodeText = codeText,
    StartUtc = start,
    EndUtc = end,
    Source = "timer"
};

TimeEntry Manual(string task, DateTime end, int minutes) => new()
{
    TaskName = task,
    Category = "Development",
    JobCodeId = dev,
    JobCodeText = "DEV — Development",
    StartUtc = end.AddMinutes(-minutes),
    EndUtc = end,
    ManualMinutes = minutes,
    Source = "add-time"
};

TimeSpan RowTotal(List<TimeEntry> entries, string key, DateTime now) =>
    TaskTypeSummary.Build(entries, day, now).FirstOrDefault(r => r.Key == key)?.Total ?? TimeSpan.Zero;

var clock = Local(17, 0);

// Remove from one entry: its start moves later.
{
    var a = Timer("Report", Local(9, 0), Local(10, 0));
    var entries = new List<TimeEntry> { a };
    var key = TaskTypeSummary.KeyOf(a);
    var result = RowTimeEdit.Remove(entries, day, key, null, TimeSpan.FromMinutes(20));
    Check(result.Applied, "remove 20 min from a single entry applies");
    Check(a.StartUtc == Local(9, 20) && a.EndUtc == Local(10, 0), "the entry starts 20 min later and keeps its end");
    Check(RowTotal(entries, key, clock) == TimeSpan.FromMinutes(40), "row total drops to 40 min");
}

// Across several entries, newest first; fully consumed entries are deleted.
{
    var oldest = Timer("Report", Local(8, 0), Local(8, 30));
    var middle = Timer("Report", Local(10, 0), Local(10, 15));
    var newest = Timer("Report", Local(13, 0), Local(13, 10));
    var entries = new List<TimeEntry> { oldest, middle, newest };
    var key = TaskTypeSummary.KeyOf(oldest);
    var result = RowTimeEdit.Remove(entries, day, key, null, TimeSpan.FromMinutes(30));
    Check(result.Applied && result.Deleted == 2 && result.Shortened == 1, "30 min takes two entries and shortens a third");
    Check(!entries.Contains(newest) && !entries.Contains(middle), "the newest entries are deleted when fully consumed");
    Check(entries.Contains(oldest) && oldest.StartUtc == Local(8, 5), "the oldest loses the last 5 min from its start");
    Check(RowTotal(entries, key, clock) == TimeSpan.FromMinutes(25), "row total is 55 - 30 = 25 min");
}

// Exactly the whole finished time deletes every finished entry.
{
    var a = Timer("Report", Local(9, 0), Local(9, 45));
    var entries = new List<TimeEntry> { a };
    var result = RowTimeEdit.Remove(entries, day, TaskTypeSummary.KeyOf(a), null, TimeSpan.FromMinutes(45));
    Check(result.Applied && entries.Count == 0, "removing all of an entry deletes it");
}

// The running segment is never touched and is not removable.
{
    var done = Timer("Report", Local(9, 0), Local(9, 30));
    var running = Timer("Report", Local(16, 0), null);
    var entries = new List<TimeEntry> { done, running };
    var key = TaskTypeSummary.KeyOf(done);
    Check(RowTimeEdit.RemovableTime(RowTimeEdit.RowEntries(entries, day, key), running.Id) == TimeSpan.FromMinutes(30),
        "removable time excludes the running segment");
    var result = RowTimeEdit.Remove(entries, day, key, running.Id, TimeSpan.FromMinutes(10));
    Check(result.Applied, "removal with a running segment applies to finished time");
    Check(running.StartUtc == Local(16, 0) && running.EndUtc is null && entries.Contains(running), "running segment is untouched");
    Check(done.StartUtc == Local(9, 10), "the finished segment took the cut");
    Check(RowTotal(entries, key, clock) == TimeSpan.FromMinutes(20 + 60), "live time still counts in the row");
}

// Over-removal is rejected with the maximum, and nothing changes.
{
    var a = Timer("Report", Local(9, 0), Local(9, 30));
    var running = Timer("Report", Local(16, 0), null);
    var entries = new List<TimeEntry> { a, running };
    var key = TaskTypeSummary.KeyOf(a);
    var result = RowTimeEdit.Remove(entries, day, key, running.Id, TimeSpan.FromMinutes(31));
    Check(!result.Applied && result.Removable == TimeSpan.FromMinutes(30), "removing more than the finished time is refused");
    Check(result.Error is not null && result.Error.Contains("30:00", StringComparison.Ordinal) &&
          result.Error.Contains("0 h 30 min", StringComparison.Ordinal), "the message states the maximum removable");
    Check(entries.Count == 2 && a.StartUtc == Local(9, 0), "a refused removal changes nothing");
    var zero = RowTimeEdit.Remove(entries, day, key, running.Id, TimeSpan.Zero);
    Check(!zero.Applied, "zero minutes is refused");
}

// Manual entries lose ManualMinutes.
{
    var manual = Manual("Report", Local(12, 0), 45);
    var entries = new List<TimeEntry> { manual };
    var key = TaskTypeSummary.KeyOf(manual);
    var result = RowTimeEdit.Remove(entries, day, key, null, TimeSpan.FromMinutes(15));
    Check(result.Applied && manual.ManualMinutes == 30, "manual 45 min minus 15 leaves ManualMinutes 30");
    Check(manual.StartUtc == Local(11, 30) && manual.EndUtc == Local(12, 0), "the manual block's clock span follows");
    Check(RowTotal(entries, key, clock) == TimeSpan.FromMinutes(30), "row total reads the reduced minutes");
}

// A cut that leaves part of a minute on a manual entry keeps the exact rest as a clock span.
{
    var manual = Manual("Report", Local(12, 0), 10);
    var timer = Timer("Report", Local(13, 0), Local(13, 0, 30));
    var entries = new List<TimeEntry> { manual, timer };
    var key = TaskTypeSummary.KeyOf(manual);
    var result = RowTimeEdit.Remove(entries, day, key, null, TimeSpan.FromMinutes(5));
    Check(result.Applied && !entries.Contains(timer), "the newer 30 s timer segment is consumed first");
    Check(RowTotal(entries, key, clock) == TimeSpan.FromSeconds(330), "exactly 5 min is removed overall (10:30 → 5:30)");
    Check(manual.ManualMinutes == 0 && manual.EndUtc == Local(12, 0) && manual.StartUtc == Local(11, 54, 30),
        "the manual block keeps 5:30 as a clock span ending where it did");
}

// Other rows and other days are untouched.
{
    var mine = Timer("Report", Local(9, 0), Local(10, 0));
    var other = Timer("Meeting", Local(10, 0), Local(11, 0));
    var yesterday = Timer("Report", Local(9, 0, 0, 10), Local(10, 0, 0, 10));
    var entries = new List<TimeEntry> { mine, other, yesterday };
    RowTimeEdit.Remove(entries, day, TaskTypeSummary.KeyOf(mine), null, TimeSpan.FromMinutes(60));
    Check(!entries.Contains(mine) && entries.Contains(other) && entries.Contains(yesterday),
        "only the row's entries on the displayed day are trimmed");
    Check(yesterday.StartUtc == Local(9, 0, 0, 10), "the same task on another day keeps its time");
}

// Move Time: every entry of the row on the day moves; other days stay.
var mtg = Guid.NewGuid();
const string MtgText = "MTG — Meetings";
{
    var a = Timer("Spec", Local(9, 0), Local(9, 30));
    var b = Timer("Spec", Local(11, 0), Local(11, 20));
    var yesterday = Timer("Spec", Local(9, 0, 0, 10), Local(9, 40, 0, 10));
    var unrelated = Timer("Other", Local(12, 0), Local(12, 15));
    var entries = new List<TimeEntry> { a, b, yesterday, unrelated };
    var key = TaskTypeSummary.KeyOf(a);
    var before = DayTotals.ForDay(entries.Where(e => TaskTypeSummary.IsOnDay(e, day)), clock);
    var result = RowTimeEdit.Move(entries, null, day, key, mtg, MtgText);
    Check(result.Applied && result.Moved == 2, "move re-points both of the day's entries");
    Check(a.JobCodeId == mtg && b.JobCodeId == mtg && a.JobCodeText == MtgText && b.JobCodeText == MtgText,
        "moved entries take the target's id and text");
    Check(yesterday.JobCodeId == dev && yesterday.JobCodeText == "DEV — Development", "the same task on another day is untouched");
    Check(unrelated.JobCodeId == dev, "other rows on the day are untouched");
    var rows = TaskTypeSummary.Build(entries, day, clock);
    Check(rows.All(r => r.Key != key), "the source row is gone");
    Check(rows.Single(r => r.Key == result.NewKey).Total == TimeSpan.FromMinutes(50), "the target row holds all 50 min");
    Check(DayTotals.ForDay(entries.Where(e => TaskTypeSummary.IsOnDay(e, day)), clock) == before, "the day's total is unchanged");
    var band = DayTotals.ByJobCodeForDay(entries, day, clock);
    Check(band.Count == 2 && band.Any(r => r.Label == JobCode.FormatLabelFromStored(MtgText) && r.Total == TimeSpan.FromMinutes(50)),
        "the per-code band shows the time under the target code");
}

// Rows with the same task + category on the target code merge.
{
    var source = Timer("Spec", Local(9, 0), Local(9, 30));
    var existing = Timer("Spec", Local(10, 0), Local(10, 45), mtg, MtgText);
    var entries = new List<TimeEntry> { source, existing };
    var result = RowTimeEdit.Move(entries, null, day, TaskTypeSummary.KeyOf(source), mtg, MtgText);
    var rows = TaskTypeSummary.Build(entries, day, clock);
    Check(result.Applied && rows.Count == 1, "moving onto a code with the same task row merges them");
    Check(rows[0].SegmentCount == 2 && rows[0].Total == TimeSpan.FromMinutes(75), "the merged row has both segments and 75 min");
}

// A running session on the row is re-pointed and keeps running.
{
    var done = Timer("Spec", Local(9, 0), Local(9, 30));
    var running = Timer("Spec", Local(16, 0), null);
    var entries = new List<TimeEntry> { done, running };
    var active = new ActiveSession
    {
        EntryId = running.Id,
        TaskName = "Spec",
        Category = "Development",
        JobCodeId = dev,
        JobCodeText = "DEV — Development",
        StartedUtc = running.StartUtc
    };
    var result = RowTimeEdit.Move(entries, active, day, TaskTypeSummary.KeyOf(done), mtg, MtgText);
    Check(result.Applied && result.ActiveMutated, "the running session is re-pointed");
    Check(result.ActiveSession is { } s && s.JobCodeId == mtg && s.JobCodeText == MtgText &&
          s.EntryId == running.Id && s.StartedUtc == Local(16, 0), "session has the target code, same entry and start");
    Check(running.EndUtc is null && running.StartUtc == Local(16, 0) && running.JobCodeId == mtg,
        "the running entry is still open under the target code");
    var moved = result.ActiveSession!;
    Check(TaskTypeSummary.KeyOf(moved.TaskName, moved.Category, moved.JobCodeId, moved.JobCodeText) == result.NewKey,
        "the session matches the moved row's key");
}

// A session on another row is left alone.
{
    var mine = Timer("Spec", Local(9, 0), Local(9, 30));
    var otherRunning = Timer("Other", Local(16, 0), null);
    var entries = new List<TimeEntry> { mine, otherRunning };
    var active = new ActiveSession { EntryId = otherRunning.Id, TaskName = "Other", Category = "Development", JobCodeId = dev, JobCodeText = "DEV — Development", StartedUtc = otherRunning.StartUtc };
    var result = RowTimeEdit.Move(entries, active, day, TaskTypeSummary.KeyOf(mine), mtg, MtgText);
    Check(result.Applied && !result.ActiveMutated && ReferenceEquals(result.ActiveSession, active), "a session on another row is not changed");
}

// Moving to "No job code" and refusing the row's own code.
{
    var a = Timer("Spec", Local(9, 0), Local(9, 30));
    var entries = new List<TimeEntry> { a };
    var same = RowTimeEdit.Move(entries, null, day, TaskTypeSummary.KeyOf(a), dev, "DEV — Development");
    Check(!same.Applied && a.JobCodeId == dev, "moving to the row's own code is refused");
    var none = RowTimeEdit.Move(entries, null, day, TaskTypeSummary.KeyOf(a), null, string.Empty);
    Check(none.Applied && a.JobCodeId is null && a.JobCodeText.Length == 0, "moving to No job code clears id and text");
    Check(DayTotals.ByJobCodeForDay(entries, day, clock).Single().Label == "No job code", "the band shows it under No job code");
}

// A ticket row moves as its own row and the running session keeps its ticket.
{
    var ticketed = Timer("Spec", Local(16, 0), null);
    ticketed.Ticket = "INC0012345";
    var plain = Timer("Spec", Local(9, 0), Local(9, 30));
    var entries = new List<TimeEntry> { ticketed, plain };
    var active = new ActiveSession
    {
        EntryId = ticketed.Id,
        TaskName = "Spec",
        Category = "Development",
        JobCodeId = dev,
        JobCodeText = "DEV — Development",
        Ticket = "INC0012345",
        StartedUtc = ticketed.StartUtc
    };
    var result = RowTimeEdit.Move(entries, active, day, TaskTypeSummary.KeyOf(ticketed), mtg, MtgText);
    Check(result.Applied && result.Moved == 1 && plain.JobCodeId == dev, "only the ticket row moves, not the same name without a ticket");
    Check(result.ActiveSession!.Ticket == "INC0012345" && TaskTypeSummary.KeyOf(result.ActiveSession) == result.NewKey,
        "the re-pointed session keeps its ticket and matches the new key");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All row time edit checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

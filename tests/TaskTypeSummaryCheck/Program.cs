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

var today = new DateTime(2026, 11, 11);
var dev = Guid.NewGuid();
var mtg = Guid.NewGuid();
var clock = Local(2026, 11, 11, 15);

// Two segments of the same task type merge, ignoring case on name and category.
var merged = TaskTypeSummary.Build(
[
    Entry("Write report", "Development", dev, "DEV — Development", Local(2026, 11, 11, 9), Local(2026, 11, 11, 9, 30)),
    Entry("write REPORT", "development", dev, "DEV — Development", Local(2026, 11, 11, 11), Local(2026, 11, 11, 11, 45))
], today, clock);
Check(merged.Count == 1, "two segments of the same task type make one row");
Check(merged[0].SegmentCount == 2, "that row counts 2 segments");
Check(merged[0].Total == TimeSpan.FromMinutes(75), "that row totals 30 + 45 minutes");
Check(merged[0].TaskName == "write REPORT", "row shows the most recent segment's name");

// Different category or job code makes separate rows.
var split = TaskTypeSummary.Build(
[
    Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 11, 9), Local(2026, 11, 11, 9, 30)),
    Entry("Write report", "Meetings", dev, "DEV", Local(2026, 11, 11, 10), Local(2026, 11, 11, 10, 30)),
    Entry("Write report", "Development", mtg, "MTG", Local(2026, 11, 11, 11), Local(2026, 11, 11, 11, 30)),
    Entry("Write report", "Development", null, "", Local(2026, 11, 11, 12), Local(2026, 11, 11, 12, 30))
], today, clock);
Check(split.Count == 4, "a different category or job code makes its own row");
Check(split.All(r => r.SegmentCount == 1), "each separate row has one segment");
Check(split[0].LastStartUtc == Local(2026, 11, 11, 12), "most recently worked row is first");

// With no code id, the code text decides the row.
var byText = TaskTypeSummary.Build(
[
    Entry("Call", "Support", null, "SUP", Local(2026, 11, 11, 9), Local(2026, 11, 11, 9, 10)),
    Entry("Call", "Support", null, "sup", Local(2026, 11, 11, 9, 20), Local(2026, 11, 11, 9, 30)),
    Entry("Call", "Support", null, "ADMIN", Local(2026, 11, 11, 9, 40), Local(2026, 11, 11, 9, 50))
], today, clock);
Check(byText.Count == 2, "without an id, job code text keys the row");
Check(byText.Single(r => r.JobCodeText == "sup").SegmentCount == 2, "matching code text merges");

// The id wins over the text, so a renamed code stays on one row.
var renamed = TaskTypeSummary.Build(
[
    Entry("Build", "Development", dev, "DEV — Old name", Local(2026, 11, 11, 9), Local(2026, 11, 11, 9, 10)),
    Entry("Build", "Development", dev, "DEV — New name", Local(2026, 11, 11, 10), Local(2026, 11, 11, 10, 10))
], today, clock);
Check(renamed.Count == 1 && renamed[0].JobCodeText == "DEV — New name", "same id with new text is one row showing the latest text");

// The running entry's live time is included once.
var running = Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 11, 14, 20), null);
var live = TaskTypeSummary.Build(
[
    Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 11, 9), Local(2026, 11, 11, 10)),
    running,
    running
], today, clock);
Check(live.Count == 1 && live[0].SegmentCount == 2, "the running segment counts once");
Check(live[0].Total == TimeSpan.FromMinutes(100), "row total is 60 closed + 40 live minutes");
var later = TaskTypeSummary.Build([running], today, clock.AddSeconds(30));
Check(later[0].Total == TimeSpan.FromMinutes(40.5), "live time follows the clock");

// Manual-minute entries count their ManualMinutes.
var manual = Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 11, 8), Local(2026, 11, 11, 8, 5));
manual.ManualMinutes = 25;
manual.Source = "manual";
var withManual = TaskTypeSummary.Build(
[
    manual,
    Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 11, 9), Local(2026, 11, 11, 9, 10))
], today, clock);
Check(withManual.Count == 1 && withManual[0].Total == TimeSpan.FromMinutes(35), "manual entry adds its ManualMinutes, not its span");

// Only today's entries are included.
var days = TaskTypeSummary.Build(
[
    Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 10, 9), Local(2026, 11, 10, 17)),
    Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 11, 9), Local(2026, 11, 11, 9, 15)),
    Entry("Write report", "Development", dev, "DEV", Local(2026, 11, 12, 9), Local(2026, 11, 12, 9, 15))
], today, clock);
Check(days.Count == 1 && days[0].SegmentCount == 1, "yesterday and tomorrow are left out");
Check(days[0].Total == TimeSpan.FromMinutes(15), "only today's minutes are summed");
Check(TaskTypeSummary.Build([], today, clock).Count == 0, "no entries makes no rows");

// Break rows and blank categories group like everything else.
var breaks = TaskTypeSummary.Build(
[
    Entry("Break", "Break", null, "", Local(2026, 11, 11, 12), Local(2026, 11, 11, 12, 20)),
    Entry("Break", "Break", null, "", Local(2026, 11, 11, 15), Local(2026, 11, 11, 15, 10)),
    Entry("Notes", "", null, "", Local(2026, 11, 11, 13), Local(2026, 11, 11, 13, 5)),
    Entry("Notes", "General", null, "", Local(2026, 11, 11, 13, 10), Local(2026, 11, 11, 13, 15))
], today, clock);
Check(breaks.Count == 2, "break and blank-category rows merge with their matches");
Check(breaks.Single(r => r.TaskName == "Break").Total == TimeSpan.FromMinutes(30), "break segments add up");
Check(breaks.Single(r => r.TaskName == "Notes").Category == "General", "blank category reads as General");

// The key for the active session matches the key for its entry.
Check(TaskTypeSummary.KeyOf(" Write Report ", "DEVELOPMENT", dev, "x") == TaskTypeSummary.KeyOf(running),
    "active-session key matches its entry regardless of case and spacing");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All task type summary checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

static DateTime Local(int year, int month, int day, int hour, int minute = 0) =>
    new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();

static TimeEntry Entry(string task, string category, Guid? codeId, string codeText, DateTime startUtc, DateTime? endUtc) => new()
{
    TaskName = task,
    Category = category,
    JobCodeId = codeId,
    JobCodeText = codeText,
    StartUtc = startUtc,
    EndUtc = endUtc
};

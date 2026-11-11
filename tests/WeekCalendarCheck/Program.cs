using ClosedXML.Excel;
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

// 9 Nov 2026 is a Monday (11 Nov 2026 is a Wednesday).
var monday = new DateTime(2026, 11, 9);
var friday = new DateTime(2026, 11, 13);
var saturday = new DateTime(2026, 11, 14);
var sunday = new DateTime(2026, 11, 15);
var nextMonday = new DateTime(2026, 11, 16);
Check(monday.DayOfWeek == DayOfWeek.Monday, "anchor date is Monday");
Check(friday.DayOfWeek == DayOfWeek.Friday, "anchor Friday is Friday");
Check(sunday.DayOfWeek == DayOfWeek.Sunday, "anchor Sunday is Sunday");

Check(WeekCalendar.MondayOf(monday) == monday, "Monday belongs to itself");
Check(WeekCalendar.MondayOf(sunday) == monday, "Sunday belongs to the Monday six days earlier");
Check(WeekCalendar.MondayOf(saturday) == monday, "Saturday belongs to the same Monday");
Check(WeekCalendar.MondayOf(nextMonday) == nextMonday, "the following Monday starts a new week");
Check(WeekCalendar.FormatRange(monday, friday) == "Mon 9 Nov 2026 – Fri 13 Nov 2026", "row label is Mon–Fri");

Check(WeekCalendar.FormatDuration(TimeSpan.FromHours(12.5)) == "12h 30m", "12.5 hours reads as 12h 30m");
Check(WeekCalendar.FormatDuration(TimeSpan.FromMinutes(90)) == "1h 30m", "90 minutes reads as 1h 30m");
Check(WeekCalendar.FormatDuration(TimeSpan.FromHours(2)) == "2h", "whole hours omit minutes");
Check(WeekCalendar.FormatDuration(TimeSpan.Zero) == "0m", "zero duration reads as 0m");

var clock = UtcFromLocal(2026, 11, 11, 12);
var mondayEntry = Entry("Monday task", UtcFromLocal(2026, 11, 9, 9), UtcFromLocal(2026, 11, 9, 10, 30),
    job: "09082-05", category: "Development", notes: "Design review");
var sameWeek = WeekCalendar.Collect([mondayEntry], new DateTime(2026, 11, 11), clock);
Check(sameWeek.Count == 1, "a Monday entry in the current week does not invent empty weeks");
Check(sameWeek[0].Monday == monday, "Monday entry is on that Mon–Fri row");
Check(sameWeek[0].Label.EndsWith("Fri 13 Nov 2026", StringComparison.Ordinal), "label ends on Friday");
Check(sameWeek[0].Days[0].Tasks.Count == 1, "the task is listed under Monday");
Check(sameWeek[0].Days[0].Tasks[0].TaskName == "Monday task", "task name is kept");
Check(sameWeek[0].Days[0].Tasks[0].Notes == "Design review", "notes are kept");
Check(sameWeek[0].TaskCount == 1 && sameWeek[0].HoursDisplay == "1h 30m", "closed span sums to 1h 30m");

var sundayEntry = Entry("Sunday task", UtcFromLocal(2026, 11, 15, 11), UtcFromLocal(2026, 11, 15, 12));
var laterNow = new DateTime(2026, 12, 2); // Wednesday; that week's Monday is 30 Nov 2026
Check(laterNow.DayOfWeek == DayOfWeek.Wednesday, "later anchor is Wednesday");
Check(WeekCalendar.MondayOf(laterNow) == new DateTime(2026, 11, 30), "current week Monday is 30 Nov 2026");

var skipped = WeekCalendar.Collect([sundayEntry], laterNow, clock);
var mondays = skipped.Select(week => week.Monday).ToList();
Check(mondays.Count == 2, "only the stored week and the empty current week are listed");
Check(mondays.Contains(monday), "Sunday entry stays on the earlier Mon–Fri row");
Check(mondays.Contains(new DateTime(2026, 11, 30)), "empty current week is included");
Check(!mondays.Contains(new DateTime(2026, 11, 16)), "empty week of 16 Nov is omitted");
Check(!mondays.Contains(new DateTime(2026, 11, 23)), "empty week of 23 Nov is omitted");

var sundayWeek = skipped.Single(week => week.Monday == monday);
Check(sundayWeek.Label == "Mon 9 Nov 2026 – Fri 13 Nov 2026", "weekend time keeps the Mon–Fri label");
Check(sundayWeek.Days[6].Date == sunday && sundayWeek.Days[6].Tasks.Count == 1, "Sunday entry is stored on Sunday");
Check(sundayWeek.BrowseDays.Count() == 6, "browse shows weekdays plus the Sunday that has time");
Check(sundayWeek.BrowseDays.All(day => day.Date != saturday), "empty Saturday stays hidden");

var currentOnly = WeekCalendar.Collect([], new DateTime(2026, 11, 11), clock);
Check(currentOnly.Count == 1 && currentOnly[0].Monday == monday, "with no entries only the current week remains");
Check(currentOnly[0].TaskCount == 0 && currentOnly[0].HoursDisplay == "0m", "empty current week reports 0 tasks and 0m");
Check(currentOnly[0].BrowseDays.Count() == 5, "empty week still shows Monday through Friday");

var manual = Entry("Manual", UtcFromLocal(2026, 11, 9, 8), UtcFromLocal(2026, 11, 9, 12), manualMinutes: 90);
var runningStart = UtcFromLocal(2026, 11, 10, 9);
var runningClock = runningStart.AddMinutes(30);
var running = Entry("Running", runningStart, endUtc: null);
var closed = Entry("Closed", UtcFromLocal(2026, 11, 11, 13), UtcFromLocal(2026, 11, 11, 14));
var summed = WeekCalendar.Collect([manual, running, closed], new DateTime(2026, 11, 11), runningClock);
Check(summed[0].TaskCount == 3, "task count is the number of entries");
Check(summed[0].Total == TimeSpan.FromHours(3), "hours sum manual minutes, elapsed time, and closed spans");
Check(summed[0].HoursDisplay == "3h", "combined 3 hours reads as 3h");
Check(summed[0].Days[1].Tasks[0].IsRunning, "open timer is marked running");
Check(summed[0].Days[1].Tasks[0].Duration == TimeSpan.FromMinutes(30), "open timer uses the supplied clock");

var manualWins = Entry("Override", runningClock.AddHours(-5), endUtc: null, manualMinutes: 20);
Check(WeekCalendar.DurationAt(manualWins, runningClock) == TimeSpan.FromMinutes(20),
    "ManualMinutes overrides the elapsed clock span");

var saturdayEntry = Entry("Saturday task", UtcFromLocal(2026, 11, 14, 15), UtcFromLocal(2026, 11, 14, 16),
    category: "Support");
var withWeekend = WeekCalendar.Collect([mondayEntry, saturdayEntry], new DateTime(2026, 11, 11), clock);
Check(withWeekend[0].TaskCount == 2, "weekend entries count toward the week");
Check(withWeekend[0].BrowseDays.Any(day => day.Date == saturday && day.Tasks.Count == 1),
    "Saturday is shown once it has an entry");

var older = WeekCalendar.Collect(
    [Entry("Older", UtcFromLocal(2026, 11, 2, 9), UtcFromLocal(2026, 11, 2, 10))],
    new DateTime(2026, 11, 11),
    clock);
Check(older.Count == 2, "a past week plus the current week");
Check(older[0].Monday == new DateTime(2026, 11, 2) && older[1].Monday == monday, "weeks are oldest first");

var exportWeeks = WeekCalendar.Collect(
    [
        mondayEntry,
        Entry("Next", UtcFromLocal(2026, 11, 16, 9), UtcFromLocal(2026, 11, 16, 11), notes: "Follow up")
    ],
    new DateTime(2026, 11, 18),
    clock);
Check(TimesheetExport.DefaultFileName(exportWeeks.Take(1).ToList(), ".pdf") == "Timesheet-2026-11-09-to-2026-11-13.pdf",
    "one week uses that Mon–Fri range in the file name");
Check(TimesheetExport.DefaultFileName(exportWeeks, ".xlsx") == "Timesheet-2-weeks-2026-11-09-to-2026-11-20.xlsx",
    "several weeks use a single file name spanning the selection");

var folder = Path.Combine(Path.GetTempPath(), "timesheet-buddy-week-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var xlsx = Path.Combine(folder, "weeks.xlsx");
    TimesheetExport.WriteExcel(xlsx, exportWeeks);
    using (var workbook = new XLWorkbook(xlsx))
    {
        Check(workbook.Worksheets.Count == 2, "Excel writes one worksheet per week");
        var first = workbook.Worksheet("2026-11-09");
        var second = workbook.Worksheet("2026-11-16");
        Check(first.Cell(1, 2).GetString().Contains("Mon 9 Nov 2026", StringComparison.Ordinal), "sheet shows the week range");
        Check(first.Cell(2, 2).GetValue<int>() == 1, "sheet shows the task count");
        Check(first.Cell(3, 2).GetString() == "1h 30m", "sheet shows the hours");
        Check(first.Cell(5, 1).GetString() == "Date" && first.Cell(5, 6).GetString() == "Notes", "sheet has the task columns");
        Check(first.Cell(6, 2).GetString() == "Monday task", "sheet lists the task");
        Check(first.Cell(6, 3).GetString() == "09082-05", "sheet lists the job code");
        Check(first.Cell(6, 4).GetString() == "Development", "sheet lists the category");
        Check(first.Cell(6, 5).GetString() == "1h 30m", "sheet lists the duration");
        Check(first.Cell(6, 6).GetString() == "Design review", "sheet lists the notes");
        Check(second.Cell(6, 2).GetString() == "Next", "second week is its own worksheet");
        Check(second.Cell(6, 6).GetString() == "Follow up", "second worksheet keeps that week's notes");
    }

    var pdf = Path.Combine(folder, "weeks.pdf");
    TimesheetExport.WritePdf(pdf, exportWeeks);
    var header = new byte[5];
    using (var stream = File.OpenRead(pdf))
        _ = stream.Read(header, 0, header.Length);
    Check(header.AsSpan().SequenceEqual("%PDF-"u8), "PDF file starts with the PDF header");
    Check(new FileInfo(pdf).Length > 1000, "PDF contains a document body");
}
finally
{
    try { Directory.Delete(folder, recursive: true); }
    catch { /* temp cleanup is best-effort */ }
}

if (failures.Count == 0)
{
    Console.WriteLine("All week checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

static TimeEntry Entry(
    string task,
    DateTime startUtc,
    DateTime? endUtc,
    string job = "",
    string category = "General",
    string notes = "",
    int manualMinutes = 0) => new()
{
    StartUtc = startUtc,
    EndUtc = endUtc,
    TaskName = task,
    JobCodeText = job,
    Category = category,
    Notes = notes,
    ManualMinutes = manualMinutes,
    Source = manualMinutes > 0 ? "manual" : "timer"
};

static DateTime UtcFromLocal(int year, int month, int day, int hour, int minute = 0)
{
    var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
    return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
}

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

TimeSpan Of(IReadOnlyList<AppUsageTotal> totals, string exe) =>
    totals.FirstOrDefault(t => string.Equals(t.Exe, exe, StringComparison.OrdinalIgnoreCase))?.Time ?? TimeSpan.Zero;

var chrome = new AppIdentity("chrome.exe", "Google Chrome");
var excel = new AppIdentity("EXCEL.EXE", "Microsoft Excel");
var threshold = AppUsageAccumulator.DefaultIdleThreshold;
var day = new DateOnly(2026, 11, 19);
var t0 = new DateTime(2026, 11, 19, 9, 0, 0);

// Friendly names.
Check(AppNames.Friendly("Google Chrome", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe") == "Google Chrome",
    "file description is used first");
Check(AppNames.Friendly("  ", "Contoso Suite", @"C:\Apps\contoso.exe") == "Contoso Suite", "blank description falls back to product name");
Check(AppNames.Friendly(null, null, @"C:\Tools\my.tool.exe") == "my.tool", "no version info falls back to the exe name without extension");
Check(AppNames.Friendly(null, " ", "") == AppNames.Unknown, "nothing at all is Unknown app");
Check(AppNames.FileName(@"C:\Windows\explorer.exe") == "explorer.exe", "Windows paths parse on any OS");
Check(AppNames.FileName("/usr/bin/code") == "code", "forward slashes parse too");
Check(AppNames.Self == "Timesheet Buddy" && AppNames.SelfExe == "TimesheetBuddy.exe", "own windows count as Timesheet Buddy");

// Name cache.
var lookups = 0;
var cache = new AppNameCache(path =>
{
    lookups++;
    return path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)
        ? ("Windows Explorer", "Microsoft® Windows® Operating System")
        : path.EndsWith("broken.exe", StringComparison.OrdinalIgnoreCase)
            ? throw new IOException("no version resource")
            : (null, null);
});
var explorer = cache.Identify(@"C:\Windows\explorer.exe", "explorer");
Check(explorer == new AppIdentity("explorer.exe", "Windows Explorer"), "explorer resolves to its file description");
cache.Identify(@"C:\WINDOWS\EXPLORER.EXE", "explorer");
Check(lookups == 1 && cache.Count == 1, "names are cached per exe path (case-insensitive)");
Check(cache.Identify(@"D:\x\broken.exe", null) == new AppIdentity("broken.exe", "broken"), "a version-info failure falls back to the exe name");
Check(cache.Identify(null, "Taskmgr") == new AppIdentity("Taskmgr.exe", "Taskmgr"), "access denied uses the process name");
Check(cache.Identify("  ", " ") == new AppIdentity("", AppNames.Unknown), "nothing known is Unknown app");
Check(new AppIdentity("EXCEL.EXE", "Excel").Key == new AppIdentity("excel.exe", "Microsoft Excel").Key, "key is the exe name, any case");

// Basic accumulation: each sample credits the interval since the previous one to the current app.
var acc = new AppUsageAccumulator(threshold);
acc.Sample(t0, chrome, TimeSpan.Zero, false);
for (var i = 1; i <= 60; i++)
    acc.Sample(t0.AddSeconds(i), chrome, TimeSpan.Zero, false);
for (var i = 61; i <= 90; i++)
    acc.Sample(t0.AddSeconds(i), excel, TimeSpan.Zero, false);
var totals = acc.Totals(day);
Check(Of(totals, "chrome.exe") == TimeSpan.FromSeconds(60), "60 s on Chrome");
Check(Of(totals, "EXCEL.EXE") == TimeSpan.FromSeconds(30), "30 s on Excel");
Check(totals[0].Name == "Google Chrome" && totals.Count == 2, "sorted longest first");
Check(acc.PendingTime == TimeSpan.Zero && acc.DirtyDays.SequenceEqual([day]), "active time is committed and the day is dirty");

// No foreground app (null) credits nothing and does not carry over to the next app.
acc = new AppUsageAccumulator(threshold);
acc.Sample(t0, chrome, TimeSpan.Zero, false);
acc.Sample(t0.AddSeconds(1), chrome, TimeSpan.Zero, false);
acc.Sample(t0.AddSeconds(10), null, TimeSpan.Zero, false);
acc.Sample(t0.AddSeconds(11), excel, TimeSpan.Zero, false);
Check(Of(acc.Totals(day), "chrome.exe") == TimeSpan.FromSeconds(1) && Of(acc.Totals(day), "EXCEL.EXE") == TimeSpan.FromSeconds(1),
    "a sample with no app credits nothing");

// A long gap between samples (sleep, stalled thread) is not credited.
acc = new AppUsageAccumulator(threshold);
acc.Sample(t0, chrome, TimeSpan.Zero, false);
acc.Sample(t0.AddSeconds(5), chrome, TimeSpan.Zero, false);
acc.Sample(t0.AddMinutes(10), chrome, TimeSpan.Zero, false);
acc.Sample(t0.AddMinutes(10).AddSeconds(1), chrome, TimeSpan.Zero, false);
Check(Of(acc.Totals(day), "chrome.exe") == TimeSpan.FromSeconds(6), "a gap over 15 s is skipped");

// Idle: 3 minutes active, then no input. Time after the last input is pending, and once idle passes
// 5 minutes it is dropped entirely.
acc = new AppUsageAccumulator(threshold);
var lastInput = t0.AddMinutes(3);
for (var s = 0; s <= 9 * 60; s++)
{
    var now = t0.AddSeconds(s);
    var idle = now > lastInput ? now - lastInput : TimeSpan.Zero;
    acc.Sample(now, chrome, idle, false);
    if (s == 4 * 60)
        Check(Of(acc.Totals(day), "chrome.exe") == TimeSpan.FromMinutes(4) && acc.PendingTime == TimeSpan.FromMinutes(1),
            "while idle under the threshold the time shows live but stays pending");
}
Check(Of(acc.Totals(day), "chrome.exe") == TimeSpan.FromMinutes(3), "an idle stretch past 5 minutes counts nothing");
Check(acc.PendingTime == TimeSpan.Zero, "the idle pending time was dropped");

// Input resumes after the idle period: the first sample only restarts the clock.
var back = t0.AddMinutes(20);
for (var s = 0; s <= 30; s += 10)
    acc.Sample(back.AddSeconds(s), chrome, TimeSpan.Zero, false);
Check(Of(acc.Totals(day), "chrome.exe") == TimeSpan.FromMinutes(3.5), "accrual resumes after input");

// Idle under the threshold then input: the reading time counts.
acc = new AppUsageAccumulator(threshold);
for (var s = 0; s <= 4 * 60; s++)
    acc.Sample(t0.AddSeconds(s), excel, TimeSpan.FromSeconds(s), false);
acc.Sample(t0.AddSeconds(4 * 60 + 1), excel, TimeSpan.Zero, false);
Check(Of(acc.Totals(day), "EXCEL.EXE") == TimeSpan.FromSeconds(241) && acc.PendingTime == TimeSpan.Zero,
    "4 minutes without input, then input, all counts and is committed");

// The threshold is configurable.
acc = new AppUsageAccumulator(TimeSpan.FromMinutes(1));
for (var s = 0; s <= 120; s++)
    acc.Sample(t0.AddSeconds(s), excel, TimeSpan.FromSeconds(s), false);
Check(Of(acc.Totals(day), "EXCEL.EXE") == TimeSpan.Zero, "a 1-minute threshold drops a 2-minute idle stretch");

// Lock: nothing accrues while locked, and pending time after the last input is dropped.
acc = new AppUsageAccumulator(threshold);
for (var s = 0; s <= 60; s++)
    acc.Sample(t0.AddSeconds(s), chrome, TimeSpan.Zero, false);
for (var s = 61; s <= 120; s++)
    acc.Sample(t0.AddSeconds(s), chrome, TimeSpan.FromSeconds(s - 60), false);
for (var s = 121; s <= 600; s++)
    acc.Sample(t0.AddSeconds(s), chrome, TimeSpan.FromSeconds(s - 60), true);
Check(Of(acc.Totals(day), "chrome.exe") == TimeSpan.FromSeconds(60), "locked time and the idle minute before an auto-lock are not counted");
acc.Sample(t0.AddSeconds(601), chrome, TimeSpan.Zero, false);
acc.Sample(t0.AddSeconds(611), chrome, TimeSpan.Zero, false);
Check(Of(acc.Totals(day), "chrome.exe") == TimeSpan.FromSeconds(70), "accrual resumes after unlock");

// Midnight: a span across midnight is split between the two days.
var lateDay = new DateOnly(2026, 11, 19);
var nextDay = new DateOnly(2026, 11, 20);
var late = new DateTime(2026, 11, 19, 23, 59, 50);
acc = new AppUsageAccumulator(threshold);
acc.Sample(late, chrome, TimeSpan.Zero, false);
acc.Sample(late.AddSeconds(5), chrome, TimeSpan.Zero, false);
acc.Sample(late.AddSeconds(15), chrome, TimeSpan.Zero, false);
acc.Sample(late.AddSeconds(25), chrome, TimeSpan.Zero, false);
Check(Of(acc.Totals(lateDay), "chrome.exe") == TimeSpan.FromSeconds(10), "10 s before midnight on the first day");
Check(Of(acc.Totals(nextDay), "chrome.exe") == TimeSpan.FromSeconds(15), "15 s after midnight on the next day");
Check(acc.DirtyDays.SequenceEqual([lateDay, nextDay]), "both days are dirty");

// A pending span across midnight is split too.
acc = new AppUsageAccumulator(threshold);
var input = new DateTime(2026, 11, 19, 23, 58, 0);
for (var s = 0; s <= 240; s++)
{
    var now = input.AddSeconds(s);
    acc.Sample(now, excel, now - input, false);
}
Check(Of(acc.Totals(lateDay), "EXCEL.EXE") == TimeSpan.FromMinutes(2) && Of(acc.Totals(nextDay), "EXCEL.EXE") == TimeSpan.FromMinutes(2),
    "pending time across midnight shows on both days");
acc.Pause(input.AddSeconds(241), TimeSpan.FromSeconds(1));
Check(acc.PendingTime == TimeSpan.Zero && acc.DirtyDays.SequenceEqual([lateDay, nextDay]) &&
      acc.Export(lateDay).Apps.Single().Seconds == 120 && acc.Export(nextDay).Apps.Single().Seconds == 120,
    "pause with recent input commits the pending time, split at midnight");

// Pause after a long idle drops the pending time.
acc = new AppUsageAccumulator(threshold);
for (var s = 0; s <= 120; s++)
    acc.Sample(t0.AddSeconds(s), excel, TimeSpan.FromSeconds(s), false);
acc.Pause(t0.AddMinutes(6), TimeSpan.FromMinutes(6));
Check(Of(acc.Totals(day), "EXCEL.EXE") == TimeSpan.Zero && acc.PendingTime == TimeSpan.Zero, "pause while idle drops pending time");
acc.Sample(t0.AddMinutes(7), excel, TimeSpan.Zero, false);
acc.Sample(t0.AddMinutes(7).AddSeconds(1), excel, TimeSpan.Zero, false);
Check(Of(acc.Totals(day), "EXCEL.EXE") == TimeSpan.FromSeconds(1), "after a pause the first sample only restarts the clock");

// Loading earlier totals, exporting, saving and evicting.
var root = Path.Combine(Path.GetTempPath(), "tsb-apptrack-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Check(AppUsageFiles.FileName(day) == "app-usage-2026-11-19.json", "file name format");
    Check(AppUsageFiles.Load(root, day) is null, "no file loads as null");

    AppUsageFiles.Save(root, day, new AppUsageDay
    {
        Date = "2026-11-19",
        Apps = [new AppUsageApp { Exe = "chrome.exe", Name = "Chrome (old name)", Seconds = 100 }]
    });
    var json = File.ReadAllText(Path.Combine(root, AppUsageFiles.FileName(day)));
    Check(json.Contains("\"exe\"") && json.Contains("\"name\"") && json.Contains("\"seconds\"") && json.Contains("\"date\""),
        "the file uses camelCase keys");
    Check(!File.Exists(Path.Combine(root, AppUsageFiles.FileName(day) + ".tmp")), "no temp file left behind");

    var loads = 0;
    acc = new AppUsageAccumulator(threshold, d => { loads++; return AppUsageFiles.Load(root, d); });
    acc.Sample(t0, chrome, TimeSpan.Zero, false);
    acc.Sample(t0.AddSeconds(10), chrome, TimeSpan.Zero, false);
    acc.Sample(t0.AddSeconds(20), chrome, TimeSpan.Zero, false);
    totals = acc.Totals(day);
    Check(Of(totals, "chrome.exe") == TimeSpan.FromSeconds(120), "saved time and new time add up");
    Check(totals[0].Name == "Google Chrome", "the newest friendly name wins");
    var export = acc.Export(day);
    Check(export.Date == "2026-11-19" && export.Apps.Count == 1 && export.Apps[0].Seconds == 120, "export holds the committed totals");
    AppUsageFiles.Save(root, day, export);
    acc.MarkSaved(day);
    Check(acc.DirtyDays.Count == 0, "a saved day is no longer dirty");
    Check(AppUsageFiles.Load(root, day)!.Apps[0].Seconds == 120, "saved file round-trips");

    acc.Totals(day.AddDays(-1));
    var before = loads;
    acc.EvictSaved(day);
    acc.Totals(day);
    acc.Totals(day.AddDays(-1));
    Check(loads == before + 1, "eviction keeps today and reloads other days from disk");

    File.WriteAllText(Path.Combine(root, AppUsageFiles.FileName(day.AddDays(-2))), "not json");
    Check(AppUsageFiles.Load(root, day.AddDays(-2)) is null, "an unreadable file loads as null");
    Check(Directory.GetFiles(root, "*.unreadable-*").Length == 1, "an unreadable file is set aside instead of being overwritten");

    var badLoads = 0;
    acc = new AppUsageAccumulator(threshold, _ => { badLoads++; throw new IOException("busy"); });
    Check(acc.Totals(day).Count == 0 && acc.Totals(day).Count == 0 && badLoads == 2, "a failed load is retried next time");

    // Data folder integration: app-usage files are part of the data and match the copy pattern.
    File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
    var files = DataFolder.DataFiles(root);
    Check(files.Contains(AppUsageFiles.FileName(day)), "DataFolder lists app-usage files");
    Check(!files.Any(f => f.Contains(".unreadable-") || f.EndsWith(".tmp")), "set-aside and temp files are not data files");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All App Track checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

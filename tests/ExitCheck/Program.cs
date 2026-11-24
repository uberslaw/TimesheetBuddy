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

var folder = Path.Combine(Path.GetTempPath(), "TimesheetBuddyExitCheck-" + Guid.NewGuid().ToString("N"));
var at = new DateTime(2026, 11, 24, 9, 5, 7);

try
{
    // When the confirmation shows.
    Check(ExitPrompt.ShouldAsk(true, null, false), "a user close asks when Ask before exiting is on");
    Check(!ExitPrompt.ShouldAsk(false, null, false), "Ask before exiting off: no question");
    Check(!ExitPrompt.ShouldAsk(true, ExitLog.Reasons.WindowsShutdown, false), "Windows shutdown never asks");
    Check(!ExitPrompt.ShouldAsk(true, ExitLog.Reasons.WindowsLogoff, false), "Windows sign-out never asks");
    Check(!ExitPrompt.ShouldAsk(true, "close request from another PC: Closed without saving", false), "a close request from another PC never asks");
    Check(!ExitPrompt.ShouldAsk(true, ExitLog.Reasons.Restart, false), "the data-folder restart never asks");
    Check(!ExitPrompt.ShouldAsk(true, null, true), "a shutdown already under way never asks");

    // What it says.
    var running = ExitPrompt.Detail("INC0012345 · Reset password", "1:05:03", noTaskRunning: false);
    Check(running.Contains("“INC0012345 · Reset password” is running (1:05:03)", StringComparison.Ordinal) &&
          running.Contains("saved to today's list as a finished entry", StringComparison.Ordinal),
        "a running timer is named and said to be saved");
    Check(ExitPrompt.Detail(null, null, noTaskRunning: true).Contains("No Task Selected", StringComparison.Ordinal),
        "running idle time is mentioned");
    Check(ExitPrompt.Detail(null, null, noTaskRunning: false) == "No task is running.", "nothing running");
    Check(ExitPrompt.Question == "Exit Timesheet Buddy?", "the question");
    Check(ExitLog.UserClose(ExitLog.Reasons.TitleBar, asked: true) == "title-bar ✕ (confirmed)", "user close reason when asked");
    Check(ExitLog.UserClose(ExitLog.Reasons.AltF4, asked: false).StartsWith("Alt+F4 (not asked", StringComparison.Ordinal),
        "user close reason when not asked");

    // Line format.
    var line = ExitLog.Format(at, 4321, ExitLog.Kinds.Exit, "title-bar ✕ (confirmed)\r\nsecond line");
    Check(line == "[2026-11-24 09:05:07] pid 4321 exit: title-bar ✕ (confirmed)  second line", "one line per event, newlines flattened");
    var parsed = ExitLog.Parse(line);
    Check(parsed is { Pid: 4321, Kind: ExitLog.Kinds.Exit } && parsed.TimeLocal == at, "lines parse back");
    Check(ExitLog.Parse("garbage") is null && ExitLog.Parse("") is null, "foreign lines are ignored");

    // Unfinished starts = sessions that ended without running exit code.
    string[] history =
    [
        ExitLog.Format(at, 10, ExitLog.Kinds.Start, "Timesheet Buddy"),
        ExitLog.Format(at.AddMinutes(1), 10, ExitLog.Kinds.Exit, ExitLog.Reasons.WindowsShutdown),
        ExitLog.Format(at.AddMinutes(2), 11, ExitLog.Kinds.Start, "Timesheet Buddy"),
        ExitLog.Format(at.AddMinutes(3), 11, ExitLog.Kinds.Error, "unhandled UI exception, error box shown"),
        ExitLog.Format(at.AddMinutes(4), 12, ExitLog.Kinds.Start, "Timesheet Buddy"),
        "not a log line"
    ];
    var lost = ExitLog.UnfinishedStarts(history, currentPid: 12);
    Check(lost.Count == 1 && lost[0].Pid == 11, "a start with no exit is found; finished and current sessions are not");
    Check(ExitLog.UnfinishedStarts(history.Append(ExitLog.Format(at.AddMinutes(5), 11, ExitLog.Kinds.Exit, "no exit was recorded")), 12).Count == 0,
        "once noted, a lost session is not reported again");

    // File: appended, trimmed to the last 200 lines, never throws.
    var path = Path.Combine(folder, ExitLog.FileName);
    for (var i = 0; i < ExitLog.MaxLines + 25; i++)
        Check(ExitLog.Append(path, ExitLog.Format(at.AddSeconds(i), 99, ExitLog.Kinds.Info, $"line {i}")), $"append {i}");
    var lines = File.ReadAllLines(path);
    Check(lines.Length == ExitLog.MaxLines, "the file keeps the last 200 lines");
    Check(lines[0].EndsWith("line 25", StringComparison.Ordinal) && lines[^1].EndsWith($"line {ExitLog.MaxLines + 24}", StringComparison.Ordinal),
        "the oldest lines are dropped first");
    Check(ExitLog.Read(path).Count == ExitLog.MaxLines && ExitLog.Read(Path.Combine(folder, "missing.log")).Count == 0, "read");
    Check(!ExitLog.Append(Path.Combine(path, "not-a-folder", ExitLog.FileName), "x"), "a failed write returns false instead of throwing");
    Check(ExitLog.Keep(["a", "", "b", "c"], 2).SequenceEqual(["b", "c"]), "blank lines are not kept");

    // Setting: on by default, also for older settings.json files without it.
    Check(new TimesheetBuddy.Models.AppSettings().AskBeforeExit, "Ask before exiting is on by default");
    var dataFolder = Path.Combine(folder, "data");
    Directory.CreateDirectory(dataFolder);
    File.WriteAllText(Path.Combine(dataFolder, "settings.json"), """{ "formatVersion": 2, "alwaysOnTop": true }""");
    Check(new SettingsStore(dataFolder).Load().AskBeforeExit, "a settings.json without askBeforeExit reads it as on");
    var store = new SettingsStore(dataFolder);
    var settings = store.Load();
    settings.AskBeforeExit = false;
    store.Save(settings);
    Check(!new SettingsStore(dataFolder).Load().AskBeforeExit, "Don't ask again is saved");
}
finally
{
    try { Directory.Delete(folder, recursive: true); } catch { }
}

if (failures.Count == 0)
{
    Console.WriteLine("All exit checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

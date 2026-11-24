using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TimesheetBuddy.Services;

/// <summary>
/// <c>exit.log</c> beside <c>crash.log</c> in the default folder: one line per start, exit and caught error,
/// <c>[yyyy-MM-dd HH:mm:ss] pid 1234 kind: text</c>, last <see cref="MaxLines"/> kept. A start with no exit
/// after it means that process ended without running any exit code (killed, power loss, or a crash that
/// could not be logged). Writing never throws. No WPF here.
/// </summary>
public static class ExitLog
{
    public const string FileName = "exit.log";
    public const int MaxLines = 200;

    public static class Kinds
    {
        public const string Start = "start";
        public const string Exit = "exit";
        public const string Info = "info";
        public const string Error = "error";
    }

    public static class Reasons
    {
        public const string TitleBar = "title-bar ✕";
        public const string AltF4 = "Alt+F4";
        public const string SystemClose = "taskbar or window menu close";
        public const string OtherClose = "main window closed";
        public const string WindowsShutdown = "Windows shutdown or restart";
        public const string WindowsLogoff = "Windows sign-out";
        public const string Restart = "restart to use another data folder";
        public const string UnknownShutdown = "application shut down (no reason recorded)";
    }

    public sealed record Line(DateTime TimeLocal, int Pid, string Kind, string Text);

    private static readonly Regex Pattern = new(
        @"^\[(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] pid (?<pid>\d+) (?<kind>[a-z]+): ?(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Format(DateTime timeLocal, int pid, string kind, string text) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[{timeLocal:yyyy-MM-dd HH:mm:ss}] pid {pid} {kind}: {OneLine(text)}");

    public static Line? Parse(string text)
    {
        var match = Pattern.Match(text ?? string.Empty);
        if (!match.Success) return null;
        if (!DateTime.TryParseExact(match.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return null;
        if (!int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            return null;
        return new Line(time, pid, match.Groups["kind"].Value, match.Groups["text"].Value);
    }

    /// <summary>The last <paramref name="max"/> non-blank lines.</summary>
    public static List<string> Keep(IEnumerable<string> lines, int max = MaxLines)
    {
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return kept.Count <= max ? kept : kept.GetRange(kept.Count - max, max);
    }

    /// <summary>
    /// Starts with no exit line after them from the same pid, oldest first, leaving out
    /// <paramref name="currentPid"/>. Each was a process that ended without logging why.
    /// </summary>
    public static List<Line> UnfinishedStarts(IEnumerable<string> lines, int currentPid)
    {
        var open = new Dictionary<int, Line>();
        foreach (var line in lines.Select(Parse).OfType<Line>())
        {
            if (line.Kind == Kinds.Start)
                open[line.Pid] = line;
            else if (line.Kind == Kinds.Exit)
                open.Remove(line.Pid);
        }

        open.Remove(currentPid);
        return open.Values.OrderBy(l => l.TimeLocal).ToList();
    }

    /// <summary>The reason logged for a user close, e.g. "title-bar ✕ (confirmed)".</summary>
    public static string UserClose(string source, bool asked) =>
        $"{source} ({(asked ? "confirmed" : "not asked: Ask before exiting is off")})";

    /// <summary>Appends a line and trims the file to <see cref="MaxLines"/>. Never throws; false when it could not write.</summary>
    public static bool Append(string path, string line, int max = MaxLines)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var lines = File.Exists(path) ? IoRetry.Run(() => File.ReadAllLines(path)).ToList() : [];
            lines.Add(line);
            var kept = Keep(lines, max);
            IoRetry.WriteAllTextAtomic(path, string.Join(Environment.NewLine, kept) + Environment.NewLine);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> Read(string path)
    {
        try
        {
            return File.Exists(path) ? IoRetry.Run(() => File.ReadAllLines(path)) : [];
        }
        catch
        {
            return [];
        }
    }

    private static string OneLine(string? text)
    {
        var value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 400 ? value : value[..399] + "…";
    }
}

/// <summary>When the "Exit Timesheet Buddy?" confirmation shows, and what it says.</summary>
public static class ExitPrompt
{
    public const string Question = "Exit Timesheet Buddy?";

    /// <summary>
    /// Only a user close asks: never once the app itself has started shutting down (Windows shutdown or
    /// sign-out, a close request from another PC, a restart, an unhandled error), and not when switched off.
    /// </summary>
    public static bool ShouldAsk(bool askBeforeExit, string? programmaticReason, bool applicationShuttingDown) =>
        askBeforeExit && programmaticReason is null && !applicationShuttingDown;

    /// <summary>What happens to running time on exit, matching the on-exit save.</summary>
    public static string Detail(string? runningTask, string? elapsed, bool noTaskRunning)
    {
        if (!string.IsNullOrWhiteSpace(runningTask))
        {
            var time = string.IsNullOrWhiteSpace(elapsed) ? string.Empty : $" ({elapsed})";
            return $"“{runningTask.Trim()}” is running{time}. It will be saved to today's list as a finished entry " +
                   "and will not resume when you start Timesheet Buddy again.";
        }

        return noTaskRunning
            ? "No task is running. No Task Selected time of a minute or more is kept for assigning later."
            : "No task is running.";
    }
}

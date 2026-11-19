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

// Line 1: job code + category.
Check(TimerWidgetLayout.HeaderLine(true, "089082-04", "04 — CS OFFICE SUPPORT", "Admin") == "089082-04 - Admin",
    "catalog code wins over stored label");
Check(TimerWidgetLayout.HeaderLine(true, null, "04 — CS OFFICE SUPPORT", "Support") == "04 - Support",
    "stored label is trimmed to its code part");
Check(TimerWidgetLayout.HeaderLine(true, null, "DEV – Software", "Development") == "DEV - Development",
    "en dash label is trimmed too");
Check(TimerWidgetLayout.HeaderLine(true, null, "MTG", "Meetings") == "MTG - Meetings",
    "code-only stored text is used as is");
Check(TimerWidgetLayout.HeaderLine(true, null, null, "Admin") == "No job code - Admin",
    "no code shows No job code");
Check(TimerWidgetLayout.HeaderLine(true, "  ", "", "Admin") == "No job code - Admin",
    "blank code and text show No job code");
Check(TimerWidgetLayout.HeaderLine(true, "ADMIN", null, "") == "ADMIN",
    "empty category drops the separator");
Check(TimerWidgetLayout.HeaderLine(false, "ADMIN", "ADMIN", "Admin") == "Idle",
    "idle header ignores stale values");

// Line 2: task name.
Check(TimerWidgetLayout.TaskLine(true, "  Feature work ") == "Feature work", "task name is trimmed");
Check(TimerWidgetLayout.TaskLine(false, "Feature work") == "No active task", "idle task line");

// Placement.
var work = new WidgetArea(0, 0, 1920, 1040);
var screen = new WidgetArea(0, 0, 1920, 1080);
var (dl, dt) = TimerWidgetLayout.DefaultPosition(work, 240, 84);
Check(dl == 1920 - 240 - 12 && dt == 1040 - 84 - 12, "default sits bottom-right above the taskbar");

var (nl, nt) = TimerWidgetLayout.Resolve(double.NaN, 100, 240, 84, work, screen, true);
Check(nl == dl && nt == dt, "NaN saved position falls back to default");

var (ul, ut) = TimerWidgetLayout.Resolve(null, null, 240, 84, work, screen, true);
Check(ul == dl && ut == dt, "missing saved position falls back to default");

var (kl, kt) = TimerWidgetLayout.Resolve(500, 300, 240, 84, work, screen, true);
Check(kl == 500 && kt == 300, "on-screen saved position is kept");

var (ol, ot) = TimerWidgetLayout.Resolve(3000, 1500, 240, 84, work, screen, true);
Check(ol == 1920 - 240 && ot == 1040 - 84, "single monitor: off-screen position is pulled into the work area");

var (bl, bt) = TimerWidgetLayout.Resolve(1800, 1020, 240, 84, work, screen, true);
Check(bl == 1920 - 240 && bt == 1040 - 84, "overlapping the edge and taskbar is pulled back fully");

var twoMonitors = new WidgetArea(0, 0, 3840, 1080);
var (sl, st) = TimerWidgetLayout.Resolve(2500, 400, 240, 84, work, twoMonitors, false);
Check(sl == 2500 && st == 400, "second monitor position is kept");

var (gl, gt) = TimerWidgetLayout.Resolve(5000, 400, 240, 84, work, twoMonitors, false);
Check(gl == dl && gt == dt, "position on a missing monitor falls back to default");

// No Task Selected line.
Check(TimerWidgetLayout.TaskLine(false, null, noTaskRunning: true) == "No Task Selected", "idle after Stop reads No Task Selected");
Check(TimerWidgetLayout.TaskLine(true, "Feature work", noTaskRunning: true) == "Feature work", "a running task wins over No Task Selected");

// Corner docking: 12 in from both edges of the monitor's work area, so bottom corners sit above the taskbar.
const double w = 268, h = 96;
var (tlx, tly) = TimerWidgetLayout.CornerPosition(work, w, h, WidgetCorner.TopLeft);
Check(tlx == 12 && tly == 12, "top-left dock");
var (trx, try_) = TimerWidgetLayout.CornerPosition(work, w, h, WidgetCorner.TopRight);
Check(trx == 1920 - w - 12 && try_ == 12, "top-right dock");
var (blx, bly) = TimerWidgetLayout.CornerPosition(work, w, h, WidgetCorner.BottomLeft);
Check(blx == 12 && bly == 1040 - h - 12, "bottom-left dock stays above the taskbar");
var (brx, bry) = TimerWidgetLayout.CornerPosition(work, w, h, WidgetCorner.BottomRight);
Check(brx == 1920 - w - 12 && bry == 1040 - h - 12, "bottom-right dock stays above the taskbar");
Check(TimerWidgetLayout.DefaultPosition(work, w, h) == (brx, bry), "default matches the bottom-right dock");

var topTaskbar = new WidgetArea(0, 40, 1920, 1080);
var (_, ttop) = TimerWidgetLayout.CornerPosition(topTaskbar, w, h, WidgetCorner.TopLeft);
Check(ttop == 52, "top dock stays below a top taskbar");

var leftMonitor = new WidgetArea(-1280, 0, 0, 984);
var (lmx, lmy) = TimerWidgetLayout.CornerPosition(leftMonitor, w, h, WidgetCorner.BottomRight);
Check(lmx == -w - 12 && lmy == 984 - h - 12, "docks on a monitor left of the primary");
var (rx, ry) = TimerWidgetLayout.Resolve(lmx, lmy, w, h, work, new WidgetArea(-1280, 0, 1920, 1080), false);
Check(rx == lmx && ry == lmy, "a docked position on another monitor survives the next show");
var (px, py) = TimerWidgetLayout.Resolve(brx, bry, w, h, work, screen, true);
Check(px == brx && py == bry, "a docked position on the primary survives the next show");

var tiny = new WidgetArea(0, 0, 200, 80);
var (sx, sy) = TimerWidgetLayout.CornerPosition(tiny, w, h, WidgetCorner.BottomRight);
Check(sx == 0 && sy == 0, "an area smaller than the widget pins it to the area's top-left");

// Device pixels to WPF units (150% scaling gives 2/3).
var dips = TimerWidgetLayout.FromDevice(new WidgetArea(0, 0, 2880, 1560), 2.0 / 3, 2.0 / 3);
Check(Math.Abs(dips.Right - 1920) < 0.001 && Math.Abs(dips.Bottom - 1040) < 0.001, "150% monitor converts to WPF units");
var bad = TimerWidgetLayout.FromDevice(new WidgetArea(0, 0, 100, 100), double.NaN, 0);
Check(bad.Right == 100 && bad.Bottom == 100, "a bad scale falls back to 1");

// Start resumes the most recent real task type, preferring today's.
var today = DateTime.Today;
TimeEntry Entry(string name, string category, string code, DateTime startLocal, bool deleted = false) => new()
{
    TaskName = name,
    Category = category,
    JobCodeText = code,
    StartUtc = startLocal.ToUniversalTime(),
    EndUtc = startLocal.AddMinutes(10).ToUniversalTime(),
    IsDeleted = deleted
};
var yesterdayLate = Entry("Old work", "General", "DEV", today.AddDays(-1).AddHours(23));
var todayEarly = Entry("Email", "Admin", "ADMIN", today.AddHours(8));
var todayLater = Entry("Feature work", "Development", "DEV", today.AddHours(9));
var todayBreak = Entry("Break", "Break", "", today.AddHours(10));
var todayDeleted = Entry("Deleted", "General", "DEV", today.AddHours(11), deleted: true);
var picked = AppState.LatestWorkedEntry([yesterdayLate, todayEarly, todayLater, todayBreak, todayDeleted], today);
Check(ReferenceEquals(picked, todayLater), "Start resumes today's most recent task, skipping Break and deleted rows");
Check(ReferenceEquals(AppState.LatestWorkedEntry([yesterdayLate, todayBreak], today), yesterdayLate),
    "with nothing real today, Start resumes the most recent earlier task");
Check(AppState.LatestWorkedEntry([todayBreak, todayDeleted], today) is null, "nothing to resume opens the app instead");

if (failures.Count == 0)
{
    Console.WriteLine("All timer widget checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

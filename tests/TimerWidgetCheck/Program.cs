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

// Coverage: share of the main window hidden by the union of other windows.
bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;
var mainRect = new WidgetArea(100, 100, 500, 400); // 400 x 300
Check(WidgetVisibilityRules.CoveredFraction(mainRect, []) == 0, "no covers: 0%");
Check(Near(WidgetVisibilityRules.CoveredFraction(mainRect, [new WidgetArea(100, 100, 300, 400)]), 0.5),
    "left half covered: 50%");
Check(Near(WidgetVisibilityRules.CoveredFraction(mainRect, [new WidgetArea(400, 300, 900, 900)]), 100.0 * 100 / 120000),
    "a cover hanging off the corner only counts its overlap");
Check(Near(WidgetVisibilityRules.CoveredFraction(mainRect, [new WidgetArea(0, 0, 1920, 1080)]), 1),
    "a full-screen app covers it completely");
Check(Near(WidgetVisibilityRules.CoveredFraction(mainRect,
        [new WidgetArea(100, 100, 300, 400), new WidgetArea(200, 100, 400, 400)]), 0.75),
    "overlapping covers are not double counted");
Check(Near(WidgetVisibilityRules.CoveredFraction(mainRect,
        [new WidgetArea(100, 100, 300, 400), new WidgetArea(100, 100, 300, 400)]), 0.5),
    "the same cover twice still counts once");
Check(Near(WidgetVisibilityRules.CoveredFraction(mainRect,
        [new WidgetArea(100, 100, 300, 250), new WidgetArea(300, 250, 500, 400)]), 0.5),
    "two disjoint quarters add up to half");
Check(WidgetVisibilityRules.CoveredFraction(mainRect,
        [new WidgetArea(600, 100, 900, 400), new WidgetArea(100, 500, 500, 800), new WidgetArea(500, 100, 700, 400)]) == 0,
    "covers outside (or only touching) the main window are ignored");
Check(WidgetVisibilityRules.CoveredFraction(mainRect, [new WidgetArea(200, 200, 200, 300), new WidgetArea(300, 300, 250, 350)]) == 0,
    "empty or inverted cover rects are ignored");
Check(WidgetVisibilityRules.CoveredFraction(new WidgetArea(0, 0, 0, 0), [new WidgetArea(0, 0, 10, 10)]) == 0,
    "an empty main rect is never covered");
var grid = new List<WidgetArea>();
for (var gx = 0; gx < 4; gx++)
for (var gy = 0; gy < 3; gy++)
    grid.Add(new WidgetArea(100 + gx * 100 - 10, 100 + gy * 100 - 10, 100 + gx * 100 + 110, 100 + gy * 100 + 110));
Check(Near(WidgetVisibilityRules.CoveredFraction(mainRect, grid), 1), "many overlapping tiles still total 100%");
var acc = new CoverageAccumulator(mainRect);
acc.Add(new WidgetArea(100, 100, 300, 400));
Check(acc.CoveredFraction >= WidgetVisibilityRules.CoveredThreshold, "accumulator reaches the threshold for an early stop");

// Primary monitor: the main window's centre decides.
var primaryMon = new WidgetArea(0, 0, 1920, 1080);
Check(WidgetVisibilityRules.CentreIsOn(new WidgetArea(1400, 600, 1880, 1040), primaryMon), "window on the primary");
Check(WidgetVisibilityRules.CentreIsOn(new WidgetArea(1600, 600, 2080, 1040), primaryMon), "straddling, centre still on primary");
Check(!WidgetVisibilityRules.CentreIsOn(new WidgetArea(1800, 600, 2280, 1040), primaryMon), "straddling, centre on the second monitor");
Check(!WidgetVisibilityRules.CentreIsOn(new WidgetArea(-1000, 100, -520, 540), primaryMon), "on a monitor left of the primary");

// Show/hide decision.
var allOn = new WidgetVisibilityOptions(true, true, true);
var visibleMain = new MainWindowSnapshot(Minimized: false, Hidden: false, Topmost: false, CoveredFraction: 0, OnPrimaryMonitor: true);
Check(WidgetVisibilityRules.Decide(visibleMain, allOn) == WidgetShowReason.None, "visible, unobscured, on primary: hide");
Check(WidgetVisibilityRules.Decide(visibleMain with { Minimized = true }, allOn) == WidgetShowReason.Minimized, "minimized: show");
Check(WidgetVisibilityRules.Decide(visibleMain with { Hidden = true }, allOn) == WidgetShowReason.Hidden, "hidden: show");
Check(WidgetVisibilityRules.Decide(visibleMain with { CoveredFraction = 0.5 }, allOn) == WidgetShowReason.Covered, "50% covered: show");
Check(WidgetVisibilityRules.Decide(visibleMain with { CoveredFraction = 0.49 }, allOn) == WidgetShowReason.None, "49% covered: hide");
Check(WidgetVisibilityRules.Decide(visibleMain with { OnPrimaryMonitor = false }, allOn) == WidgetShowReason.OtherMonitor,
    "on another monitor: show");
Check(WidgetVisibilityRules.Decide(visibleMain with { Topmost = true, CoveredFraction = 0.9 }, allOn) == WidgetShowReason.None,
    "pinned on the primary: hide even if a topmost app overlaps");
Check(WidgetVisibilityRules.Decide(visibleMain with { Topmost = true, OnPrimaryMonitor = false }, allOn) == WidgetShowReason.OtherMonitor,
    "pinned but on another monitor: show");
Check(WidgetVisibilityRules.Decide(visibleMain with { Topmost = true, Minimized = true }, allOn) == WidgetShowReason.Minimized,
    "pinned and minimized: show");
var masterOff = allOn with { Enabled = false };
Check(WidgetVisibilityRules.Decide(visibleMain with { Minimized = true }, masterOff) == WidgetShowReason.None, "master Disable: never minimized");
Check(WidgetVisibilityRules.Decide(visibleMain with { CoveredFraction = 1, OnPrimaryMonitor = false }, masterOff) == WidgetShowReason.None,
    "master Disable: never covered or other monitor");
var minimizeOnly = new WidgetVisibilityOptions(true, false, false);
Check(WidgetVisibilityRules.Decide(visibleMain with { CoveredFraction = 1 }, minimizeOnly) == WidgetShowReason.None, "covered option off");
Check(WidgetVisibilityRules.Decide(visibleMain with { OnPrimaryMonitor = false }, minimizeOnly) == WidgetShowReason.None, "other-monitor option off");
Check(WidgetVisibilityRules.Decide(visibleMain with { Hidden = true }, minimizeOnly) == WidgetShowReason.Hidden,
    "hidden still counts with both options off");
Check(WidgetVisibilityRules.Decide(visibleMain with { OnPrimaryMonitor = false, CoveredFraction = 1 }, allOn with { WhenOtherMonitor = false })
      == WidgetShowReason.Covered, "covered still applies with the monitor option off");
Check(WidgetVisibilityRules.Decide(visibleMain with { Minimized = true }, allOn, closing: true) == WidgetShowReason.None, "closing: hide");
Check(WidgetVisibilityRules.Decide(visibleMain with { OnPrimaryMonitor = false, ModalOpen = true }, allOn) == WidgetShowReason.None,
    "a prompt or dialog is open: hide");
var fresh = new AppSettings();
Check(fresh.ShowTimerWidget && fresh.ShowTimerWidgetWhenCovered && fresh.ShowTimerWidgetOnOtherMonitor, "all widget options default on");
var oldJson = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ShowTimerWidget\":true}")!;
Check(oldJson.ShowTimerWidgetWhenCovered && oldJson.ShowTimerWidgetOnOtherMonitor, "missing options in old settings.json read as on");

// Hysteresis.
var gate = new WidgetVisibilityGate();
Check(gate.Update(WidgetShowReason.Minimized, fromPoll: false), "minimize shows at once");
Check(!gate.Update(WidgetShowReason.None, fromPoll: false), "restore hides at once");
Check(!gate.Update(WidgetShowReason.Covered, fromPoll: false), "covered on an event alone does not show");
Check(!gate.Update(WidgetShowReason.Covered, fromPoll: true), "covered for one poll: still hidden");
Check(gate.Update(WidgetShowReason.Covered, fromPoll: true), "covered for two polls: shown");
Check(gate.Update(WidgetShowReason.Covered, fromPoll: false), "stays shown while covered");
Check(!gate.Update(WidgetShowReason.None, fromPoll: false), "activating the main window hides at once");
gate.Update(WidgetShowReason.OtherMonitor, fromPoll: true);
Check(!gate.Update(WidgetShowReason.None, fromPoll: true), "a one-poll blip resets");
Check(!gate.Update(WidgetShowReason.OtherMonitor, fromPoll: true), "the count restarts after a blip");
Check(gate.Update(WidgetShowReason.OtherMonitor, fromPoll: true), "other monitor for two polls: shown");
Check(gate.Update(WidgetShowReason.Covered, fromPoll: true), "switching between delayed reasons keeps it shown");
gate.Reset();
Check(!gate.Shown && !gate.Update(WidgetShowReason.OtherMonitor, fromPoll: true), "reset (prompt or widget click) starts the delay again");
Check(gate.Update(WidgetShowReason.Hidden, fromPoll: false), "hide shows at once even mid-delay");

if (failures.Count == 0)
{
    Console.WriteLine("All timer widget checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

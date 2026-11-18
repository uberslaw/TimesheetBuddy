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

if (failures.Count == 0)
{
    Console.WriteLine("All timer widget checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

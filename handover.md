# Handover — Timesheet Buddy 3.0

## Purpose

Desktop timesheet helper for logging time against job codes through the day. Compact always-on-top window with an active timer, favorites, settings, a “still working?” recheck prompt, and a catch-up check. Every catch-up interval, if some of the last N minutes are not already logged (including a running timer), it asks you to assign tasks to those unaccounted minutes. If a timer already covers the interval, it asks whether you are still on that task instead.

## Stack

| Item | Detail |
|------|--------|
| UI | WPF |
| Runtime | .NET 10 (`net10.0-windows`) |
| Language | C# |
| Persistence | JSON under `%LocalAppData%\TimesheetBuddy\` |
| OS | Windows only (will not run on macOS/Linux) |

## Layout (download this folder)

```
TimesheetBuddy/
├── TimesheetBuddy.csproj
├── App.xaml / App.xaml.cs          # App startup; shared State + Scheduler
├── MainWindow.xaml / .cs           # Compact tracker UI + prompt orchestration
├── Models/                         # JobCode, FavoriteTask, TimeEntry, AppSettings
├── Services/
│   ├── SettingsStore.cs            # Load/save settings.json
│   ├── AppState.cs                 # Timer session, entries, settings mutations
│   ├── UnaccountedTime.cs          # Catch-up overlap: unaccounted minutes vs still-working
│   ├── NoTaskTime.cs               # "No Task Selected" runs: 1-minute rule, open spans, placement
│   ├── PromptScheduler.cs          # Recheck + catch-up timers
│   ├── DayTotals.cs                # Today’s totals, including a live timer
│   ├── JobCodeViewList.cs          # Visible job-code rows (active codes, or a saved order)
│   ├── TaskTypeSummary.cs          # Today's entries grouped by task + category + job code
│   ├── LockIntervalSplit.cs        # Split a lock interval off the running entry
│   ├── ScreenLockTracker.cs        # Decide whether an unlock should prompt
│   ├── TimerWidgetLayout.cs        # Widget text lines + on-screen position clamp
│   ├── WidgetVisibilityRules.cs    # When the widget shows: rules, rect-union coverage, hysteresis (pure)
│   ├── WindowVisibility.cs         # Win32: z-order walk for coverage, primary-monitor check
│   └── ScreenLockWatcher.cs        # SessionLock / SessionUnlock → prompt
├── Views/
│   ├── SettingsWindow.*            # Job codes, favorites, categories, prompts, widget
│   ├── RecheckWindow.*             # “Still working on X?”
│   ├── FifteenMinuteWindow.*       # Assign unaccounted minutes to active/today/new task
│   ├── NoTaskAssignWindow.*        # Split pending No Task Selected time across tasks / Break
│   ├── DayLogWindow.*              # Viewing-day entries + day notes
│   ├── ManualEntryWindow.*         # Manual add from day log
│   ├── AddTimeWindow.*             # HH:MM add-time dialog (no live timer)
│   ├── AssignAndDeleteWindow.*     # Reassign each portion, then remove the task row
│   ├── RestoreDeletedWindow.*      # Same-day restore of soft-deleted task types
│   ├── LoadTimesheetWindow.*       # Calendar picker for history mode
│   ├── QuickAddJobCodeWindow.*     # Menu → Add job code (code + description)
│   ├── JobCodeViewWindow.*         # Alternate row-per-code timer view
│   ├── AddJobCodeWindow.*          # Job-code view: add existing or create one
│   ├── LockPromptWindow.*          # Assign lock time to last active task or another code
│   └── TimerWidgetWindow.*         # Small always-on-top timer shown while the main window can't be seen
├── README.md
└── handover.md                     # This file
```

`Assets/TimesheetBuddy.ico` is the hourglass app icon (exe via `ApplicationIcon`, every window via `Icon="/Assets/TimesheetBuddy.ico"`); regenerate it with `python tools/make_icon.py` (needs Pillow). An empty `Converters/` folder exists for future use.

## How to run

```bash
cd TimesheetBuddy
dotnet restore
dotnet build
dotnet run
```

Or open `TimesheetBuddy.csproj` in Visual Studio and press F5.

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

## Feature map

| Feature | Where |
|---------|--------|
| Window size (~2× tray width, fixed; height resizable, saved) | `MainWindow.ApplyCompactSize()` |
| Today by task (expandable cumulative rows; click to continue) | `MainWindow` + `Services/TaskTypeSummary` |
| Expand ▶/▼ → per-segment HH:mm + duration | `TaskTypeRowModel.Children` from `TaskSegment` |
| Right-click → Add time / Start|Continue / Assign time and Delete / Delete | `MainWindow` context menu + `AddTimeWindow` / `AssignAndDeleteWindow` |
| Soft-delete + same-day restore | `TimeEntry.IsDeleted` / `DeletedAtLocal` + `RestoreDeletedWindow` |
| Load older timesheet (history mode) | `AppState.ViewingDate` / `IsHistoryMode` + `LoadTimesheetWindow` |
| Pin / always on top | Pin toggle → `AppState.UpdatePromptSettings` |
| Start timer from job code / task / Enter | `MainWindow` selection + Start handlers (disabled in history) |
| Stop timer | Stop button → `AppState.StopActive` (starts No Task Selected) |
| No Task Selected timer (starts on any user Stop, ends on any Start; band row + big counter) | `AppState.StopActive` / `StartOrSwitch` + `Services/NoTaskTime` |
| Assign No Task Selected time (next interval; split across tasks, All to Break, Not now) | `MainWindow.OnFifteenDue` → `Views/NoTaskAssignWindow` → `AppState.AssignNoTaskTime` |
| Quick-add favorites menu | `MainWindow.QuickAdd_Click` |
| Menu → Add job code… | `Views/QuickAddJobCodeWindow` → `AppState.AddJobCode` |
| Settings (codes, favorites, categories, intervals, Widget tab); ⚙ button or Menu → Settings… | `Views/SettingsWindow` |
| Still-working recheck popup | `PromptScheduler` → `Views/RecheckWindow` (skipped in history) |
| Catch-up prompt (only when minutes are unaccounted; Not now dismisses) | `PromptScheduler` → `UnaccountedTime` → `Views/FifteenMinuteWindow` or `RecheckWindow` |
| Day log + free-form day notes | `Views/DayLogWindow` (uses `ViewingDate`) |
| Manual time entry | `Views/ManualEntryWindow` (lands on `ViewingDate`) |
| Job code view (switch from the Menu; Compact tracker returns) | `Views/JobCodeViewWindow` |
| Screen-lock prompt while a timer is running | `ScreenLockWatcher` → `Views/LockPromptWindow` |
| Timer widget (code - category, task, ▶ HH:MM:SS ■; drag to move, click to restore; Settings → Widget → Show when minimized) | `MainWindow.UpdateTimerWidget` → `Views/TimerWidgetWindow` + `Services/TimerWidgetLayout` |
| Widget also when the main window is buried behind other apps or on another monitor (Settings → Widget checkboxes) | `MainWindow.UpdateTimerWidget` → `Services/WindowVisibility` (Win32) → `Services/WidgetVisibilityRules` (`Decide`, `CoverageAccumulator`, `WidgetVisibilityGate`) |
| Widget corner arrows (dock to that corner of the widget's monitor work area) | `TimerWidgetWindow.Dock_Click` → `TimerWidgetLayout.CornerPosition` |
| Widget Start (resume last worked task type) / Stop (same stop as the main window) | `MainWindow.StartFromWidget` → `AppState.ResumeLastTask`; `MainWindow.StopFromUser` |

## Data

| File | Contents |
|------|----------|
| `%LocalAppData%\TimesheetBuddy\settings.json` | Job codes, favorites, categories, prompt prefs, window position, timer widget toggles (`showTimerWidget`, `showTimerWidgetWhenCovered`, `showTimerWidgetOnOtherMonitor`) + position, job-code view order (`jobCodeViewIds`), all time entries, active session, running No Task Selected start (`noTaskStartedUtc`) and pending unassigned spans (`unassignedSegments`) |
| `%LocalAppData%\TimesheetBuddy\day-notes-yyyyMMdd.txt` | Per-day free-text notes from the day log |
| `%LocalAppData%\TimesheetBuddy\crash.log` | Unhandled exceptions. Startup failures also show a dialog with this path |

Defaults seed a few job codes (`ADMIN`, `DEV`, `MTG`, `SUP`) and sample favorites on first launch.

Closing the app while a timer is running saves that task onto the current day's list as a finished entry (end time set). It does not resume after restart.

**No Task Selected.** Any user stop (main Stop, job-code view Stop, widget Stop, the still-working dialog's Stop, deleting the running task, loading an older day) starts a "No Task Selected" timer from the same instant; `AppState.StartOrSwitch` from anywhere ends it. Closing the app (`AppState.StopAllForShutdown`) finishes the running task without starting idle time and closes any running idle run into pending; nothing is started on launch, and a running start left in settings.json by a crash is dropped on load (its end is unknown). A run under 1 minute is discarded; 1 minute or more becomes pending. Storage is separate from time entries: `noTaskStartedUtc` (running start, nullable) and `unassignedSegments` (`startUtc` / `endUtc` pairs) in settings.json. Because every total reads `Entries`, unassigned time is never in the band Total, Time Remaining, job-code rows, the job-code view, Today by task, Weeks & export, PDF or Excel. The main window shows it as a muted italic "No Task Selected" row in the band (today's pending plus the running run; not added to Total), and while it runs the big counter shows the idle elapsed time and the status line reads "No Task Selected". Open time is pending plus the running run once it reaches a minute, minus anything an entry already covers (e.g. Add time over that span). On each catch-up interval (same `FifteenMinuteIntervalMinutes` cadence), if open time is at least 1 whole minute, `NoTaskAssignWindow` opens instead of any other catch-up dialog: "{n} minutes were logged as No Task Selected. Where should they go?", with the same rows as the old catch-up grid (task, category, common task/favorite, comment, job code, minutes), Add row, All to Break (`Break` / `Break`, no job code), Not now (stays pending, asked again next interval) and Assign. Minutes must be positive and at most n (whole minutes, rounded down). Assigned entries (`source: "no-task"`) are placed inside the open spans latest first, so they never overlap each other or logged time; the rest stays pending (a leftover under 1 minute is dropped). If idle is running and has reached a minute when you assign, it is closed at now for the assignment and restarts from now. The unaccounted-minutes check treats pending and running idle spans as covered, so the same minutes are never asked about twice, and one tick shows at most one dialog. The screen-lock prompt still only follows a running task. If the catch-up prompt is turned off in Settings, pending time just stays pending and visible. Check: `dotnet run --project tests/NoTaskTimeCheck`.

**Job code view.** Menu → Job code view opens a taller window with one row per code. Click a row to run the timer on that code (the row turns green); Stop ends it for the whole view. Each row shows today’s time for that code, including the live timer, and the footer total is every entry logged today. Add inserts an existing code or creates a code plus description. Remove only changes the saved row list (`AppSettings.JobCodeViewIds`); it does not delete time entries. Until the user adds or removes a row, the view lists the active codes.

**Today by task (main window).** Below the inputs, the compact tracker lists the viewing day's work with one row per task type: task name + category + job code. Name and category match case-insensitively; the code matches on `JobCodeId`, or on the code text when an entry has no id. Every non-deleted entry logged that day joins its row automatically (timer, manual, add-time, catch-up, lock-split slices, Break). Columns: expand arrow, task, category, job code, total (`hh:mm:ss`, including the live timer), and the number of segments. ▶/▼ expands indented child rows for each segment (local HH:mm + duration, chronological); parent rows on either side stay visible. Click a parent row to continue that task type (`AppState.StartOrSwitch`); the expand arrow does not start a timer. Right-click offers Add time… (finished manual segment, no live timer), Start timer / Continue, Assign time and Delete… (reassign each portion’s clocks to other tasks via `TaskTypeReassign` / `AssignAndDeleteWindow`, then remove the source row), and Delete task. Soft-delete sets `TimeEntry.IsDeleted` + `DeletedAtLocal` on all of that type's entries for the viewing day (and stops the timer if that type is active). Deleted entries are filtered out of TaskTypeSummary, EntriesForDay, UnaccountedTime, WeekCalendar, and DayTotals. Purge removes soft-deleted entries whose `DeletedAtLocal.Date` is before today on load/save. Menu → Restore deleted tasks… restores today's groups. Rows are ordered by most recent start and only re-sort when entries change, not on the one-second tick. Check: `dotnet run --project tests/TaskTypeSummaryCheck`.

**History mode.** Menu → Load timesheet… opens a calendar (no future dates). Entering another day stops any active timer, sets `AppState.ViewingDate` / `IsHistoryMode`, shows a yellow banner (“Editing {date} — timer inactive” + Back to today), and disables Start/Stop/job-code auto-start. Row clicks open Add time instead of starting a timer. Manual and Add time entries land on `ViewingDate` (end at that day's clock time-of-day equivalent). `PromptScheduler` does not fire recheck/fifteen while `IsHistoryMode`. Menu → Back to today (or the banner button) calls `ExitHistoryMode()`.

**Timer widget.** Minimizing the main window (its button, the taskbar, Win+M) shows a 268×96 always-on-top widget that never takes focus or a taskbar slot. Line 1 is `<JobCode.Code> - <category>` (stored label trimmed to its code part when the code is not in the catalog, `No job code - <category>` without one), line 2 the task name, line 3 is ▶ Start, the same `ElapsedDisplay` as the main counter in `ActiveTimerBrush`, and ■ Stop. After a Stop it shows `Idle` / `No Task Selected` / the idle counter; with nothing ever stopped, `Idle` / `No active task` / `00:00:00`. It refreshes from `AppState.PropertyChanged`, driven by the main window's one-second tick. Stop runs the main window's stop path (`StopFromUser`, so No Task Selected starts) and is disabled while no task runs. Start is disabled while a task runs (and in history mode); otherwise it resumes the task type (name + category + job code) of the most recently started non-deleted, non-Break entry, today's first, falling back to the latest earlier day (`AppState.LatestWorkedEntry` / `ResumeLastTask`); with nothing to resume it opens the main window. Four faint outward arrows in the corners (brighter on hover, tooltips "Dock top-left" etc.) dock the widget 12 units in from that corner of the work area of the monitor it is on (Win32 `MonitorFromWindow` + `GetMonitorInfo`, converted to WPF units; falls back to the primary work area), so bottom corners sit above the taskbar, and save the spot through `SaveWidgetPosition`. Buttons and arrows handle their own mouse-down, so they never drag or open the app; Start/Stop sit in transparent slots that swallow presses while the button is disabled (WPF does not hit-test disabled controls, so the press would otherwise reach the widget). Anywhere else, press + move drags (`DragMove`, saved as `widgetLeft` / `widgetTop`, finite values only) and press + release without moving more than 3 units, or a double-click, restores the main window, which hides the widget; prompts do the same via `EnsureVisibleForPrompt`. On each show the position is pulled back onto the work area, or reset to bottom-right if its monitor is gone. Closing the main window closes it. Settings → Widget → Show when minimized (Enable / Disable, default Enable) is the same `showTimerWidget` setting and the master switch; Disable hides a showing widget at once. Under it, two checkboxes (default on, greyed while Disable is picked): "Also when the main window is covered by other windows" (`showTimerWidgetWhenCovered`) and "Also when the main window is on another monitor" (`showTimerWidgetOnOtherMonitor`); a settings.json without them reads them as on. Check: `dotnet run --project tests/TimerWidgetCheck`.

**When the widget shows.** The rule is "the user can't see the main window on the primary monitor". `MainWindow.UpdateTimerWidget` gathers a `MainWindowSnapshot` and `WidgetVisibilityRules.Decide` returns, in order: hide while closing, while the master is Disable, or while a modal dialog (any prompt, Settings, Day log…) is open (`ComponentDispatcher.IsThreadModal`); show if minimized; show if hidden (`Visibility != Visible` or `!IsVisible`; always counts with the master on); show if the "another monitor" option is on and the main window's centre (`GetWindowRect`) is outside the primary monitor's bounds (`MonitorFromPoint(0,0, MONITOR_DEFAULTTOPRIMARY)` + `GetMonitorInfo`; both in the thread's DPI coordinate space, a failed probe counts as on the primary); show if the "covered" option is on, the main window is not pinned (`Topmost`) and at least `CoveredThreshold` (50%) of it is covered; otherwise hide. Pinned on the primary and not minimized therefore always hides. Coverage (`WindowVisibility.CoveredFraction`) walks windows above the main HWND with `GetWindow(GW_HWNDPREV)` (at most 1024) and counts a window only if it is visible, not minimized, not DWM-cloaked (suspended UWP apps, other virtual desktops), not owned by this process (the widget, dialogs, the job-code view), not `WS_EX_TOOLWINDOW`, not a click-through (`WS_EX_LAYERED | WS_EX_TRANSPARENT`) or alpha-0 layered overlay, not a shell window (`Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`), and has a non-empty rect (`DWMWA_EXTENDED_FRAME_BOUNDS`, falling back to `GetWindowRect`). `CoverageAccumulator` keeps the uncovered part as disjoint rectangles and cuts each cover out, so overlaps are not double counted; the walk stops as soon as 50% is reached. It runs on StateChanged, Activated, Deactivated, LocationChanged, SizeChanged, IsVisibleChanged, pin toggle and settings changes, and on the existing one-second `_uiTimer` tick (other apps moving in front raise no event here). `WidgetVisibilityGate` adds hysteresis: minimized/hidden show at once and "nothing to show" hides at once (so clicking the main window hides it immediately), while covered / other monitor must hold on 2 consecutive ticks (`StablePollsToShow`) before the widget appears; events never advance that count. Clicking the widget and prompts (`EnsureVisibleForPrompt`) reset the gate, so with the main window on another monitor the widget comes back about 2 s after it is restored. Showing never activates the widget (`ShowActivated=False`, `Show()` only when not already visible, so a drag in progress is never repositioned) and never touches the main window's z-order.

**Screen lock.** If Windows locks the session while a timer is running, unlock (after at least 30 seconds) asks how to assign that time: Assign on the last-active row (job code, category, task name) keeps it there; Assign beside a job-code selector moves the lock interval onto that code. Moving the interval leaves the time before the lock on the original code and continues that code from the unlock moment, so the locked minutes are not counted twice. A lock under 30 seconds, or a lock while the timer is idle, does not prompt.

## Architecture notes

- **No MVVM framework** — code-behind + `AppState` (`INotifyPropertyChanged`) for simplicity.
- **Singletons on `App`**: `App.State`, `App.Scheduler`, `App.Store`.
- **PromptScheduler** ticks every second; only one modal prompt at a time (`RecheckOpen` / `FifteenOpen`).
- Catch-up measures the last N minutes (`FifteenMinuteIntervalMinutes`, same value the scheduler waits). Accounted time is the union of logged entries plus the running timer's elapsed overlap. A gap under 30 seconds counts as zero. If at least one minute is unaccounted, the assign-tasks dialog opens and the still-working dialog does not. If nothing is unaccounted and a timer is running, the existing still-working window opens instead. If nothing is unaccounted and no timer is running, no popup appears.
- The still-working window stays until the user chooses. Catch-up asks how to assign the whole unaccounted block: last active task, another task logged today, or a newly entered task. Not now dismisses without creating entries.
- Selecting a job code in the main window **starts** the timer immediately.

## Known gaps / follow-ups

The original brief ended mid-sentence (“I’ll need a section where i can pu in…”). A **day notes** box was added in the day log as a best-effort interpretation — confirm or replace with the intended section (projects, clients, export, etc.).

Other reasonable next steps if needed:

- Export day log to CSV/Excel
- Edit/delete historical entries in the day log
- System tray icon / minimize-to-tray
- Installer / single-file publish (`dotnet publish -c Release -r win-x64 --self-contained`)
- Wire empty `Converters/` if binding converters are added

## Smoke-test checklist

1. Launch → compact window appears near bottom-right; Pin keeps it on top.
2. Settings → add a job code and a favorite → Save.
3. Pick job code → timer starts; Stop ends it.
4. Quick add a favorite → timer switches to that task.
5. Settings → set recheck to a short interval (e.g. 0h 5m) with an active task → popup appears; choose next interval.
6. Settings → set catch-up interval to 5 minutes. With a timer that has already been running for those 5 minutes, the still-working prompt appears and the assign-tasks dialog does not. With no timer and no entries in that window, the assign-tasks dialog shows the unaccounted minutes; Not now closes it without creating entries. Saving is blocked when the assigned minutes are 0 or more than the gap.
7. Day log shows entries; day notes persist after blur/refocus.
8. Close while a timer is running → the task is saved on today's list as a finished entry and does not resume after restart.
9. Run a task, Stop, run another, then click the first task's row under "Today by task" → it turns green, its total keeps counting, and its segment count goes from 1 to 2. Clicking it again does nothing. Drag the bottom edge to resize; the height comes back after restart.
10. Start a task and minimize (button, taskbar, Win+M) → the widget appears bottom-right with `code - category`, the task name and a ticking orange counter matching the main one; it does not take focus. Drag it elsewhere, restore, minimize again → same spot. Click it → the main window comes back and the widget hides. Stop the timer and minimize → `Idle` / `No Task Selected` / the idle counter (on a fresh launch with nothing stopped: `Idle` / `No active task` / `00:00:00`). With the widget showing, let a recheck or catch-up prompt fire → main window restores, widget hides, the dialog is on top. Close the app from the taskbar while minimized → the process exits.

11. Start a task, press Stop → status reads "No Task Selected", the big counter restarts from 00:00:00 and a muted italic "No Task Selected" row appears in the band; Total and Time Remaining do not move. Start a task within a minute → the row disappears (discarded). Stop again and wait over a minute, then let the catch-up interval fire → "{n} minutes were logged as No Task Selected…" appears (not the unaccounted dialog). Assign part of it → those minutes appear under Today by task and Total grows; the rest stays in the row. Not now → asked again next interval. All to Break → a Break row appears. Stop a task and close the app → on relaunch nothing is running and the idle time is still pending if it reached a minute.
12. Minimize with a task running → ▶ is greyed, ■ is live. Click ■ → the widget reads `No Task Selected` with a counting idle timer and the main window has not opened. Click ▶ → the last task resumes (its row turns green after restore) without opening the app. With no entries at all, ▶ opens the main window. Click each corner arrow → the widget jumps to that corner of the monitor it is on, clear of the taskbar; restore and minimize again → same corner. Press on a disabled ▶ → nothing happens (no open, no drag). Press on the text and drag → it moves; a plain click → the app opens. Menu → Settings… opens Settings; Widget → Disable → Save, then minimize → no widget; Enable → it is back on the next minimize.
13. Unpin the main window (Pin off) and put another app (e.g. a maximized browser) over it → the widget appears within about 2 s without taking focus. Click the main window (or its taskbar button) → it comes to the front and the widget hides at once. Move the other app so it covers less than half → widget hides. Pin on → the widget hides and stays hidden while pinned on the primary. Drag the main window onto a second monitor → the widget appears on the primary (saved spot, or bottom-right) within about 2 s; drag back → it hides. Settings → Widget → untick both checkboxes → Save → covering or moving the window no longer shows it; minimizing still does. Pick Disable → the checkboxes grey out.

## Contact / ownership

Built as a greenfield Cursor cloud agent project. No external services, auth, or cloud sync — all data is local to the Windows user profile.

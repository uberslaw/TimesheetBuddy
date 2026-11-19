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
│   ├── PercentBreakdown.cs         # Whole-percent shares summing to 100 (largest remainder) for the band
│   ├── JobCodeViewList.cs          # Visible job-code rows (active codes, or a saved order)
│   ├── TaskTypeSummary.cs          # Today's entries grouped by task + category + job code + ticket
│   ├── TicketRules.cs              # Ticket box: RITM/INC prefix, 03 / ServiceNow code match, Support category, "ticket · task" label
│   ├── RowTimeEdit.cs              # Pure row edits: remove time (newest finished first), move a row to another code
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
│   ├── ModifyTimeWindow.*          # Right-click Modify Time…: add or remove hours + minutes on a row
│   ├── MoveTimeWindow.*            # Right-click Move Time…: filterable job-code list + confirm
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
| Right-click → Modify Time… / Move Time… / Start|Continue / Change job code… / Assign time and Delete / Delete | `MainWindow` context menu + `ModifyTimeWindow` / `MoveTimeWindow` / `ChangeJobCodeWindow` / `AssignAndDeleteWindow` |
| Modify Time… (Add = same as Add time; Remove trims newest finished segments, running timer untouched, over-removal refused) | `MainWindow.PromptModifyTime` → `AppState.AddTimeToTaskType` / `RemoveTimeFromTaskType` → `Services/RowTimeEdit.Remove` |
| Move Time… (all of a row's time for the displayed day to another code; rows merge; running timer follows) | `MainWindow.MoveTimeMenu_Click` → `Views/MoveTimeWindow` → `AppState.MoveTaskTypeToJobCode` → `Services/RowTimeEdit.Move` |
| Soft-delete + same-day restore | `TimeEntry.IsDeleted` / `DeletedAtLocal` + `RestoreDeletedWindow` |
| Load older timesheet (history mode) | `AppState.ViewingDate` / `IsHistoryMode` + `LoadTimesheetWindow` |
| Pin / always on top | Pin toggle → `AppState.UpdatePromptSettings` |
| Add (job code + category + task name + ticket → new task, timer starts; Enter in Task name or Ticket) | `MainWindow.TryAddFromInputs` → `AppState.StartOrSwitch` (disabled in history). Picking a job code alone does not start anything |
| Ticket box (RITM/INC + digit → job code 03 / ServiceNow + category Support, never starts the timer) | `MainWindow.TicketBox_TextChanged` / `ApplyTicketDefaults` + `Services/TicketRules` |
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
| Timer widget (code - category, task, ▶ compact counter ■; drag to move, click to restore; Settings → Widget → Show when minimized) | `MainWindow.UpdateTimerWidget` → `Views/TimerWidgetWindow` + `Services/TimerWidgetLayout` |
| Widget also when the main window is buried behind other apps or on another monitor (Settings → Widget checkboxes) | `MainWindow.UpdateTimerWidget` → `Services/WindowVisibility` (Win32) → `Services/WidgetVisibilityRules` (`Decide`, `CoverageAccumulator`, `WidgetVisibilityGate`) |
| Widget corner arrows (dock to that corner of the widget's monitor work area) | `TimerWidgetWindow.Dock_Click` → `TimerWidgetLayout.CornerPosition` |
| Widget Start (resume last worked task type) / Stop (same stop as the main window) | `MainWindow.StartFromWidget` → `AppState.ResumeLastTask`; `MainWindow.StopFromUser` |
| Compact time counters (`1:05:03`, `5:03`, `14`; no zero hours/minutes, no leading zero) | `DayTotals.FormatCompact` (band rows, No Task Selected, Total, Time Remaining, big counter + widget via `AppState.ElapsedDisplay`, Today by task totals + segments, job-code view rows + footer, Assign time and Delete dialog) |

| Band % breakdown (each code's whole-percent share of Total, summing to 100) | `MainWindow.RefreshDayCodeTotals` → `Services/PercentBreakdown.Shares` |

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

**Today by task (main window).** Below the inputs, the compact tracker lists the viewing day's work with one row per task type: task name + category + job code. Name and category match case-insensitively; the code matches on `JobCodeId`, or on the code text when an entry has no id. Every non-deleted entry logged that day joins its row automatically (timer, manual, add-time, catch-up, lock-split slices, Break). Columns: expand arrow, task, category, job code, total (compact counter, including the live timer), and the number of segments. ▶/▼ expands indented child rows for each segment (local HH:mm + duration, chronological); parent rows on either side stay visible. Click a parent row to continue that task type (`AppState.StartOrSwitch`); the expand arrow does not start a timer. Right-click offers Modify Time… and Move Time… (see below), Start timer / Continue, Change job code…, Assign time and Delete… (reassign each portion’s clocks to other tasks via `TaskTypeReassign` / `AssignAndDeleteWindow`, then remove the source row), and Delete task. Soft-delete sets `TimeEntry.IsDeleted` + `DeletedAtLocal` on all of that type's entries for the viewing day (and stops the timer if that type is active). Deleted entries are filtered out of TaskTypeSummary, EntriesForDay, UnaccountedTime, WeekCalendar, and DayTotals. Purge removes soft-deleted entries whose `DeletedAtLocal.Date` is before today on load/save. Menu → Restore deleted tasks… restores today's groups. Rows are ordered by most recent start and only re-sort when entries change, not on the one-second tick. Check: `dotnet run --project tests/TaskTypeSummaryCheck`.

**Ticket box and Add (main window).** The task row reads [Task name][Ticket][Add][Stop]; Ticket (118 px, upper-cased, labelled like Task name) sits between the task name and the buttons, and the task name takes the rest. Tab order: Job code, Category, Task name, Ticket, Add, Stop. As you type a ticket, `TicketRules.Prefix` decides whether it is a ServiceNow ticket: the trimmed text must start with `RITM` or `INC` (any case) followed by a digit, so `INC0012345`, ` inc0012345`, `RITM0012345` match and `INCOMING`, `INC`, `REQ123`, `CHG0012345` do not. When the match first becomes true, or the prefix changes between RITM and INC, the job code is set to `TicketRules.FindServiceNowCode` (a code whose Code is exactly `03`; else a code whose trailing part is `03`, shown as "03 — …", preferring one named ServiceNow; else any code whose Code or Description contains SERVICE NOW / SERVICE-NOW / SERVICENOW, case, spaces and hyphens ignored) and the category to the existing "Support" (any case), or the text "Support" if that category does not exist. Without a matching code the selector is left alone and a muted hint appears under the row. Further typing in the same ticket does not re-apply, so you can change the code or category afterwards; clearing the ticket or typing a non-ticket reverts nothing. The auto-fill never starts or switches the timer, and if a row was loaded from the list it releases that row (job code unlocked) instead of renaming it. Add (or Enter in Task name or Ticket) calls `AppState.StartOrSwitch` with the job code, category, task name and ticket: any running task is stopped and saved, No Task Selected time ends, the new segment starts, and it appears in Today by task at once (a new row, or a new segment on an identical row). A task name or a job code is needed; with only a ticket, the ticket is used as the task name. The boxes keep their text after Add. The ticket is stored as `ticket` on each time entry and on the active session (trimmed; old settings.json files without it load as empty). It is part of the Today-by-task key, so the same task name with two tickets makes two rows; with no ticket the key is unchanged. It shows as `INC0012345 · task name` in the Task column and row tooltip, the Tracking status line, the widget task line and its Stop/Start tooltips, Weeks & export rows, the PDF Task column, the day log, Restore deleted tasks, the catch-up and Assign time and Delete targets, and the screen-lock label; Excel gets its own Ticket column after Notes. Continue (row click / Start timer), Modify Time, Move Time, Change job code, rename, resume from the widget, screen-lock splits and Assign time and Delete keep the ticket with the time. Favorites and Quick add clear the Ticket box. Check: `dotnet run --project tests/TicketRulesCheck`.

**History mode.** Menu → Load timesheet… opens a calendar (no future dates). Entering another day stops any active timer, sets `AppState.ViewingDate` / `IsHistoryMode`, shows a yellow banner (“Editing {date} — timer inactive” + Back to today), and disables Add/Stop. Row clicks open Add time instead of starting a timer. Manual and Add time entries land on `ViewingDate` (end at that day's clock time-of-day equivalent). `PromptScheduler` does not fire recheck/fifteen while `IsHistoryMode`. Menu → Back to today (or the banner button) calls `ExitHistoryMode()`.

**Timer widget.** Minimizing the main window (its button, the taskbar, Win+M) shows a 268×96 always-on-top widget that never takes focus or a taskbar slot. Line 1 is `<JobCode.Code> - <category>` (stored label trimmed to its code part when the code is not in the catalog, `No job code - <category>` without one), line 2 the task name, line 3 is ▶ Start, the same `ElapsedDisplay` as the main counter in `ActiveTimerBrush`, and ■ Stop. After a Stop it shows `Idle` / `No Task Selected` / the idle counter; with nothing ever stopped, `Idle` / `No active task` / `0`. It refreshes from `AppState.PropertyChanged`, driven by the main window's one-second tick. Stop runs the main window's stop path (`StopFromUser`, so No Task Selected starts) and is disabled while no task runs. Start is disabled while a task runs (and in history mode); otherwise it resumes the task type (name + category + job code) of the most recently started non-deleted, non-Break entry, today's first, falling back to the latest earlier day (`AppState.LatestWorkedEntry` / `ResumeLastTask`); with nothing to resume it opens the main window. Four faint outward arrows in the corners (brighter on hover, tooltips "Dock top-left" etc.) dock the widget 12 units in from that corner of the work area of the monitor it is on (Win32 `MonitorFromWindow` + `GetMonitorInfo`, converted to WPF units; falls back to the primary work area), so bottom corners sit above the taskbar, and save the spot through `SaveWidgetPosition`. Buttons and arrows handle their own mouse-down, so they never drag or open the app; Start/Stop sit in transparent slots that swallow presses while the button is disabled (WPF does not hit-test disabled controls, so the press would otherwise reach the widget). Anywhere else, press + move drags (`DragMove`, saved as `widgetLeft` / `widgetTop`, finite values only) and press + release without moving more than 3 units, or a double-click, restores the main window, which hides the widget; prompts do the same via `EnsureVisibleForPrompt`. On each show the position is pulled back onto the work area, or reset to bottom-right if its monitor is gone. Closing the main window closes it. Settings → Widget → Show when minimized (Enable / Disable, default Enable) is the same `showTimerWidget` setting and the master switch; Disable hides a showing widget at once. Under it, two checkboxes (default on, greyed while Disable is picked): "Also when the main window is covered by other windows" (`showTimerWidgetWhenCovered`) and "Also when the main window is on another monitor" (`showTimerWidgetOnOtherMonitor`); a settings.json without them reads them as on. Check: `dotnet run --project tests/TimerWidgetCheck`.

**When the widget shows.** The rule is "the user can't see the main window on the primary monitor". `MainWindow.UpdateTimerWidget` gathers a `MainWindowSnapshot` and `WidgetVisibilityRules.Decide` returns, in order: hide while closing, while the master is Disable, or while a modal dialog (any prompt, Settings, Day log…) is open (`ComponentDispatcher.IsThreadModal`); show if minimized; show if hidden (`Visibility != Visible` or `!IsVisible`; always counts with the master on); show if the "another monitor" option is on and the main window's centre (`GetWindowRect`) is outside the primary monitor's bounds (`MonitorFromPoint(0,0, MONITOR_DEFAULTTOPRIMARY)` + `GetMonitorInfo`; both in the thread's DPI coordinate space, a failed probe counts as on the primary); show if the "covered" option is on, the main window is not pinned (`Topmost`) and at least `CoveredThreshold` (50%) of it is covered; otherwise hide. Pinned on the primary and not minimized therefore always hides. Coverage (`WindowVisibility.CoveredFraction`) walks windows above the main HWND with `GetWindow(GW_HWNDPREV)` (at most 1024) and counts a window only if it is visible, not minimized, not DWM-cloaked (suspended UWP apps, other virtual desktops), not owned by this process (the widget, dialogs, the job-code view), not `WS_EX_TOOLWINDOW`, not a click-through (`WS_EX_LAYERED | WS_EX_TRANSPARENT`) or alpha-0 layered overlay, not a shell window (`Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`), and has a non-empty rect (`DWMWA_EXTENDED_FRAME_BOUNDS`, falling back to `GetWindowRect`). `CoverageAccumulator` keeps the uncovered part as disjoint rectangles and cuts each cover out, so overlaps are not double counted; the walk stops as soon as 50% is reached. It runs on StateChanged, Activated, Deactivated, LocationChanged, SizeChanged, IsVisibleChanged, pin toggle and settings changes, and on the existing one-second `_uiTimer` tick (other apps moving in front raise no event here). `WidgetVisibilityGate` adds hysteresis: minimized/hidden show at once and "nothing to show" hides at once (so clicking the main window hides it immediately), while covered / other monitor must hold on 2 consecutive ticks (`StablePollsToShow`) before the widget appears; events never advance that count. Clicking the widget and prompts (`EnsureVisibleForPrompt`) reset the gate, so with the main window on another monitor the widget comes back about 2 s after it is restored. Showing never activates the widget (`ShowActivated=False`, `Show()` only when not already visible, so a drag in progress is never repositioned) and never touches the main window's z-order.

**Modify Time (right-click on a Today-by-task row).** Replaces the old Add time… menu item. `ModifyTimeWindow` names the row (task, then job code or "No job code"), shows its current total, and asks Add time / Remove time (Add is the default) plus hours (0–99) and minutes (0–59), at least 1 minute. Add runs the old Add time path unchanged (`AppState.AddTimeToTaskType`: a finished `add-time` entry with `ManualMinutes`, ending now, or at the same clock time on a loaded older day). Remove calls `AppState.RemoveTimeFromTaskType` → `RowTimeEdit.Remove` on that row's entries for the displayed day: finished segments only, most recent first (by end time); a timer segment is shortened by moving `StartUtc` later, a manual one loses `ManualMinutes` (its start follows so the clock span still matches; if the cut leaves part of a minute it becomes an exact clock span with `ManualMinutes = 0`), and a segment that reaches zero is removed from `Entries` outright (not soft-deleted, so it does not show under Restore deleted tasks). The running timer's segment (the `ActiveSession` entry, or any open timer entry) is never touched and is not removable. Asking for more than the finished time is refused with "You can remove at most {compact} from this row ({h} h {m} min in whole minutes)…", both in the dialog and again in `RowTimeEdit`; it never silently caps. Afterwards the row, band, totals, job-code view and widget refresh on the next tick; a row emptied by Remove disappears. History-mode row clicks still open the plain `AddTimeWindow`, and the Quick add menu in history mode still adds time through it. No Task Selected is not a task row and has no menu. Check: `dotnet run --project tests/RowTimeEditCheck`.

**Move Time (right-click on a Today-by-task row).** For merging a row logged under the wrong, similar code. `MoveTimeWindow` shows the row (task, code, compact total, segment count), a filter box (type to narrow; ↑/↓ move the selection) and a list of active job codes sorted by code, without the row's own code (inactive codes are hidden). "No job code" is listed first when the displayed day already has a row without a code and the source row has one. Move… (or double-click) asks "Move {total} ({n} segments) from “task” (code) to {target}?"; No returns to the list. Yes calls `AppState.MoveTaskTypeToJobCode` → `RowTimeEdit.Move`: every non-deleted entry of that row on the displayed day gets the target's `JobCodeId` and `JobCodeText` (the code's `Display`, or empty for No job code); other days and other rows are untouched. Because Today by task keys on task + category + code, the row joins any existing row with the same task and category on the target code (they merge); otherwise it reappears under the new code. In the per-code band the source code's row loses that time (and disappears when nothing else is on it) and the target's row gains it; Total and Time Remaining do not change. If the running timer's entry is in the row, `ActiveSession.JobCodeId` / `JobCodeText` are re-pointed with the same entry and start time, so the timer keeps running without a stop or restart (no No Task Selected time is created). The expanded state and a row loaded in the editors follow the new key. "Change job code…" (older item, combo of active codes, no confirm) is still there and does the same re-pointing; Move Time adds the filter, the No job code target and the confirmation. Check: `dotnet run --project tests/RowTimeEditCheck`.

**Compact time display.** Every live on-screen counter goes through `DayTotals.FormatCompact`: hours > 0 → `H:MM:SS` (`1:05:03`, `12:00:07`, `26:10:00` past a day, total hours), else minutes > 0 → `M:SS` (`5:03`, `45:00`), else bare seconds (`14`, `7`, `0`). Seconds always show and only the inner parts keep two digits; fractions of a second are dropped. A negative span keeps a leading `-` (Time Remaining still floors at `0` once 7.5 h is logged, so it never goes negative today). Used by the band's per-code rows, No Task Selected, Total and Time Remaining (now with seconds), the big counter and the widget (both through `AppState.ElapsedDisplay`, so the still-working prompt's "Elapsed …" too), Today by task totals and expanded segment durations, the job-code view rows and Today footer, and the Assign time and Delete dialog (which mirrors a task row). The big counter is right-aligned with a `MinWidth` so the band does not shift as digits appear; the widget counter stays centred; band and job-code view times are right-aligned. Left alone on purpose: PDF/Excel export, Weeks & export and the day log (`WeekCalendar.FormatDuration` / their own `1h 05m` style), the screen-lock prompt's "locked for HH:MM:SS" (`LockIntervalSplit.FormatLockClock`), the add-time HH:MM inputs, saved data, and `DayTotals.FormatClock` / `FormatClockHm` / `FormatSummary` (no longer used by the UI). Check: `dotnet run --project tests/TimeDisplayCheck`.

**Band percentage breakdown.** Each per-code row in the green band has a third column right of its time: that code's share of the band's Total (logged work for the displayed day, live timer included), e.g. `50%`, in Consolas 11 and `MutedBrush`, right-aligned. The column is `Auto` with `MinWidth="34"` in the shared group `DayCodePercent` (8 px gap from the times), and the No Task Selected, Total and Time Remaining grids carry the same column empty so all times stay lined up; the label column (`*`) gives up the width, the big counter is unchanged. `PercentBreakdown.Shares` floors each share and hands the leftover points to the largest fractional parts (ties: larger time, then row order), so the shown values always add up to exactly 100; a tiny code can read `0%`. No percentage on Total (no 100%), Time Remaining or No Task Selected (not part of Total); none at all when Total is zero. Refreshed with the band on the one-second tick, in history mode too (the displayed day). The job-code view window has its own rows and shows no percentages. Example: 2:09:05 / 1:06:45 / 0:54:33 / 0:08:38 → 50% / 26% / 21% / 3%. Check: `dotnet run --project tests/PercentBreakdownCheck`.

**Screen lock.** If Windows locks the session while a timer is running, unlock (after at least 30 seconds) asks how to assign that time: Assign on the last-active row (job code, category, task name) keeps it there; Assign beside a job-code selector moves the lock interval onto that code. Moving the interval leaves the time before the lock on the original code and continues that code from the unlock moment, so the locked minutes are not counted twice. A lock under 30 seconds, or a lock while the timer is idle, does not prompt.

## Architecture notes

- **No MVVM framework** — code-behind + `AppState` (`INotifyPropertyChanged`) for simplicity.
- **Singletons on `App`**: `App.State`, `App.Scheduler`, `App.Store`.
- **PromptScheduler** ticks every second; only one modal prompt at a time (`RecheckOpen` / `FifteenOpen`).
- Catch-up measures the last N minutes (`FifteenMinuteIntervalMinutes`, same value the scheduler waits). Accounted time is the union of logged entries plus the running timer's elapsed overlap. A gap under 30 seconds counts as zero. If at least one minute is unaccounted, the assign-tasks dialog opens and the still-working dialog does not. If nothing is unaccounted and a timer is running, the existing still-working window opens instead. If nothing is unaccounted and no timer is running, no popup appears.
- The still-working window stays until the user chooses. Catch-up asks how to assign the whole unaccounted block: last active task, another task logged today, or a newly entered task. Not now dismisses without creating entries.
- Selecting a job code in the main window only fills the selector; **Add** starts the timer. Clicking a Today-by-task row still continues that task at once.

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
3. Pick a job code → nothing starts. Type a task name, click Add → a row appears under Today by task and the timer runs; Stop ends it.
4. Quick add a favorite → timer switches to that task.
5. Settings → set recheck to a short interval (e.g. 0h 5m) with an active task → popup appears; choose next interval.
6. Settings → set catch-up interval to 5 minutes. With a timer that has already been running for those 5 minutes, the still-working prompt appears and the assign-tasks dialog does not. With no timer and no entries in that window, the assign-tasks dialog shows the unaccounted minutes; Not now closes it without creating entries. Saving is blocked when the assigned minutes are 0 or more than the gap.
7. Day log shows entries; day notes persist after blur/refocus.
8. Close while a timer is running → the task is saved on today's list as a finished entry and does not resume after restart.
9. Run a task, Stop, run another, then click the first task's row under "Today by task" → it turns green, its total keeps counting, and its segment count goes from 1 to 2. Clicking it again does nothing. Drag the bottom edge to resize; the height comes back after restart.
10. Start a task and minimize (button, taskbar, Win+M) → the widget appears bottom-right with `code - category`, the task name and a ticking orange counter matching the main one; it does not take focus. Drag it elsewhere, restore, minimize again → same spot. Click it → the main window comes back and the widget hides. Stop the timer and minimize → `Idle` / `No Task Selected` / the idle counter (on a fresh launch with nothing stopped: `Idle` / `No active task` / `0`). With the widget showing, let a recheck or catch-up prompt fire → main window restores, widget hides, the dialog is on top. Close the app from the taskbar while minimized → the process exits.

11. Start a task, press Stop → status reads "No Task Selected", the big counter restarts from 0 and a muted italic "No Task Selected" row appears in the band; Total and Time Remaining do not move. Start a task within a minute → the row disappears (discarded). Stop again and wait over a minute, then let the catch-up interval fire → "{n} minutes were logged as No Task Selected…" appears (not the unaccounted dialog). Assign part of it → those minutes appear under Today by task and Total grows; the rest stays in the row. Not now → asked again next interval. All to Break → a Break row appears. Stop a task and close the app → on relaunch nothing is running and the idle time is still pending if it reached a minute.
12. Minimize with a task running → ▶ is greyed, ■ is live. Click ■ → the widget reads `No Task Selected` with a counting idle timer and the main window has not opened. Click ▶ → the last task resumes (its row turns green after restore) without opening the app. With no entries at all, ▶ opens the main window. Click each corner arrow → the widget jumps to that corner of the monitor it is on, clear of the taskbar; restore and minimize again → same corner. Press on a disabled ▶ → nothing happens (no open, no drag). Press on the text and drag → it moves; a plain click → the app opens. Menu → Settings… opens Settings; Widget → Disable → Save, then minimize → no widget; Enable → it is back on the next minimize.
13. Unpin the main window (Pin off) and put another app (e.g. a maximized browser) over it → the widget appears within about 2 s without taking focus. Click the main window (or its taskbar button) → it comes to the front and the widget hides at once. Move the other app so it covers less than half → widget hides. Pin on → the widget hides and stays hidden while pinned on the primary. Drag the main window onto a second monitor → the widget appears on the primary (saved spot, or bottom-right) within about 2 s; drag back → it hides. Settings → Widget → untick both checkboxes → Save → covering or moving the window no longer shows it; minimizing still does. Pick Disable → the checkboxes grey out.
14. Watch the counters: a fresh task's big counter reads `7`, `59`, then `1:00`; a row with 1 h 5 m reads `1:05:03`; nothing shows `00:`. Time Remaining shows seconds (e.g. `7:29:46`). Minimize → the widget counter matches the big one and stays centred. The job-code view rows and footer use the same format; PDF/Excel exports and the day log are unchanged.
15. Right-click a Today-by-task row → the first item reads "Modify Time…". Add 0:10 → the row grows by 10 minutes and gets a new segment (same as the old Add time). Remove 0:05 from a row with finished segments → the newest finished segment shrinks by 5 minutes (expand ▶ to see it); remove its whole length → that segment disappears. On the running row, Remove more than its finished time → a message says the maximum removable and nothing changes; the live counter keeps ticking. Remove a row's entire finished time when nothing runs on it → the row disappears and Total / Time Remaining / the code's band row drop by that amount.
16. Log a few minutes on task A under code DEV and on task A under code MTG (same category). Right-click the DEV row → Move Time… → the list has no DEV entry; type "mt" → only matching codes remain. Pick MTG → "Move {total} ({n} segments) from “A” (DEV …) to MTG …?" → Yes → the DEV row is gone, the MTG row's total and segment count include it, the band no longer lists DEV (if nothing else used it) and Total is unchanged. Repeat with the running task's row → the big counter and widget keep counting without a reset, the row stays green under the new code, and the widget header shows the new code. Load an older day and move a row there → today's rows are unchanged.

17. Type `INC0012345` in Ticket → Job code becomes 03 — SERVICE-NOW, Category becomes Support, and the timer has not started (big counter and status unchanged). Change the category to General, keep typing digits → it stays General. Enter a task name, click Add → a row `INC0012345 · task name` appears under Today by task, turns green and the timer runs; the status reads `Tracking: INC0012345 · task name` and the boxes keep their text. Type `RITM0012345` and click Add → a separate row starts and the first stops. Type `INCOMING` → nothing changes. With no 03 / ServiceNow code in Settings, typing a ticket leaves the job code alone and shows the hint. Export the week to Excel → the Ticket column has the ticket.
18. Log time on three or four codes → each band row shows a grey `NN%` right of its time, the column lines up, the values add to 100, Total / Time Remaining / No Task Selected show none, and the big counter has not moved. Let the timer run → the running code's share creeps up on the tick without the rows shifting. On a fresh day with nothing logged → no percentages.

## Contact / ownership

Built as a greenfield Cursor cloud agent project. No external services, auth, or cloud sync — all data is local to the Windows user profile.

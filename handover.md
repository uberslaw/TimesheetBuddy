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
│   ├── PromptScheduler.cs          # Recheck + catch-up timers
│   ├── DayTotals.cs                # Today’s totals, including a live timer
│   ├── JobCodeViewList.cs          # Visible job-code rows (active codes, or a saved order)
│   ├── TaskTypeSummary.cs          # Today's entries grouped by task + category + job code
│   ├── LockIntervalSplit.cs        # Split a lock interval off the running entry
│   ├── ScreenLockTracker.cs        # Decide whether an unlock should prompt
│   └── ScreenLockWatcher.cs        # SessionLock / SessionUnlock → prompt
├── Views/
│   ├── SettingsWindow.*            # Job codes, favorites, categories, prompts
│   ├── RecheckWindow.*             # “Still working on X?”
│   ├── FifteenMinuteWindow.*       # Assign tasks to unaccounted minutes (Not now cancels)
│   ├── DayLogWindow.*              # Viewing-day entries + day notes
│   ├── ManualEntryWindow.*         # Manual add from day log
│   ├── AddTimeWindow.*             # HH:MM add-time dialog (no live timer)
│   ├── AssignAndDeleteWindow.*     # Reassign each portion, then remove the task row
│   ├── RestoreDeletedWindow.*      # Same-day restore of soft-deleted task types
│   ├── LoadTimesheetWindow.*       # Calendar picker for history mode
│   ├── QuickAddJobCodeWindow.*     # Menu → Add job code (code + description)
│   ├── JobCodeViewWindow.*         # Alternate row-per-code timer view
│   ├── AddJobCodeWindow.*          # Job-code view: add existing or create one
│   └── LockPromptWindow.*          # Assign lock time to last active task or another code
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
| Stop timer | Stop button → `AppState.StopActive` |
| Quick-add favorites menu | `MainWindow.QuickAdd_Click` |
| Menu → Add job code… | `Views/QuickAddJobCodeWindow` → `AppState.AddJobCode` |
| Settings (codes, favorites, categories, intervals) | `Views/SettingsWindow` |
| Still-working recheck popup | `PromptScheduler` → `Views/RecheckWindow` (skipped in history) |
| Catch-up prompt (only when minutes are unaccounted; Not now dismisses) | `PromptScheduler` → `UnaccountedTime` → `Views/FifteenMinuteWindow` or `RecheckWindow` |
| Day log + free-form day notes | `Views/DayLogWindow` (uses `ViewingDate`) |
| Manual time entry | `Views/ManualEntryWindow` (lands on `ViewingDate`) |
| Job code view (switch from the Menu; Compact tracker returns) | `Views/JobCodeViewWindow` |
| Screen-lock prompt while a timer is running | `ScreenLockWatcher` → `Views/LockPromptWindow` |

## Data

| File | Contents |
|------|----------|
| `%LocalAppData%\TimesheetBuddy\settings.json` | Job codes, favorites, categories, prompt prefs, window position, job-code view order (`jobCodeViewIds`), all time entries, active session |
| `%LocalAppData%\TimesheetBuddy\day-notes-yyyyMMdd.txt` | Per-day free-text notes from the day log |
| `%LocalAppData%\TimesheetBuddy\crash.log` | Unhandled exceptions. Startup failures also show a dialog with this path |

Defaults seed a few job codes (`ADMIN`, `DEV`, `MTG`, `SUP`) and sample favorites on first launch.

Closing the app while a timer is running saves that task onto the current day's list as a finished entry (end time set). It does not resume after restart.

**Job code view.** Menu → Job code view opens a taller window with one row per code. Click a row to run the timer on that code (the row turns green); Stop ends it for the whole view. Each row shows today’s time for that code, including the live timer, and the footer total is every entry logged today. Add inserts an existing code or creates a code plus description. Remove only changes the saved row list (`AppSettings.JobCodeViewIds`); it does not delete time entries. Until the user adds or removes a row, the view lists the active codes.

**Today by task (main window).** Below the inputs, the compact tracker lists the viewing day's work with one row per task type: task name + category + job code. Name and category match case-insensitively; the code matches on `JobCodeId`, or on the code text when an entry has no id. Every non-deleted entry logged that day joins its row automatically (timer, manual, add-time, catch-up, lock-split slices, Break). Columns: expand arrow, task, category, job code, total (`hh:mm:ss`, including the live timer), and the number of segments. ▶/▼ expands indented child rows for each segment (local HH:mm + duration, chronological); parent rows on either side stay visible. Click a parent row to continue that task type (`AppState.StartOrSwitch`); the expand arrow does not start a timer. Right-click offers Add time… (finished manual segment, no live timer), Start timer / Continue, Assign time and Delete… (reassign each portion’s clocks to other tasks via `TaskTypeReassign` / `AssignAndDeleteWindow`, then remove the source row), and Delete task. Soft-delete sets `TimeEntry.IsDeleted` + `DeletedAtLocal` on all of that type's entries for the viewing day (and stops the timer if that type is active). Deleted entries are filtered out of TaskTypeSummary, EntriesForDay, UnaccountedTime, WeekCalendar, and DayTotals. Purge removes soft-deleted entries whose `DeletedAtLocal.Date` is before today on load/save. Menu → Restore deleted tasks… restores today's groups. Rows are ordered by most recent start and only re-sort when entries change, not on the one-second tick. Check: `dotnet run --project tests/TaskTypeSummaryCheck`.

**History mode.** Menu → Load timesheet… opens a calendar (no future dates). Entering another day stops any active timer, sets `AppState.ViewingDate` / `IsHistoryMode`, shows a yellow banner (“Editing {date} — timer inactive” + Back to today), and disables Start/Stop/job-code auto-start. Row clicks open Add time instead of starting a timer. Manual and Add time entries land on `ViewingDate` (end at that day's clock time-of-day equivalent). `PromptScheduler` does not fire recheck/fifteen while `IsHistoryMode`. Menu → Back to today (or the banner button) calls `ExitHistoryMode()`.

**Screen lock.** If Windows locks the session while a timer is running, unlock (after at least 30 seconds) asks how to assign that time: Assign on the last-active row (job code, category, task name) keeps it there; Assign beside a job-code selector moves the lock interval onto that code. Moving the interval leaves the time before the lock on the original code and continues that code from the unlock moment, so the locked minutes are not counted twice. A lock under 30 seconds, or a lock while the timer is idle, does not prompt.

## Architecture notes

- **No MVVM framework** — code-behind + `AppState` (`INotifyPropertyChanged`) for simplicity.
- **Singletons on `App`**: `App.State`, `App.Scheduler`, `App.Store`.
- **PromptScheduler** ticks every second; only one modal prompt at a time (`RecheckOpen` / `FifteenOpen`).
- Catch-up measures the last N minutes (`FifteenMinuteIntervalMinutes`, same value the scheduler waits). Accounted time is the union of logged entries plus the running timer's elapsed overlap. A gap under 30 seconds counts as zero. If at least one minute is unaccounted, the assign-tasks dialog opens and the still-working dialog does not. If nothing is unaccounted and a timer is running, the existing still-working window opens instead. If nothing is unaccounted and no timer is running, no popup appears.
- The still-working window stays until the user chooses. Catch-up can be dismissed with Not now, which does not create entries. Save is allowed only when the assigned minutes are positive and do not exceed the unaccounted total (assigning less is allowed).
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

## Contact / ownership

Built as a greenfield Cursor cloud agent project. No external services, auth, or cloud sync — all data is local to the Windows user profile.

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
│   └── PromptScheduler.cs          # Recheck + catch-up timers
├── Views/
│   ├── SettingsWindow.*            # Job codes, favorites, categories, prompts
│   ├── RecheckWindow.*             # “Still working on X?”
│   ├── FifteenMinuteWindow.*       # Assign tasks to unaccounted minutes (Not now cancels)
│   ├── DayLogWindow.*              # Today’s entries + day notes
│   └── ManualEntryWindow.*         # Manual add from day log
├── README.md
└── handover.md                     # This file
```

Empty `Assets/` and `Converters/` folders exist for future use.

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
| Window size (~2× tray width, ~4× taskbar height) | `MainWindow.ApplyCompactSize()` |
| Pin / always on top | Pin toggle → `AppState.UpdatePromptSettings` |
| Start timer from job code / task / Enter | `MainWindow` selection + Start handlers |
| Stop timer | Stop button → `AppState.StopActive` |
| Quick-add favorites menu | `MainWindow.QuickAdd_Click` |
| Settings (codes, favorites, categories, intervals) | `Views/SettingsWindow` |
| Still-working recheck popup | `PromptScheduler` → `Views/RecheckWindow` |
| Catch-up prompt (only when minutes are unaccounted; Not now dismisses) | `PromptScheduler` → `UnaccountedTime` → `Views/FifteenMinuteWindow` or `RecheckWindow` |
| Day log + free-form day notes | `Views/DayLogWindow` |
| Manual time entry | `Views/ManualEntryWindow` |

## Data

| File | Contents |
|------|----------|
| `%LocalAppData%\TimesheetBuddy\settings.json` | Job codes, favorites, categories, prompt prefs, window position, all time entries, active session |
| `%LocalAppData%\TimesheetBuddy\day-notes-yyyyMMdd.txt` | Per-day free-text notes from the day log |
| `%LocalAppData%\TimesheetBuddy\crash.log` | Unhandled exceptions. Startup failures also show a dialog with this path |

Defaults seed a few job codes (`ADMIN`, `DEV`, `MTG`, `SUP`) and sample favorites on first launch.

Closing the app while a timer is running saves that task onto the current day's list as a finished entry (end time set). It does not resume after restart.

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
- Per-job-code daily totals / reporting view
- Wire empty `Assets/` (app icon) and `Converters/` if binding converters are added

## Smoke-test checklist

1. Launch → compact window appears near bottom-right; Pin keeps it on top.
2. Settings → add a job code and a favorite → Save.
3. Pick job code → timer starts; Stop ends it.
4. Quick add a favorite → timer switches to that task.
5. Settings → set recheck to a short interval (e.g. 0h 5m) with an active task → popup appears; choose next interval.
6. Settings → set catch-up interval to 5 minutes. With a timer that has already been running for those 5 minutes, the still-working prompt appears and the assign-tasks dialog does not. With no timer and no entries in that window, the assign-tasks dialog shows the unaccounted minutes; Not now closes it without creating entries. Saving is blocked when the assigned minutes are 0 or more than the gap.
7. Day log shows entries; day notes persist after blur/refocus.
8. Close while a timer is running → the task is saved on today's list as a finished entry and does not resume after restart.

## Contact / ownership

Built as a greenfield Cursor cloud agent project. No external services, auth, or cloud sync — all data is local to the Windows user profile.

# Timesheet Buddy 3.0

A compact always-on-top WPF (.NET 10) timesheet helper for tracking time against job codes throughout the day.

## What it does

- **Compact tracker window** sized from your tray (~2× tray width; drag the bottom edge to change the height), draggable, pinable always-on-top
- **Live timer** for the active task — starts when you click **Add** (or press Enter) with a job code and/or task name, optionally with a ServiceNow ticket
- **Settings** to manage job codes, favorite tasks, categories, recheck interval, the catch-up popup interval, and where your data is stored
- **Quick add** menu of favorite tasks (grouped by category)
- **Still-working recheck** popup that asks if you are still on the active task, with hours/minutes until the next check
- **Catch-up popup** (“What have you done for the last 15 minutes?”) that stays until you respond — add rows with comments, job codes, minutes, and common tasks
- **Day log** with today’s entries plus a free-form notes area
- **App Track** (optional, off by default): tick **Menu → App Track** to log how long each program is in the foreground, by program name only (never window titles, web pages, documents or email). It pauses after 5 minutes without input and while the screen is locked. **Menu → App usage…** shows each day's programs with time and percentage. It is kept apart from your timesheet and never appears in totals or exports.

Data is stored locally in `%LocalAppData%\TimesheetBuddy\` (`settings.json` plus `day-notes-*.txt` and, with App Track on, `app-usage-*.json`, a daily snapshot in `backups\` and the change log in `journal\`). To keep it somewhere else (another drive, a OneDrive folder), use **Menu → Settings… → Data → Browse…**; your data is copied there and checked. Tick **Move my data** to delete the old copy afterwards; if the folder already has data (another PC's), choose **Merge** to combine both. The crash log and this PC's id (`client.json`) always stay in `%LocalAppData%\TimesheetBuddy\`.

- **Import data…** (Menu, or Settings → Data) brings entries, day notes and App Track usage in from a backup, a OneDrive conflict copy, another folder or an earlier Timesheet Buddy release, with a preview first. Older releases' files are also upgraded automatically when the app opens them (the original is kept as `settings.json.bak-…`).
- **Change log…** (Menu) lists recent changes from every PC using the data folder, with the PC's name.
- **Two PCs at once:** with a shared OneDrive folder, if the app is open on another PC you are asked "Are you active on this one?" (never while the screen is locked). Yes lets you keep any of the other PC's entries for today, then the other PC closes without saving. Not now leaves both open; their changes are merged on save. Detection takes about 20 seconds plus OneDrive's sync time.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Run

```bash
cd TimesheetBuddy
dotnet restore
dotnet build
dotnet run
```

Or open `TimesheetBuddy/TimesheetBuddy.csproj` in Visual Studio 2022+/2026 and press F5.

## Everyday use

1. Pin the window if you want it always visible.
2. Choose a **job code** and/or type/pick a **task name** (and a **Ticket** if there is one), then **Add**. Add puts the task on today's list and starts its timer; picking a job code on its own does not start anything. Typing a ticket that starts with `INC` or `RITM` plus a digit sets the job code to 03 (ServiceNow) and the category to Support.
3. Use **+ Quick add** for favorite tasks.
4. Open **Menu → Settings…** to edit job codes, favorites, categories, and prompt intervals.
5. Open **Menu → Day log…** for today’s log and day notes; **Add manual** there adds a manual entry.
   **Menu → App Track** (the first item) switches program tracking on or off; **App usage…** below it shows the results.
6. When the recheck or 15-minute popup appears, answer it — the catch-up window will not dismiss until you save.

## Notes

WPF targets Windows only (`net10.0-windows`). This project will not run on macOS/Linux.

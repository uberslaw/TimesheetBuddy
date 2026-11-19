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

Data is stored locally in `%LocalAppData%\TimesheetBuddy\` (`settings.json` plus `day-notes-*.txt`). To keep it somewhere else (another drive, a synced folder), use **Menu → Settings… → Data → Browse…**; your data is copied there and the old copy is left in place. The crash log always stays in `%LocalAppData%\TimesheetBuddy\`.

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
6. When the recheck or 15-minute popup appears, answer it — the catch-up window will not dismiss until you save.

## Notes

WPF targets Windows only (`net10.0-windows`). This project will not run on macOS/Linux.

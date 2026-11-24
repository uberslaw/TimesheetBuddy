using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Soft-deleted task-type group on the current calendar day (restorable until midnight).
/// </summary>
public sealed record DeletedTaskGroup(
    string Key,
    string TaskName,
    string Category,
    Guid? JobCodeId,
    string JobCodeText,
    DateTime DeletedAtLocal,
    IReadOnlyList<Guid> EntryIds,
    string Ticket = "");

public sealed class AppState : INotifyPropertyChanged
{
    private const string IdleStatus = "Idle — pick a job code or task, then Add";

    private readonly SettingsStore _store;
    private AppSettings _settings;
    private TimeSpan _elapsed;
    private string _statusText = IdleStatus;
    private bool _isRunning;
    private DateTime _viewingDate = DateTime.Today;

    public AppState(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        NormalizeNoTaskOnLoad();
        PurgeExpiredDeletedEntries(persist: true);
        JobCodes = new ObservableCollection<JobCode>(_settings.JobCodes);
        FavoriteTasks = new ObservableCollection<FavoriteTask>(_settings.FavoriteTasks);
        Categories = new ObservableCollection<string>(_settings.Categories);
        TodayEntries = new ObservableCollection<TimeEntry>(
            EntriesForDay(DateTime.Today).Reverse());
        _store.ExternalChangesMerged += _ => ReloadFromSettings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler? ViewingDateChanged;

    public ObservableCollection<JobCode> JobCodes { get; }
    public ObservableCollection<FavoriteTask> FavoriteTasks { get; }
    public ObservableCollection<string> Categories { get; }
    public ObservableCollection<TimeEntry> TodayEntries { get; }

    public AppSettings Settings => _settings;

    /// <summary>Change log for this install; null in checks.</summary>
    public ChangeJournal? Journal { get; set; }

    private (string From, string To, string Key, DateTime AtUtc)? _pendingRename;

    private void Log(string action, string summary, Guid? entryId = null)
    {
        FlushRename(force: true);
        Journal?.Append(action, summary + DaySuffix, entryId);
    }

    /// <summary>Live renames (one per keystroke) become one journal line once typing pauses.</summary>
    private void NoteRename(string fromKey, string fromLabel, string newKey, string to)
    {
        if (_pendingRename is { } pending && pending.Key == fromKey)
            fromLabel = pending.From;
        else
            FlushRename(force: true);
        _pendingRename = (fromLabel, to, newKey, DateTime.UtcNow);
    }

    private void FlushRename(bool force)
    {
        if (_pendingRename is not { } pending) return;
        if (!force && DateTime.UtcNow - pending.AtUtc < TimeSpan.FromSeconds(5)) return;
        _pendingRename = null;
        Journal?.Append(ChangeJournal.Actions.Modify, $"Renamed {pending.From} to {pending.To}" + DaySuffix);
    }

    private string DaySuffix => IsHistoryMode ? $" (on {ViewingDate:ddd d MMM yyyy})" : string.Empty;

    private string RowLabel(string key)
    {
        var entry = RowTimeEdit.RowEntries(_settings.Entries, ViewingDate, key).FirstOrDefault();
        return entry is null ? key : ChangeJournal.Describe(entry.TaskName, entry.JobCodeText, entry.Ticket);
    }

    /// <summary>
    /// X for the catch-up prompt. Same floor the scheduler uses; not a second interval.
    /// </summary>
    public int CatchUpIntervalMinutes =>
        Math.Max(5, _settings.FifteenMinuteIntervalMinutes);

    /// <summary>Local calendar day currently shown in the task list / day log.</summary>
    public DateTime ViewingDate
    {
        get => _viewingDate.Date;
        private set
        {
            var next = value.Date;
            if (_viewingDate.Date == next) return;
            _viewingDate = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsHistoryMode));
            OnPropertyChanged(nameof(ViewingDateLabel));
            ViewingDateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsHistoryMode => ViewingDate != DateTime.Today;

    public string ViewingDateLabel =>
        IsHistoryMode
            ? $"Editing {ViewingDate:ddd d MMM yyyy} — timer inactive"
            : "Today";

    public ActiveSession? ActiveSession
    {
        get => _settings.ActiveSession;
        private set
        {
            _settings.ActiveSession = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(ActiveTaskLabel));
            OnPropertyChanged(nameof(ActiveJobCodeLabel));
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
        }
    }

    public TimeSpan Elapsed
    {
        get => _elapsed;
        private set
        {
            _elapsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ElapsedDisplay));
        }
    }

    public string ElapsedDisplay => DayTotals.FormatCompact(Elapsed);

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string ActiveTaskLabel => ActiveSession is { } session &&
                                     TicketRules.Label(session.Ticket, session.TaskName) is { Length: > 0 } label
        ? label
        : "No active task";

    public string ActiveJobCodeLabel => ActiveSession?.JobCodeText is { Length: > 0 } code
        ? code
        : "—";

    /// <summary>Start of the running "No Task Selected" timer, or null while a task runs or nothing was stopped.</summary>
    public DateTime? NoTaskStartedUtc => ActiveSession is null ? _settings.NoTaskStartedUtc : null;

    public bool IsNoTaskRunning => NoTaskStartedUtc is not null;

    /// <summary>
    /// While no task runs, <see cref="Elapsed"/> is the "No Task Selected" timer, so the
    /// main counter and the widget show it; <see cref="IsRunning"/> stays false.
    /// </summary>
    public void Tick()
    {
        FlushRename(force: false);
        if (ActiveSession is null)
        {
            var idle = NoTaskStartedUtc is DateTime started ? DateTime.UtcNow - started : TimeSpan.Zero;
            Elapsed = idle < TimeSpan.Zero ? TimeSpan.Zero : idle;
            IsRunning = false;
            return;
        }

        Elapsed = DateTime.UtcNow - ActiveSession.StartedUtc;
        IsRunning = true;
    }

    public void RestoreSessionIfAny()
    {
        if (IsHistoryMode)
        {
            StatusText = ViewingDateLabel;
            return;
        }

        if (ActiveSession is null)
        {
            StatusText = NotTrackingStatus();
            return;
        }

        Tick();
        StatusText = $"Tracking: {ActiveTaskLabel}";
    }

    public void StartOrSwitch(string taskName, string category, JobCode? jobCode, string? ticket = null)
    {
        if (IsHistoryMode) return;
        if (string.IsNullOrWhiteSpace(taskName) && jobCode is null)
            return;

        var name = !string.IsNullOrWhiteSpace(taskName) ? taskName
            : !string.IsNullOrWhiteSpace(jobCode?.Description) ? jobCode.Description
            : jobCode?.Code ?? "Untitled task";

        StartOrSwitch(name, category, jobCode?.Id, jobCode?.Display ?? string.Empty, ticket);
    }

    /// <summary>
    /// Starts a task type logged earlier. The code id and text are kept as given, so the new
    /// segment groups with the earlier ones even when that code was since removed.
    /// </summary>
    public void StartOrSwitch(string taskName, string category, Guid? jobCodeId, string jobCodeText, string? ticket = null)
    {
        if (IsHistoryMode) return;
        if (string.IsNullOrWhiteSpace(taskName) && jobCodeId is null && string.IsNullOrWhiteSpace(jobCodeText))
            return;

        var now = DateTime.UtcNow;
        StopActive(save: false, startNoTask: false, now);
        EndNoTask(now);

        var name = string.IsNullOrWhiteSpace(taskName)
            ? (string.IsNullOrWhiteSpace(jobCodeText) ? "Untitled task" : jobCodeText.Trim())
            : taskName.Trim();
        var codeText = jobCodeText?.Trim() ?? string.Empty;
        var ticketText = TicketRules.Normalize(ticket);

        var entry = new TimeEntry
        {
            StartUtc = now,
            TaskName = name,
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
            JobCodeId = jobCodeId,
            JobCodeText = codeText,
            Ticket = ticketText,
            Source = "timer"
        };

        _settings.Entries.Add(entry);
        ActiveSession = new ActiveSession
        {
            EntryId = entry.Id,
            TaskName = name,
            Category = entry.Category,
            JobCodeId = jobCodeId,
            JobCodeText = codeText,
            Ticket = ticketText,
            StartedUtc = entry.StartUtc
        };

        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Start, "Started " + ChangeJournal.Describe(name, codeText, ticketText), entry.Id);
        Tick();
        StatusText = $"Tracking: {ActiveTaskLabel}";
    }

    /// <summary>
    /// A user stop: ends the running task and starts the "No Task Selected" timer from the same moment.
    /// </summary>
    public void StopActive(bool save) => StopActive(save, startNoTask: true, DateTime.UtcNow);

    /// <summary>
    /// App close: finishes a running task and any running "No Task Selected" time (kept when it
    /// reached a minute) without starting new idle time, so nothing resumes on the next launch.
    /// </summary>
    public void StopAllForShutdown()
    {
        FlushRename(force: true);
        var now = DateTime.UtcNow;
        StopActive(save: false, startNoTask: false, now);
        EndNoTask(now);
        Persist();
    }

    private void StopActive(bool save, bool startNoTask, DateTime nowUtc)
    {
        if (ActiveSession is null) return;

        var entry = _settings.Entries.FirstOrDefault(e => e.Id == ActiveSession.EntryId);
        if (entry is not null && entry.EndUtc is null)
            entry.EndUtc = nowUtc;
        Log(ChangeJournal.Actions.Stop,
            $"Stopped {ChangeJournal.Describe(ActiveSession.TaskName, ActiveSession.JobCodeText, ActiveSession.Ticket)} after " +
            ChangeJournal.FormatDuration(nowUtc - ActiveSession.StartedUtc),
            ActiveSession.EntryId);

        if (startNoTask)
            _settings.NoTaskStartedUtc ??= nowUtc;
        ActiveSession = null;
        OnPropertyChanged(nameof(IsNoTaskRunning));
        Tick();
        StatusText = NotTrackingStatus();
        RefreshToday();
        if (save) Persist();
    }

    /// <summary>Closes the running "No Task Selected" time; a run under a minute is dropped.</summary>
    private void EndNoTask(DateTime nowUtc)
    {
        if (_settings.NoTaskStartedUtc is null) return;

        NoTaskTime.Close(_settings.UnassignedSegments, _settings.NoTaskStartedUtc, nowUtc);
        _settings.NoTaskStartedUtc = null;
        OnPropertyChanged(nameof(IsNoTaskRunning));
    }

    /// <summary>Unassigned time on offer in the assignment dialog (pending, plus the running run once it reaches a minute).</summary>
    public IReadOnlyList<UtcInterval> OpenNoTaskTime(DateTime? nowUtc = null) =>
        NoTaskTime.Open(
            _settings.UnassignedSegments,
            NoTaskStartedUtc,
            _settings.Entries,
            nowUtc ?? DateTime.UtcNow);

    /// <summary>"No Task Selected" time on a day, including the running timer from its first second.</summary>
    public TimeSpan NoTaskTotalForDay(DateTime dayLocal, DateTime nowUtc) =>
        NoTaskTime.TotalForDay(
            NoTaskTime.Open(
                _settings.UnassignedSegments,
                NoTaskStartedUtc,
                _settings.Entries,
                nowUtc,
                includeShortRun: true),
            dayLocal);

    /// <summary>
    /// Logs the slices inside the unassigned spans (latest first) and removes that time from
    /// pending. A running "No Task Selected" timer of a minute or more is closed at now first and
    /// restarts from now. Returns null on success, otherwise why nothing was saved.
    /// </summary>
    public string? AssignNoTaskTime(IReadOnlyList<CatchUpSlice> slices)
    {
        var now = DateTime.UtcNow;
        if (NoTaskStartedUtc is DateTime started && now - started >= NoTaskTime.MinimumRun)
        {
            NoTaskTime.Close(_settings.UnassignedSegments, started, now);
            _settings.NoTaskStartedUtc = now;
        }

        var open = NoTaskTime.Open(_settings.UnassignedSegments, null, _settings.Entries, now);
        var error = NoTaskTime.Validate(slices, open);
        if (error is not null)
        {
            Persist();
            return error;
        }

        var placed = NoTaskTime.Place(slices, open).ToList();
        _settings.Entries.AddRange(placed);
        _settings.UnassignedSegments = NoTaskTime.Remaining(_settings.UnassignedSegments, _settings.Entries, now);
        Tick();
        RefreshToday();
        Persist();
        foreach (var e in placed)
            Log(ChangeJournal.Actions.Add,
                $"No Task Selected time assigned: {ChangeJournal.Describe(e.TaskName, e.JobCodeText, e.Ticket)} {ChangeJournal.FormatDuration(e.Duration)}", e.Id);
        return null;
    }

    /// <summary>The open No Task Selected spans the Today-by-task row shows for <see cref="ViewingDate"/>.</summary>
    public IReadOnlyList<UtcInterval> NoTaskSpansForViewingDay(DateTime? nowUtc = null) =>
        NoTaskTime.ForDay(_settings.UnassignedSegments, NoTaskStartedUtc, _settings.Entries, ViewingDate,
            nowUtc ?? DateTime.UtcNow);

    /// <summary>
    /// Move Time… on the No Task Selected row: the viewing day's open idle time becomes entries for the
    /// target at their own clock times and leaves pending. Returns null on success, otherwise why nothing changed.
    /// </summary>
    public string? MoveNoTaskTime(NoTaskTime.MoveTarget target)
    {
        var result = NoTaskTime.MoveDay(_settings.UnassignedSegments, NoTaskStartedUtc, _settings.Entries,
            ViewingDate, DateTime.UtcNow, target);
        if (!result.Applied)
            return result.Error ?? "Could not move that time.";

        _settings.Entries.AddRange(result.Added);
        ApplyNoTaskResult(result);
        var to = ChangeJournal.Describe(target.TaskName, target.JobCodeText, target.Ticket);
        Log(ChangeJournal.Actions.Move, $"Moved {ChangeJournal.FormatDuration(result.Amount)} of {NoTaskTime.Label} time to {to}");
        foreach (var e in result.Added)
            Journal?.Append(ChangeJournal.Actions.Add,
                $"{NoTaskTime.Label} time moved: {to} {ChangeJournal.FormatDuration(e.Duration)}" + DaySuffix, e.Id);
        return null;
    }

    /// <summary>
    /// Discards the viewing day's open No Task Selected time, newest first (null = all of it). Nothing is
    /// logged as work. Returns null on success, otherwise why nothing changed.
    /// </summary>
    public string? DiscardNoTaskTime(TimeSpan? amount)
    {
        var result = NoTaskTime.DiscardFromDay(_settings.UnassignedSegments, NoTaskStartedUtc, _settings.Entries,
            ViewingDate, DateTime.UtcNow, amount);
        if (!result.Applied)
            return result.Error ?? "Could not discard that time.";

        ApplyNoTaskResult(result);
        Log(ChangeJournal.Actions.Delete, $"Discarded {ChangeJournal.FormatDuration(result.Amount)} of {NoTaskTime.Label} time");
        return null;
    }

    private void ApplyNoTaskResult(NoTaskTime.DayEditResult result)
    {
        _settings.UnassignedSegments = result.Pending;
        if (ActiveSession is null)
            _settings.NoTaskStartedUtc = result.RunningStartUtc;
        Tick();
        RefreshToday();
        Persist();
    }

    private string NotTrackingStatus()
    {
        if (IsHistoryMode) return ViewingDateLabel;
        return NoTaskStartedUtc is null ? IdleStatus : NoTaskTime.Label;
    }

    /// <summary>
    /// A "No Task Selected" start still saved here means the app did not close normally; its end
    /// is unknown, so it is dropped rather than resumed. Malformed segments are dropped too.
    /// </summary>
    private void NormalizeNoTaskOnLoad()
    {
        _settings.UnassignedSegments ??= [];
        var before = _settings.UnassignedSegments.Count;
        _settings.UnassignedSegments.RemoveAll(s => s is null || s.EndUtc <= s.StartUtc);
        var changed = before != _settings.UnassignedSegments.Count || _settings.NoTaskStartedUtc is not null;
        _settings.NoTaskStartedUtc = null;
        if (changed)
            _store.Save(_settings);
    }

    public void AddFifteenMinuteRows(IEnumerable<TimeEntry> rows)
    {
        foreach (var row in rows)
        {
            if (row.ManualMinutes <= 0 && string.IsNullOrWhiteSpace(row.Notes) &&
                string.IsNullOrWhiteSpace(row.TaskName))
                continue;

            row.Source = "fifteen";
            if (row.StartUtc == default)
            {
                row.StartUtc = DateTime.UtcNow.AddMinutes(-row.ManualMinutes);
                row.EndUtc = DateTime.UtcNow;
            }

            _settings.Entries.Add(row);
            Log(ChangeJournal.Actions.Add,
                $"Catch-up: {ChangeJournal.Describe(row.TaskName, row.JobCodeText, row.Ticket)} {ChangeJournal.FormatDuration(row.Duration)}", row.Id);
        }

        RefreshToday();
        Persist();
    }

    /// <summary>
    /// Pending and running "No Task Selected" time counts as known here: it has its own
    /// dialog, so the unaccounted-minutes dialog never asks about the same minutes.
    /// </summary>
    public UnaccountedWindow MeasureCatchUpWindow(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        return UnaccountedTime.Measure(
            _settings.Entries,
            ActiveSession,
            CatchUpIntervalMinutes,
            now,
            NoTaskTime.Spans(_settings.UnassignedSegments, NoTaskStartedUtc, now));
    }

    public void AddCatchUpAssignments(IReadOnlyList<CatchUpSlice> slices, UnaccountedWindow window) =>
        AddFifteenMinuteRows(UnaccountedTime.Place(slices, window));

    public void AddManualEntry(TimeEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Source))
            entry.Source = "manual";

        // Manual + Add time entries land on ViewingDate (clock time-of-day equivalent).
        if (IsHistoryMode)
        {
            var minutes = entry.ManualMinutes > 0
                ? entry.ManualMinutes
                : (int)Math.Max(1, Math.Round(((entry.EndUtc ?? DateTime.UtcNow) - entry.StartUtc).TotalMinutes));
            var (startUtc, endUtc) = ResolveManualRange(TimeSpan.FromMinutes(minutes));
            entry.StartUtc = startUtc;
            entry.EndUtc = endUtc;
            if (entry.ManualMinutes <= 0)
                entry.ManualMinutes = minutes;
        }

        _settings.Entries.Add(entry);
        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Add,
            $"Manual entry: {ChangeJournal.Describe(entry.TaskName, entry.JobCodeText, entry.Ticket)} {ChangeJournal.FormatDuration(entry.Duration)}", entry.Id);
    }

    /// <summary>
    /// Adds a finished time block to a task type without starting the live timer.
    /// Uses <see cref="ViewingDate"/> so historical edits land on the loaded day.
    /// </summary>
    public void AddTimeToTaskType(
        string taskName,
        string category,
        Guid? jobCodeId,
        string jobCodeText,
        TimeSpan duration,
        string? ticket = null)
    {
        if (duration <= TimeSpan.Zero) return;

        var (startUtc, endUtc) = ResolveManualRange(duration);
        var totalMinutes = (int)Math.Round(duration.TotalMinutes);
        if (totalMinutes < 1) totalMinutes = 1;

        var name = string.IsNullOrWhiteSpace(taskName) ? "Untitled task" : taskName.Trim();
        var added = new TimeEntry
        {
            StartUtc = startUtc,
            EndUtc = endUtc,
            TaskName = name,
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
            JobCodeId = jobCodeId,
            JobCodeText = jobCodeText?.Trim() ?? string.Empty,
            Ticket = TicketRules.Normalize(ticket),
            Notes = "Added time",
            ManualMinutes = totalMinutes,
            Source = "add-time"
        };
        _settings.Entries.Add(added);
        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Add,
            $"Added {ChangeJournal.FormatDuration(duration)} to {ChangeJournal.Describe(name, added.JobCodeText, added.Ticket)}", added.Id);
    }

    /// <summary>Finished time on a task-type row of <see cref="ViewingDate"/> that Remove may take.</summary>
    public TimeSpan RemovableTimeForTaskType(string key) =>
        RowTimeEdit.RemovableTime(
            RowTimeEdit.RowEntries(_settings.Entries, ViewingDate, key),
            ActiveSession?.EntryId);

    /// <summary>
    /// Takes time off a task-type row on <see cref="ViewingDate"/>, newest finished segment first;
    /// the running timer is untouched. Returns null on success, otherwise why nothing changed.
    /// </summary>
    public string? RemoveTimeFromTaskType(string key, TimeSpan amount)
    {
        var label = RowLabel(key);
        var result = RowTimeEdit.Remove(_settings.Entries, ViewingDate, key, ActiveSession?.EntryId, amount);
        if (!result.Applied)
            return result.Error ?? "Could not remove that time.";

        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Modify, $"Removed {ChangeJournal.FormatDuration(amount)} from {label}");
        return null;
    }

    /// <summary>
    /// Moves every entry of a task-type row on <see cref="ViewingDate"/> to another job code (or none).
    /// A running timer on that row keeps running under the new code. Returns the row's new key,
    /// or null with <paramref name="error"/> set when nothing changed.
    /// </summary>
    public string? MoveTaskTypeToJobCode(string key, Guid? targetId, string targetText, out string? error)
    {
        var label = RowLabel(key);
        var result = RowTimeEdit.Move(_settings.Entries, ActiveSession, ViewingDate, key, targetId, targetText);
        if (!result.Applied)
        {
            error = result.Error ?? "Could not move that time.";
            return null;
        }

        if (result.ActiveMutated)
        {
            ActiveSession = result.ActiveSession;
            StatusText = $"Tracking: {ActiveTaskLabel}";
        }

        error = null;
        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Move,
            $"Moved {label} to {(string.IsNullOrWhiteSpace(targetText) ? "No job code" : targetText.Trim())}");
        return result.NewKey;
    }

    public void EnterHistoryMode(DateTime dayLocal)
    {
        var day = dayLocal.Date;
        if (day == DateTime.Today)
        {
            ExitHistoryMode();
            return;
        }

        if (day > DateTime.Today)
            return;

        // Never keep a live timer while editing another day.
        StopActive(save: true);
        ViewingDate = day;
        StatusText = ViewingDateLabel;
        RefreshToday();
    }

    public void ExitHistoryMode()
    {
        ViewingDate = DateTime.Today;
        StatusText = ActiveSession is null
            ? NotTrackingStatus()
            : $"Tracking: {ActiveTaskLabel}";
        RefreshToday();
    }

    /// <summary>
    /// Soft-deletes every non-deleted entry of this task type on <see cref="ViewingDate"/>.
    /// Stops the timer when the active session matches that type.
    /// </summary>
    public void SoftDeleteTaskType(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var label = RowLabel(key);

        if (ActiveSession is not null && TaskTypeSummary.KeyOf(ActiveSession) == key)
        {
            StopActive(save: true);
        }

        var nowLocal = DateTime.Now;
        var changed = false;
        foreach (var entry in _settings.Entries)
        {
            if (entry.IsDeleted) continue;
            if (!TaskTypeSummary.IsOnDay(entry, ViewingDate)) continue;
            if (TaskTypeSummary.KeyOf(entry) != key) continue;

            entry.IsDeleted = true;
            entry.DeletedAtLocal = nowLocal;
            if (entry.EndUtc is null)
                entry.EndUtc = DateTime.UtcNow;
            changed = true;
        }

        if (!changed) return;

        PurgeExpiredDeletedEntries(persist: false);
        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Delete, "Deleted " + label);
    }

    /// <summary>
    /// Moves every portion of <paramref name="sourceKey"/> on <see cref="ViewingDate"/> onto
    /// other tasks (preserving clocks), then removes the source row. A running timer on a
    /// reassigned portion keeps running under the destination task.
    /// </summary>
    public string? AssignTaskTypeSegmentsAndDelete(
        string sourceKey,
        IReadOnlyList<TaskTypeReassign.Assignment> assignments)
    {
        var label = RowLabel(sourceKey);
        var result = TaskTypeReassign.Apply(
            _settings.Entries,
            ActiveSession,
            ViewingDate,
            sourceKey,
            assignments);

        if (!result.Applied)
            return result.Error ?? "Could not assign those portions.";

        if (result.ActiveMutated)
        {
            ActiveSession = result.ActiveSession;
            Tick();
            StatusText = ActiveSession is null
                ? NotTrackingStatus()
                : $"Tracking: {ActiveTaskLabel}";
        }

        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Move, $"Assigned the time of {label} to other tasks and deleted it");
        return null;
    }

    public IReadOnlyList<DeletedTaskGroup> GetDeletedTaskGroupsToday()
    {
        PurgeExpiredDeletedEntries(persist: true);
        var groups = new Dictionary<string, List<TimeEntry>>();
        foreach (var entry in _settings.Entries)
        {
            if (!entry.IsDeleted || entry.DeletedAtLocal is null) continue;
            if (entry.DeletedAtLocal.Value.Date != DateTime.Today) continue;

            var key = TaskTypeSummary.KeyOf(entry);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(entry);
        }

        return groups
            .Select(pair =>
            {
                var latest = pair.Value.MaxBy(e => e.DeletedAtLocal ?? DateTime.MinValue)!;
                return new DeletedTaskGroup(
                    pair.Key,
                    latest.TaskName.Trim(),
                    string.IsNullOrWhiteSpace(latest.Category) ? "General" : latest.Category.Trim(),
                    latest.JobCodeId,
                    latest.JobCodeText.Trim(),
                    latest.DeletedAtLocal ?? DateTime.Now,
                    pair.Value.Select(e => e.Id).ToList(),
                    TicketRules.Normalize(latest.Ticket));
            })
            .OrderByDescending(g => g.DeletedAtLocal)
            .ThenBy(g => g.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void RestoreDeletedGroups(IEnumerable<string> keys)
    {
        var keySet = keys.ToHashSet(StringComparer.Ordinal);
        if (keySet.Count == 0) return;

        var restored = new List<TimeEntry>();
        foreach (var entry in _settings.Entries)
        {
            if (!entry.IsDeleted || entry.DeletedAtLocal is null) continue;
            if (entry.DeletedAtLocal.Value.Date != DateTime.Today) continue;
            if (!keySet.Contains(TaskTypeSummary.KeyOf(entry))) continue;

            entry.IsDeleted = false;
            entry.DeletedAtLocal = null;
            restored.Add(entry);
        }

        if (restored.Count == 0) return;
        RefreshToday();
        Persist();
        foreach (var group in restored.GroupBy(TaskTypeSummary.KeyOf))
        {
            var first = group.First();
            Log(ChangeJournal.Actions.Restore, "Restored " + ChangeJournal.Describe(first.TaskName, first.JobCodeText, first.Ticket));
        }
    }

    public void RestoreAllDeletedToday() =>
        RestoreDeletedGroups(GetDeletedTaskGroupsToday().Select(g => g.Key));

    public void SetJobCodeViewIds(IReadOnlyList<Guid> orderedIds)
    {
        _settings.JobCodeViewIds = orderedIds.Distinct().ToList();
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public JobCode? AddJobCode(string code, string description)
    {
        var trimmed = code.Trim();
        if (trimmed.Length == 0)
            return null;
        if (JobCodes.Any(existing => string.Equals(existing.Code, trimmed, StringComparison.OrdinalIgnoreCase)))
            return null;

        var created = new JobCode
        {
            Code = trimmed,
            Description = description.Trim(),
            IsActive = true
        };

        var next = JobCodes.Select(existing => new JobCode
        {
            Id = existing.Id,
            Code = existing.Code,
            Description = existing.Description,
            IsActive = existing.IsActive
        }).ToList();
        next.Add(created);
        ReplaceJobCodes(next);
        return created;
    }

    public bool ApplyScreenLockChoice(
        LockIntervalSplit.Snapshot snapshot,
        DateTime unlockedAtUtc,
        LockIntervalSplit.Choice choice,
        JobCode? targetCode)
    {
        var result = LockIntervalSplit.Apply(
            _settings.Entries,
            ActiveSession,
            snapshot,
            unlockedAtUtc,
            choice,
            targetCode);

        return ApplyLockResult(result);
    }

    public bool ApplyScreenLockAssignments(
        LockIntervalSplit.Snapshot snapshot,
        DateTime unlockedAtUtc,
        IReadOnlyList<LockIntervalSplit.AssignmentSlice> slices)
    {
        var result = LockIntervalSplit.ApplyAssignments(
            _settings.Entries,
            ActiveSession,
            snapshot,
            unlockedAtUtc,
            slices);

        return ApplyLockResult(result);
    }

    private bool ApplyLockResult(LockIntervalSplit.Result result)
    {
        if (!result.Applied)
            return false;

        if (result.ContinuedSession is not null)
        {
            ActiveSession = result.ContinuedSession;
            Tick();
            StatusText = $"Tracking: {ActiveTaskLabel}";
        }

        if (result.Mutated)
        {
            RefreshToday();
            Persist();
            Log(ChangeJournal.Actions.Modify, "Screen-lock time assigned");
        }

        return true;
    }

    /// <summary>
    /// Changes the job code on every non-deleted entry of <paramref name="key"/> on
    /// <see cref="ViewingDate"/> (and the active session when it matches).
    /// </summary>
    public string? ChangeTaskTypeJobCodeOnViewingDay(string key, JobCode newCode)
    {
        if (string.IsNullOrWhiteSpace(key) || newCode is null) return null;
        var label = RowLabel(key);

        var changed = false;
        string? newKey = null;
        foreach (var entry in _settings.Entries)
        {
            if (entry.IsDeleted) continue;
            if (!TaskTypeSummary.IsOnDay(entry, ViewingDate)) continue;
            if (TaskTypeSummary.KeyOf(entry) != key) continue;

            entry.JobCodeId = newCode.Id;
            entry.JobCodeText = newCode.Display;
            newKey = TaskTypeSummary.KeyOf(entry);
            changed = true;
        }

        if (ActiveSession is not null && TaskTypeSummary.KeyOf(ActiveSession) == key)
        {
            ActiveSession = new ActiveSession
            {
                EntryId = ActiveSession.EntryId,
                TaskName = ActiveSession.TaskName,
                Category = ActiveSession.Category,
                JobCodeId = newCode.Id,
                JobCodeText = newCode.Display,
                Ticket = ActiveSession.Ticket,
                StartedUtc = ActiveSession.StartedUtc
            };
            StatusText = $"Tracking: {ActiveTaskLabel}";
            newKey ??= TaskTypeSummary.KeyOf(ActiveSession);
            changed = true;
        }

        if (!changed) return null;

        RefreshToday();
        Persist();
        Log(ChangeJournal.Actions.Move, $"Moved {label} to {newCode.Display}");
        return newKey;
    }

    /// <summary>
    /// Renames/recategorizes every non-deleted entry of <paramref name="key"/> on
    /// <see cref="ViewingDate"/> (and the active session when it matches). Job code is unchanged.
    /// </summary>
    public string? RenameTaskTypeOnViewingDay(string key, string newTaskName, string newCategory)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var name = newTaskName.Trim();
        if (name.Length == 0) return null;
        var category = string.IsNullOrWhiteSpace(newCategory) ? "General" : newCategory.Trim();
        var label = RowLabel(key);

        var changed = false;
        string? newKey = null;
        foreach (var entry in _settings.Entries)
        {
            if (entry.IsDeleted) continue;
            if (!TaskTypeSummary.IsOnDay(entry, ViewingDate)) continue;
            if (TaskTypeSummary.KeyOf(entry) != key) continue;

            entry.TaskName = name;
            entry.Category = category;
            newKey = TaskTypeSummary.KeyOf(entry);
            changed = true;
        }

        if (ActiveSession is not null && TaskTypeSummary.KeyOf(ActiveSession) == key)
        {
            ActiveSession = new ActiveSession
            {
                EntryId = ActiveSession.EntryId,
                TaskName = name,
                Category = category,
                JobCodeId = ActiveSession.JobCodeId,
                JobCodeText = ActiveSession.JobCodeText,
                Ticket = ActiveSession.Ticket,
                StartedUtc = ActiveSession.StartedUtc
            };
            StatusText = $"Tracking: {ActiveTaskLabel}";
            newKey ??= TaskTypeSummary.KeyOf(ActiveSession);
            changed = true;
        }

        if (!changed) return null;

        RefreshToday();
        Persist();
        NoteRename(key, label, newKey!, $"{name} / {category}");
        return newKey;
    }

    public void ReplaceJobCodes(IEnumerable<JobCode> codes)
    {
        _settings.JobCodes = codes.Select(c => new JobCode
        {
            Id = c.Id == Guid.Empty ? Guid.NewGuid() : c.Id,
            Code = c.Code.Trim(),
            Description = c.Description.Trim(),
            IsActive = c.IsActive
        }).Where(c => !string.IsNullOrWhiteSpace(c.Code)).ToList();

        JobCodes.Clear();
        foreach (var c in _settings.JobCodes) JobCodes.Add(c);
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceFavorites(IEnumerable<FavoriteTask> favorites)
    {
        _settings.FavoriteTasks = favorites.Select(f => new FavoriteTask
        {
            Id = f.Id == Guid.Empty ? Guid.NewGuid() : f.Id,
            Name = f.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(f.Category) ? "General" : f.Category.Trim(),
            JobCodeId = f.JobCodeId
        }).Where(f => !string.IsNullOrWhiteSpace(f.Name)).ToList();

        FavoriteTasks.Clear();
        foreach (var f in _settings.FavoriteTasks) FavoriteTasks.Add(f);
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceCategories(IEnumerable<string> categories)
    {
        _settings.Categories = categories
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_settings.Categories.Count == 0)
            _settings.Categories.Add("General");

        Categories.Clear();
        foreach (var c in _settings.Categories) Categories.Add(c);
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdatePromptSettings(
        bool alwaysOnTop,
        bool recheckEnabled,
        int recheckHours,
        int recheckMinutes,
        bool fifteenEnabled,
        int fifteenMinutes)
    {
        _settings.AlwaysOnTop = alwaysOnTop;
        _settings.RecheckPromptEnabled = recheckEnabled;
        _settings.RecheckHours = Math.Max(0, recheckHours);
        _settings.RecheckMinutes = Math.Clamp(recheckMinutes, 0, 59);
        if (_settings.RecheckHours == 0 && _settings.RecheckMinutes == 0)
            _settings.RecheckMinutes = 15;

        _settings.FifteenMinutePromptEnabled = fifteenEnabled;
        _settings.FifteenMinuteIntervalMinutes = Math.Clamp(fifteenMinutes, 5, 120);
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRecheckInterval(int hours, int minutes)
    {
        _settings.RecheckHours = Math.Max(0, hours);
        _settings.RecheckMinutes = Math.Clamp(minutes, 0, 59);
        if (_settings.RecheckHours == 0 && _settings.RecheckMinutes == 0)
            _settings.RecheckMinutes = 15;
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveWindowPosition(double left, double top, double height)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
            return;

        _settings.WindowLeft = left;
        _settings.WindowTop = top;
        if (double.IsFinite(height) && height > 0)
            _settings.WindowHeight = height;
        Persist();
    }

    public void SaveWidgetPosition(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
            return;

        _settings.WidgetLeft = left;
        _settings.WidgetTop = top;
        Persist();
    }

    public void SetTimerWidgetOptions(bool show, bool whenCovered, bool onOtherMonitor)
    {
        if (_settings.ShowTimerWidget == show &&
            _settings.ShowTimerWidgetWhenCovered == whenCovered &&
            _settings.ShowTimerWidgetOnOtherMonitor == onOtherMonitor)
            return;
        _settings.ShowTimerWidget = show;
        _settings.ShowTimerWidgetWhenCovered = whenCovered;
        _settings.ShowTimerWidgetOnOtherMonitor = onOtherMonitor;
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetAppTrackEnabled(bool enabled)
    {
        if (_settings.AppTrackEnabled == enabled)
            return;
        _settings.AppTrackEnabled = enabled;
        Persist();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The task type the widget's Start resumes: the most recently started entry today, else the
    /// most recent on any day. Deleted entries and Break are skipped.
    /// </summary>
    public static TimeEntry? LatestWorkedEntry(IEnumerable<TimeEntry> entries, DateTime todayLocal)
    {
        TimeEntry? today = null;
        TimeEntry? any = null;
        foreach (var entry in entries)
        {
            if (entry is null || entry.IsDeleted) continue;
            if (string.IsNullOrWhiteSpace(entry.TaskName) && entry.JobCodeId is null &&
                string.IsNullOrWhiteSpace(entry.JobCodeText))
                continue;
            if (string.Equals(entry.TaskName?.Trim(), LockIntervalSplit.BreakTaskName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.Category?.Trim(), LockIntervalSplit.BreakCategory, StringComparison.OrdinalIgnoreCase))
                continue;

            if (any is null || entry.StartUtc > any.StartUtc)
                any = entry;
            if (TaskTypeSummary.IsOnDay(entry, todayLocal) && (today is null || entry.StartUtc > today.StartUtc))
                today = entry;
        }

        return today ?? any;
    }

    public TimeEntry? LastWorkedEntry() => LatestWorkedEntry(_settings.Entries, DateTime.Today);

    /// <summary>Starts the last worked task type again. False when there is none (or a task is already running).</summary>
    public bool ResumeLastTask()
    {
        if (IsHistoryMode || ActiveSession is not null) return false;

        var last = LastWorkedEntry();
        if (last is null) return false;

        var code = FindJobCode(last.JobCodeId);
        StartOrSwitch(last.TaskName, last.Category, last.JobCodeId, code?.Display ?? last.JobCodeText, last.Ticket);
        return ActiveSession is not null;
    }

    public IEnumerable<TimeEntry> EntriesForDay(DateTime dayLocal) =>
        _settings.Entries
            .Where(e =>
            {
                if (e.IsDeleted) return false;
                var start = e.StartUtc.ToLocalTime().Date;
                var end = (e.EndUtc ?? e.StartUtc).ToLocalTime().Date;
                return start == dayLocal.Date || end == dayLocal.Date;
            })
            .OrderBy(e => e.StartUtc);

    public JobCode? FindJobCode(Guid? id) =>
        id is null ? null : JobCodes.FirstOrDefault(j => j.Id == id);

    public void Persist()
    {
        PurgeExpiredDeletedEntries(persist: false);
        _store.Save(_settings);
    }

    /// <summary>
    /// Merges an older copy into the data in use (Import data…): see <see cref="SettingsMerge.Import"/>.
    /// Saves and refreshes the lists.
    /// </summary>
    public SettingsMerge.Report ImportSettings(AppSettings incoming, string from)
    {
        var report = SettingsMerge.Import(_settings, SettingsStore.Clone(incoming), DateTime.UtcNow);
        ReloadFromSettings();
        Persist();
        Log(ChangeJournal.Actions.Import,
            $"Imported {report.EntriesAdded} entries, {report.JobCodesAdded.Count} job codes, {report.CategoriesAdded.Count} categories from {from}");
        return report;
    }

    /// <summary>Picks up changes another PC saved to settings.json, in memory only (see <see cref="SettingsStore.Pull"/>).</summary>
    public SettingsMerge.Report? SyncFromDisk() => _store.Pull(_settings);

    /// <summary>Keep selected (after Yes on the overlap prompt): adds the chosen items from the other PC and saves.</summary>
    public IReadOnlyList<TimeEntry> KeepFromOtherPc(IReadOnlyList<OverlapMerge.Item> items, IReadOnlySet<Guid> selected,
        string machine)
    {
        var added = OverlapMerge.Keep(_settings, items, selected);
        var offered = items.Count(i => !i.AlreadyHere);
        if (added.Count > 0)
        {
            RefreshToday();
            Persist();
        }

        Log(ChangeJournal.Actions.Overlap, added.Count == 0
            ? $"Discarded {offered} item(s) from {machine}"
            : $"Kept {added.Count} of {offered} item(s) from {machine}: " +
              string.Join("; ", added.Select(e => $"{ChangeJournal.Describe(e.TaskName, e.JobCodeText, e.Ticket)} {ChangeJournal.FormatDuration(e.Duration)}")));
        foreach (var e in added)
            Journal?.Append(ChangeJournal.Actions.Add, $"From {machine}: {ChangeJournal.Describe(e.TaskName, e.JobCodeText, e.Ticket)}", e.Id);
        return added;
    }

    public void LogOverlap(string summary) => Journal?.Append(ChangeJournal.Actions.Overlap, summary);

    public void LogFolderChange(string summary) => Journal?.Append(ChangeJournal.Actions.Folder, summary);

    public void LogImport(string summary) => Journal?.Append(ChangeJournal.Actions.Import, summary);

    private void ReloadFromSettings()
    {
        JobCodes.Clear();
        foreach (var c in _settings.JobCodes) JobCodes.Add(c);
        FavoriteTasks.Clear();
        foreach (var f in _settings.FavoriteTasks) FavoriteTasks.Add(f);
        Categories.Clear();
        foreach (var c in _settings.Categories) Categories.Add(c);
        RefreshToday();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private (DateTime startUtc, DateTime endUtc) ResolveManualRange(TimeSpan duration)
    {
        DateTime endLocal;
        if (IsHistoryMode)
        {
            // Place the block ending at "now"'s clock time on the historical day.
            endLocal = ViewingDate.Date + DateTime.Now.TimeOfDay;
            if (endLocal.TimeOfDay < duration)
                endLocal = ViewingDate.Date + duration;
        }
        else
        {
            endLocal = DateTime.Now;
        }

        var startLocal = endLocal - duration;
        return (startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
    }

    private void PurgeExpiredDeletedEntries(bool persist)
    {
        var before = _settings.Entries.Count;
        _settings.Entries.RemoveAll(e =>
            e.IsDeleted &&
            e.DeletedAtLocal is DateTime deleted &&
            deleted.Date < DateTime.Today);
        if (persist && _settings.Entries.Count != before)
            _store.Save(_settings);
    }

    private void RefreshToday()
    {
        TodayEntries.Clear();
        foreach (var e in EntriesForDay(ViewingDate).Reverse())
            TodayEntries.Add(e);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

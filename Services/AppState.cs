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
    IReadOnlyList<Guid> EntryIds);

public sealed class AppState : INotifyPropertyChanged
{
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private TimeSpan _elapsed;
    private string _statusText = "Idle — pick a job code or task to start";
    private bool _isRunning;
    private DateTime _viewingDate = DateTime.Today;

    public AppState(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        PurgeExpiredDeletedEntries(persist: true);
        JobCodes = new ObservableCollection<JobCode>(_settings.JobCodes);
        FavoriteTasks = new ObservableCollection<FavoriteTask>(_settings.FavoriteTasks);
        Categories = new ObservableCollection<string>(_settings.Categories);
        TodayEntries = new ObservableCollection<TimeEntry>(
            EntriesForDay(DateTime.Today).Reverse());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SettingsChanged;
    public event EventHandler? ViewingDateChanged;

    public ObservableCollection<JobCode> JobCodes { get; }
    public ObservableCollection<FavoriteTask> FavoriteTasks { get; }
    public ObservableCollection<string> Categories { get; }
    public ObservableCollection<TimeEntry> TodayEntries { get; }

    public AppSettings Settings => _settings;

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

    public string ElapsedDisplay
    {
        get
        {
            var e = Elapsed;
            return $"{(int)e.TotalHours:00}:{e.Minutes:00}:{e.Seconds:00}";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string ActiveTaskLabel => ActiveSession?.TaskName is { Length: > 0 } name
        ? name
        : "No active task";

    public string ActiveJobCodeLabel => ActiveSession?.JobCodeText is { Length: > 0 } code
        ? code
        : "—";

    public void Tick()
    {
        if (ActiveSession is null)
        {
            Elapsed = TimeSpan.Zero;
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
            StatusText = "Idle — pick a job code or task to start";
            return;
        }

        Tick();
        StatusText = $"Tracking: {ActiveTaskLabel}";
    }

    public void StartOrSwitch(string taskName, string category, JobCode? jobCode)
    {
        if (IsHistoryMode) return;
        if (string.IsNullOrWhiteSpace(taskName) && jobCode is null)
            return;

        var name = !string.IsNullOrWhiteSpace(taskName) ? taskName
            : !string.IsNullOrWhiteSpace(jobCode?.Description) ? jobCode.Description
            : jobCode?.Code ?? "Untitled task";

        StartOrSwitch(name, category, jobCode?.Id, jobCode?.Display ?? string.Empty);
    }

    /// <summary>
    /// Starts a task type logged earlier. The code id and text are kept as given, so the new
    /// segment groups with the earlier ones even when that code was since removed.
    /// </summary>
    public void StartOrSwitch(string taskName, string category, Guid? jobCodeId, string jobCodeText)
    {
        if (IsHistoryMode) return;
        if (string.IsNullOrWhiteSpace(taskName) && jobCodeId is null && string.IsNullOrWhiteSpace(jobCodeText))
            return;

        StopActive(save: true);

        var name = string.IsNullOrWhiteSpace(taskName)
            ? (string.IsNullOrWhiteSpace(jobCodeText) ? "Untitled task" : jobCodeText.Trim())
            : taskName.Trim();
        var codeText = jobCodeText?.Trim() ?? string.Empty;

        var entry = new TimeEntry
        {
            StartUtc = DateTime.UtcNow,
            TaskName = name,
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
            JobCodeId = jobCodeId,
            JobCodeText = codeText,
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
            StartedUtc = entry.StartUtc
        };

        RefreshToday();
        Persist();
        Tick();
        StatusText = $"Tracking: {name}";
    }

    public void StopActive(bool save)
    {
        if (ActiveSession is null) return;

        var entry = _settings.Entries.FirstOrDefault(e => e.Id == ActiveSession.EntryId);
        if (entry is not null && entry.EndUtc is null)
            entry.EndUtc = DateTime.UtcNow;

        ActiveSession = null;
        Elapsed = TimeSpan.Zero;
        IsRunning = false;
        StatusText = IsHistoryMode
            ? ViewingDateLabel
            : "Idle — pick a job code or task to start";
        RefreshToday();
        if (save) Persist();
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
        }

        RefreshToday();
        Persist();
    }

    public UnaccountedWindow MeasureCatchUpWindow(DateTime? nowUtc = null) =>
        UnaccountedTime.Measure(
            _settings.Entries,
            ActiveSession,
            CatchUpIntervalMinutes,
            nowUtc ?? DateTime.UtcNow);

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
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;

        var (startUtc, endUtc) = ResolveManualRange(duration);
        var totalMinutes = (int)Math.Round(duration.TotalMinutes);
        if (totalMinutes < 1) totalMinutes = 1;

        var name = string.IsNullOrWhiteSpace(taskName) ? "Untitled task" : taskName.Trim();
        _settings.Entries.Add(new TimeEntry
        {
            StartUtc = startUtc,
            EndUtc = endUtc,
            TaskName = name,
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
            JobCodeId = jobCodeId,
            JobCodeText = jobCodeText?.Trim() ?? string.Empty,
            Notes = "Added time",
            ManualMinutes = totalMinutes,
            Source = "add-time"
        });
        RefreshToday();
        Persist();
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
            ? "Idle — pick a job code or task to start"
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

        if (ActiveSession is not null &&
            TaskTypeSummary.KeyOf(
                ActiveSession.TaskName,
                ActiveSession.Category,
                ActiveSession.JobCodeId,
                ActiveSession.JobCodeText) == key)
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
                ? (IsHistoryMode ? ViewingDateLabel : "Idle — pick a job code or task to start")
                : $"Tracking: {ActiveTaskLabel}";
        }

        RefreshToday();
        Persist();
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
                    pair.Value.Select(e => e.Id).ToList());
            })
            .OrderByDescending(g => g.DeletedAtLocal)
            .ThenBy(g => g.TaskName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void RestoreDeletedGroups(IEnumerable<string> keys)
    {
        var keySet = keys.ToHashSet(StringComparer.Ordinal);
        if (keySet.Count == 0) return;

        var changed = false;
        foreach (var entry in _settings.Entries)
        {
            if (!entry.IsDeleted || entry.DeletedAtLocal is null) continue;
            if (entry.DeletedAtLocal.Value.Date != DateTime.Today) continue;
            if (!keySet.Contains(TaskTypeSummary.KeyOf(entry))) continue;

            entry.IsDeleted = false;
            entry.DeletedAtLocal = null;
            changed = true;
        }

        if (!changed) return;
        RefreshToday();
        Persist();
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

        if (ActiveSession is not null &&
            TaskTypeSummary.KeyOf(
                ActiveSession.TaskName,
                ActiveSession.Category,
                ActiveSession.JobCodeId,
                ActiveSession.JobCodeText) == key)
        {
            ActiveSession = new ActiveSession
            {
                EntryId = ActiveSession.EntryId,
                TaskName = ActiveSession.TaskName,
                Category = ActiveSession.Category,
                JobCodeId = newCode.Id,
                JobCodeText = newCode.Display,
                StartedUtc = ActiveSession.StartedUtc
            };
            StatusText = $"Tracking: {ActiveTaskLabel}";
            newKey ??= TaskTypeSummary.KeyOf(
                ActiveSession.TaskName,
                ActiveSession.Category,
                ActiveSession.JobCodeId,
                ActiveSession.JobCodeText);
            changed = true;
        }

        if (!changed) return null;

        RefreshToday();
        Persist();
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

        if (ActiveSession is not null &&
            TaskTypeSummary.KeyOf(
                ActiveSession.TaskName,
                ActiveSession.Category,
                ActiveSession.JobCodeId,
                ActiveSession.JobCodeText) == key)
        {
            ActiveSession = new ActiveSession
            {
                EntryId = ActiveSession.EntryId,
                TaskName = name,
                Category = category,
                JobCodeId = ActiveSession.JobCodeId,
                JobCodeText = ActiveSession.JobCodeText,
                StartedUtc = ActiveSession.StartedUtc
            };
            StatusText = $"Tracking: {name}";
            newKey ??= TaskTypeSummary.KeyOf(name, category, ActiveSession.JobCodeId, ActiveSession.JobCodeText);
            changed = true;
        }

        if (!changed) return null;

        RefreshToday();
        Persist();
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

    public void SetShowTimerWidget(bool show)
    {
        if (_settings.ShowTimerWidget == show)
            return;
        _settings.ShowTimerWidget = show;
        Persist();
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

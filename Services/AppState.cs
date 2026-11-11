using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

public sealed class AppState : INotifyPropertyChanged
{
    private readonly SettingsStore _store;
    private AppSettings _settings;
    private TimeSpan _elapsed;
    private string _statusText = "Idle — pick a job code or task to start";
    private bool _isRunning;

    public AppState(SettingsStore store)
    {
        _store = store;
        _settings = store.Load();
        JobCodes = new ObservableCollection<JobCode>(_settings.JobCodes);
        FavoriteTasks = new ObservableCollection<FavoriteTask>(_settings.FavoriteTasks);
        Categories = new ObservableCollection<string>(_settings.Categories);
        TodayEntries = new ObservableCollection<TimeEntry>(
            _settings.Entries
                .Where(e => e.StartUtc.ToLocalTime().Date == DateTime.Today ||
                            (e.EndUtc?.ToLocalTime().Date == DateTime.Today))
                .OrderByDescending(e => e.StartUtc));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SettingsChanged;

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
        StatusText = "Idle — pick a job code or task to start";
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
        entry.Source = "manual";
        _settings.Entries.Add(entry);
        RefreshToday();
        Persist();
    }

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

    public IEnumerable<TimeEntry> EntriesForDay(DateTime dayLocal) =>
        _settings.Entries
            .Where(e =>
            {
                var start = e.StartUtc.ToLocalTime().Date;
                var end = (e.EndUtc ?? e.StartUtc).ToLocalTime().Date;
                return start == dayLocal.Date || end == dayLocal.Date;
            })
            .OrderBy(e => e.StartUtc);

    public JobCode? FindJobCode(Guid? id) =>
        id is null ? null : JobCodes.FirstOrDefault(j => j.Id == id);

    public void Persist() => _store.Save(_settings);

    private void RefreshToday()
    {
        TodayEntries.Clear();
        foreach (var e in EntriesForDay(DateTime.Today).Reverse())
            TodayEntries.Add(e);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

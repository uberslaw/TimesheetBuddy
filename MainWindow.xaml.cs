using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;
using TimesheetBuddy.Views;

namespace TimesheetBuddy;

public partial class MainWindow : Window
{
    private readonly AppState _state = App.State;
    private readonly PromptScheduler _scheduler = App.Scheduler;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ScreenLockWatcher _lockWatcher;
    private readonly ObservableCollection<TaskTypeRowModel> _taskRows = [];
    private readonly ObservableCollection<DayCodeTotalRowModel> _dayCodeRows = [];
    private readonly HashSet<string> _expandedKeys = new(StringComparer.Ordinal);
    private JobCodeViewWindow? _jobCodeView;
    private TimerWidgetWindow? _widget;
    private readonly WidgetVisibilityGate _widgetGate = new();
    private TaskTypeRowModel? _pressedRow;
    private DateTime _taskRowsDay = DateTime.Today;
    private bool _taskRebuildQueued;
    private bool _jobCodeLockedFromList;
    private bool _fillingEditors;
    private string? _editingRowKey;
    private string? _lastTicketPrefix;
    private bool _positionDirty;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        TaskRowsList.ItemsSource = _taskRows;
        DayCodeTotalsList.ItemsSource = _dayCodeRows;
        _lockWatcher = new ScreenLockWatcher(_state);
        _lockWatcher.UnlockPrompt += OnScreenUnlocked;
        _lockWatcher.LockChanged += locked =>
        {
            App.AppTracker.SetLocked(locked);
            App.OnScreenLockChanged(locked);
        };
        _uiTimer.Tick += (_, _) => RefreshTimer();
        _state.PropertyChanged += StateOnPropertyChanged;
        _state.SettingsChanged += (_, _) =>
        {
            ReloadSelectors();
            UpdateTimerWidget();
        };
        IsVisibleChanged += (_, _) => UpdateTimerWidget();
        _state.ViewingDateChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            ApplyModeUi();
            QueueTaskRebuild();
            BindStatus();
        });
        _state.TodayEntries.CollectionChanged += (_, _) => QueueTaskRebuild();
        _scheduler.RecheckDue += OnRecheckDue;
        _scheduler.FifteenMinuteDue += OnFifteenDue;
    }

    /// <summary>Whether Windows is locked now (probed at start, then followed through the lock events).</summary>
    internal bool IsScreenLocked => _lockWatcher.IsLocked;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactSize();
        PlaceWindow();
        PinToggle.IsChecked = _state.Settings.AlwaysOnTop;
        Topmost = _state.Settings.AlwaysOnTop;
        ReloadSelectors();
        ApplyModeUi();
        RefreshTimer();
        _uiTimer.Start();
        _scheduler.Start();
        _lockWatcher.Start();
        if (!_state.IsHistoryMode)
            _state.RestoreSessionIfAny();
        BindStatus();
        RebuildTaskRows();
        RefreshDayCodeTotals();

        // Editable ComboBoxes surface TextBox.TextChanged for live rename while a row is selected.
        TaskNameCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(EditorFields_Changed), true);
        CategoryCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(EditorFields_Changed), true);
    }

    private void ApplyCompactSize()
    {
        var work = SystemParameters.WorkArea;

        // Approximate system tray width as a slice of the work area (typical tray cluster).
        var trayWidth = Math.Clamp(work.Width * 0.12, 180, 320);
        Width = Math.Clamp(Math.Round(trayWidth * 2), 420, 720);
        MinWidth = Width;
        MaxWidth = Width;

        // Tall enough for the controls plus several task rows; the list scrolls beyond that.
        MinHeight = 300;
        MaxHeight = Math.Max(MinHeight, work.Height);
        var saved = _state.Settings.WindowHeight;
        Height = saved is double h && double.IsFinite(h) && h > 0
            ? Math.Clamp(h, MinHeight, MaxHeight)
            : Math.Clamp(Math.Round(work.Height * 0.5), MinHeight, Math.Min(480, MaxHeight));
    }

    private void PlaceWindow()
    {
        var work = SystemParameters.WorkArea;
        if (IsSavedPosition(_state.Settings.WindowLeft, _state.Settings.WindowTop))
        {
            Left = _state.Settings.WindowLeft!.Value;
            Top = _state.Settings.WindowTop!.Value;

            // A position saved for the old short window can push the taller one off the bottom.
            var onPrimary = Left >= work.Left - Width && Left <= work.Right;
            var bottom = onPrimary ? work.Bottom : SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            var top = onPrimary ? work.Top : SystemParameters.VirtualScreenTop;
            if (Top + Height > bottom)
                Top = Math.Max(top, bottom - Height);
            return;
        }

        Left = work.Right - Width - 12;
        Top = work.Bottom - Height - 12;
    }

    private static bool IsSavedPosition(double? left, double? top) =>
        left is double x && top is double y &&
        !double.IsNaN(x) && !double.IsNaN(y) &&
        !double.IsInfinity(x) && !double.IsInfinity(y);

    private void ApplyModeUi()
    {
        var history = _state.IsHistoryMode;
        HistoryBanner.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        HistoryBannerText.Text = history
            ? $"Editing {_state.ViewingDate:ddd d MMM yyyy} — timer inactive"
            : string.Empty;

        TasksHeader.Text = history
            ? $"{_state.ViewingDate:d MMM yyyy} by task — click a row to add time"
            : "Today by task — click a row to keep going";

        TaskEmptyText.Text = history
            ? $"Nothing logged on {_state.ViewingDate:d MMM yyyy} yet"
            : "Nothing logged today yet";

        AddButton.IsEnabled = !history;
        AddButton.Opacity = history ? 0.4 : 1;
        StopButton.IsEnabled = !history;
        StopButton.Opacity = history ? 0.4 : 1;
        TimerPanel.Opacity = history ? 0.45 : 1;
        ApplyJobCodeEditability();
        if (history)
            TimerText.Text = "--:--:--";
    }

    private void ApplyJobCodeEditability()
    {
        // List selection locks the job code; new tasks (Add) can pick freely.
        JobCodeCombo.IsEnabled = !_state.IsHistoryMode && !_jobCodeLockedFromList;
        JobCodeCombo.Opacity = JobCodeCombo.IsEnabled ? 1 : 0.7;
    }

    private void LockJobCodeFromList(bool locked)
    {
        _jobCodeLockedFromList = locked;
        ApplyJobCodeEditability();
    }

    private void ReloadSelectors()
    {
        JobCodeCombo.ItemsSource = _state.JobCodes.Where(j => j.IsActive).ToList();
        CategoryCombo.ItemsSource = _state.Categories.ToList();
        TaskNameCombo.ItemsSource = _state.FavoriteTasks
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        if (CategoryCombo.Items.Count > 0 && CategoryCombo.SelectedItem is null)
            CategoryCombo.SelectedIndex = 0;

        BindStatus();
        ApplyJobCodeEditability();
    }

    private void BindStatus()
    {
        StatusText.Text = _state.StatusText;
        if (_state.IsHistoryMode)
            TimerText.Text = "--:--:--";
        else
            TimerText.Text = _state.ElapsedDisplay;
    }

    private void RefreshTimer()
    {
        if (_positionDirty)
        {
            _positionDirty = false;
            SavePlacement();
        }

        // Other apps moving in front raise no event in this process, so the tick re-checks.
        UpdateTimerWidget(fromPoll: true);

        if (_state.IsHistoryMode)
        {
            RefreshDayCodeTotals();
            return;
        }

        _state.Tick();
        TimerText.Text = _state.ElapsedDisplay;
        RefreshTaskTotals();
        RefreshDayCodeTotals();
    }

    private void RefreshDayCodeTotals()
    {
        var day = _state.ViewingDate;
        var now = DateTime.UtcNow;
        var rows = DayTotals.ByJobCodeForDay(_state.Settings.Entries, day, now);

        // Shown beside the code rows but never added to Total or Time Remaining.
        var noTask = _state.NoTaskTotalForDay(day, now);
        var showNoTask = noTask > TimeSpan.Zero || (!_state.IsHistoryMode && _state.IsNoTaskRunning);
        NoTaskRow.Visibility = showNoTask ? Visibility.Visible : Visibility.Collapsed;
        NoTaskTotalText.Text = DayTotals.FormatCompact(noTask);

        _dayCodeRows.Clear();
        var grand = TimeSpan.Zero;
        var percents = PercentBreakdown.Shares(rows.Select(r => r.Total).ToList());
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            grand += row.Total;
            var percentText = percents.Count == rows.Count ? PercentBreakdown.Format(percents[i]) : string.Empty;
            _dayCodeRows.Add(new DayCodeTotalRowModel(row.Label, DayTotals.FormatCompact(row.Total), percentText));
        }

        DayCodeEmptyText.Visibility = rows.Count == 0 && !showNoTask ? Visibility.Visible : Visibility.Collapsed;
        DayGrandTotalText.Text = DayTotals.FormatCompact(grand);
        DayRemainingText.Text = DayTotals.FormatCompact(DayTotals.TimeRemaining(grand));
        DayTotalsPanel.ToolTip = _state.IsHistoryMode
            ? $"Time accounted for {_state.ViewingDate:d MMM yyyy} by job code (most to least), with each code's share of Total. Remaining is 7.5h minus logged."
            : "Time accounted for today by job code (most to least), with each code's share of Total. Remaining is 7.5h minus logged.";
    }

    private void SavePlacement() =>
        _state.SaveWindowPosition(Left, Top, WindowState == WindowState.Normal ? ActualHeight : double.NaN);

    private void StateOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            BindStatus();
            if (e.PropertyName is nameof(AppState.ActiveSession) or nameof(AppState.IsRunning))
                _scheduler.OnSessionChanged();
            if (e.PropertyName is nameof(AppState.ActiveSession))
                QueueTaskRebuild();
            if (e.PropertyName is nameof(AppState.IsHistoryMode) or nameof(AppState.ViewingDate))
            {
                ApplyModeUi();
                RefreshDayCodeTotals();
            }
        });
    }

    // RefreshToday clears and refills TodayEntries, so coalesce those events into one rebuild.
    private void QueueTaskRebuild()
    {
        if (_taskRebuildQueued)
            return;
        _taskRebuildQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _taskRebuildQueued = false;
            RebuildTaskRows();
        }, DispatcherPriority.Background);
    }

    /// <summary>Re-sorts the list. Only runs when entries change, never on the clock tick.</summary>
    private void RebuildTaskRows()
    {
        var day = _state.ViewingDate;
        var summary = TaskTypeSummary.Build(_state.Settings.Entries, day, DateTime.UtcNow);
        _taskRowsDay = day;

        _taskRows.Clear();
        foreach (var row in summary)
        {
            var model = TaskTypeRowModel.From(row);
            model.IsExpanded = _expandedKeys.Contains(row.Key);
            model.SyncSegments(row.Segments);
            _taskRows.Add(model);
        }

        ApplyTaskTotals(summary);
        var empty = _taskRows.Count == 0;
        TaskEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TaskHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        RefreshDayCodeTotals();
    }

    private void RefreshTaskTotals()
    {
        if (_taskRowsDay != _state.ViewingDate)
        {
            RebuildTaskRows();
            return;
        }

        var summary = TaskTypeSummary.Build(_state.Settings.Entries, _taskRowsDay, DateTime.UtcNow);
        if (summary.Count != _taskRows.Count)
        {
            RebuildTaskRows();
            return;
        }

        ApplyTaskTotals(summary);
    }

    private void ApplyTaskTotals(IReadOnlyList<TaskTypeRow> summary)
    {
        var byKey = summary.ToDictionary(r => r.Key);
        var session = _state.IsHistoryMode ? null : _state.ActiveSession;
        var activeKey = session is null ? null : TaskTypeSummary.KeyOf(session);

        foreach (var row in _taskRows)
        {
            if (!byKey.TryGetValue(row.Key, out var current))
            {
                QueueTaskRebuild();
                return;
            }

            row.TotalText = DayTotals.FormatCompact(current.Total);
            row.SegmentCount = current.SegmentCount;
            row.IsActive = row.Key == activeKey;
            if (row.IsExpanded || row.IsActive)
                row.SyncSegments(current.Segments);
        }
    }

    private void TaskRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep the window's drag handler from swallowing the click.
        // Expand arrow has its own click handler — ignore presses that start there.
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null)
        {
            _pressedRow = null;
            return;
        }

        _pressedRow = (sender as FrameworkElement)?.DataContext as TaskTypeRowModel;
        e.Handled = true;
    }

    private void TaskRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<Button>(d) is not null)
            return;

        var pressed = _pressedRow;
        _pressedRow = null;
        if (sender is not FrameworkElement { DataContext: TaskTypeRowModel row } || !ReferenceEquals(row, pressed))
            return;
        e.Handled = true;

        // Always mirror the row into the top editors (job code read-only).
        FillEditorsFromRow(row);

        if (_state.IsHistoryMode)
        {
            PromptAddTime(row);
            return;
        }

        var session = _state.ActiveSession;
        var alreadyActive = row.IsActive ||
            (session is not null && TaskTypeSummary.KeyOf(session) == row.Key);
        if (alreadyActive)
            return;

        ContinueTaskType(row);
    }

    private void FillEditorsFromRow(TaskTypeRowModel row)
    {
        _fillingEditors = true;
        _editingRowKey = row.Key;
        TaskNameCombo.Text = row.TaskName;
        CategoryCombo.Text = row.Category;
        SetTicketText(row.Ticket);

        var code = _state.FindJobCode(row.JobCodeId);
        if (code is not null)
        {
            JobCodeCombo.SelectedItem = _state.JobCodes.FirstOrDefault(j => j.Id == code.Id);
        }
        else
        {
            // Keep a display value even if the code was removed from the catalog.
            JobCodeCombo.SelectedItem = null;
            JobCodeCombo.Text = JobCode.FormatLabelFromStored(row.JobCodeText);
        }

        _fillingEditors = false;
        LockJobCodeFromList(true);
    }

    private void EditorFields_Changed(object sender, EventArgs e) =>
        TryLiveRenameFromEditors();

    private void EditorFields_Changed(object sender, TextChangedEventArgs e) =>
        TryLiveRenameFromEditors();

    private void TryLiveRenameFromEditors()
    {
        if (_fillingEditors || !_jobCodeLockedFromList || _editingRowKey is null)
            return;
        if (_state.IsHistoryMode)
            return;

        // Update the list row as you type; do not rebuild (that would fight the caret).
        ApplySelectedTaskRename(rebuild: false);
    }

    private bool ApplySelectedTaskRename(bool rebuild)
    {
        if (_editingRowKey is null || !_jobCodeLockedFromList)
            return false;

        var name = TaskNameCombo.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return false;

        var category = CategoryCombo.Text?.Trim() ?? "General";
        var oldKey = _editingRowKey;
        var newKey = _state.RenameTaskTypeOnViewingDay(oldKey, name, category);
        if (newKey is null)
            return false;

        if (_expandedKeys.Remove(oldKey))
            _expandedKeys.Add(newKey);
        _editingRowKey = newKey;
        BindStatus();

        // Merging into an existing type needs a full rebuild; otherwise patch the row in place.
        var mergesIntoExisting = !string.Equals(oldKey, newKey, StringComparison.Ordinal) &&
                                 _taskRows.Any(r => r.Key == newKey);
        if (rebuild || mergesIntoExisting)
        {
            QueueTaskRebuild();
            return true;
        }

        var row = _taskRows.FirstOrDefault(r => r.Key == oldKey);
        row?.ApplyIdentity(newKey, name, category);
        return true;
    }

    private void ContinueTaskType(TaskTypeRowModel row)
    {
        var code = _state.FindJobCode(row.JobCodeId);
        _state.StartOrSwitch(row.TaskName, row.Category, row.JobCodeId, code?.Display ?? row.JobCodeText, row.Ticket);
        _scheduler.OnSessionChanged();
        BindStatus();
        FillEditorsFromRow(row);
    }

    private void PromptAddTime(TaskTypeRowModel row)
    {
        var dialog = new AddTimeWindow(row.TaskName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        AddTimeToRow(row, dialog.SelectedDuration);
    }

    private void AddTimeToRow(TaskTypeRowModel row, TimeSpan duration)
    {
        var code = _state.FindJobCode(row.JobCodeId);
        _state.AddTimeToTaskType(
            row.TaskName,
            row.Category,
            row.JobCodeId,
            code?.Display ?? row.JobCodeText,
            duration,
            row.Ticket);

        row.IsExpanded = true;
        _expandedKeys.Add(row.Key);
        QueueTaskRebuild();
        StatusText.Text = $"Added {FormatShortDuration(duration)} to {row.TaskName}";
    }

    private void PromptModifyTime(TaskTypeRowModel row)
    {
        var current = TaskTypeSummary.Build(_state.Settings.Entries, _state.ViewingDate, DateTime.UtcNow)
            .FirstOrDefault(r => r.Key == row.Key);
        if (current is null)
        {
            MessageBox.Show(this, "That task no longer has time on this day.", "Modify time",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var codeLabel = JobCode.FormatLabelFromStored(row.JobCodeText);
        var dialog = new ModifyTimeWindow(
            row.TaskName,
            codeLabel == "—" ? "No job code" : codeLabel,
            current.Total,
            _state.RemovableTimeForTaskType(row.Key))
        { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (!dialog.IsRemove)
        {
            AddTimeToRow(row, dialog.SelectedDuration);
            return;
        }

        var error = _state.RemoveTimeFromTaskType(row.Key, dialog.SelectedDuration);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Modify time", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        QueueTaskRebuild();
        RefreshDayCodeTotals();
        StatusText.Text = $"Removed {FormatShortDuration(dialog.SelectedDuration)} from {row.TaskName}";
    }

    private void ExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaskTypeRowModel row }) return;

        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded)
        {
            _expandedKeys.Add(row.Key);
            var summary = TaskTypeSummary.Build(_state.Settings.Entries, _state.ViewingDate, DateTime.UtcNow);
            var match = summary.FirstOrDefault(r => r.Key == row.Key);
            if (match is not null)
                row.SyncSegments(match.Segments);
        }
        else
        {
            _expandedKeys.Remove(row.Key);
        }

        e.Handled = true;
    }

    private void TaskRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu, DataContext: TaskTypeRowModel row })
            return;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Header is string h &&
                (h.StartsWith("Start timer", StringComparison.Ordinal) ||
                 h.StartsWith("Continue", StringComparison.Ordinal)))
            {
                item.Header = row.SegmentCount > 0 ? "Continue" : "Start timer";
                item.IsEnabled = !_state.IsHistoryMode && !row.IsActive;
            }
        }
    }

    private void ModifyTimeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var row)) return;
        PromptModifyTime(row);
    }

    private void StartTaskMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsHistoryMode) return;
        if (!TryGetMenuRow(sender, out var row) || row.IsActive) return;
        ContinueTaskType(row);
    }

    private void ChangeJobCodeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var row)) return;

        var dialog = new ChangeJobCodeWindow(
            row.TaskName,
            _state.JobCodes.ToList(),
            row.JobCodeId)
        { Owner = this };

        if (dialog.ShowDialog() != true || dialog.SelectedCode is null)
            return;

        if (row.JobCodeId == dialog.SelectedCode.Id)
            return;

        var oldKey = row.Key;
        var newKey = _state.ChangeTaskTypeJobCodeOnViewingDay(oldKey, dialog.SelectedCode);
        if (newKey is null)
        {
            MessageBox.Show(this,
                "Could not change that job code.",
                "Change job code",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_expandedKeys.Remove(oldKey))
            _expandedKeys.Add(newKey);
        if (_editingRowKey == oldKey)
        {
            _editingRowKey = newKey;
            _fillingEditors = true;
            JobCodeCombo.SelectedItem = _state.JobCodes.FirstOrDefault(j => j.Id == dialog.SelectedCode.Id);
            _fillingEditors = false;
            LockJobCodeFromList(true);
        }

        QueueTaskRebuild();
        BindStatus();
        StatusText.Text = $"Moved {row.TaskName} to {dialog.SelectedCode.Display}";
    }

    private void MoveTimeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var row)) return;

        var summary = TaskTypeSummary.Build(_state.Settings.Entries, _state.ViewingDate, DateTime.UtcNow);
        var source = summary.FirstOrDefault(r => r.Key == row.Key);
        if (source is null)
        {
            MessageBox.Show(this, "That task no longer has time on this day.", "Move time",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = MoveTargetsFor(source, summary);
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "There is no other job code to move this time to. Add one from Menu → Add job code….",
                "Move time", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sourceLabel = JobCode.FormatLabelFromStored(source.JobCodeText);
        var dialog = new MoveTimeWindow(
            source.TaskName,
            sourceLabel == "—" ? "No job code" : sourceLabel,
            source.Total,
            source.SegmentCount,
            targets)
        { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedTarget is not { } target)
            return;

        var oldKey = source.Key;
        var newKey = _state.MoveTaskTypeToJobCode(oldKey, target.JobCodeId, target.JobCodeText, out var error);
        if (newKey is null)
        {
            MessageBox.Show(this, error ?? "Could not move that time.", "Move time",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_expandedKeys.Remove(oldKey))
            _expandedKeys.Add(newKey);
        if (_editingRowKey == oldKey)
        {
            _editingRowKey = newKey;
            _fillingEditors = true;
            var code = _state.FindJobCode(target.JobCodeId);
            JobCodeCombo.SelectedItem = code is null ? null : _state.JobCodes.FirstOrDefault(j => j.Id == code.Id);
            if (code is null)
                JobCodeCombo.Text = target.JobCodeId is null ? string.Empty : target.JobCodeText;
            _fillingEditors = false;
            LockJobCodeFromList(true);
        }

        QueueTaskRebuild();
        RefreshDayCodeTotals();
        BindStatus();
        StatusText.Text = $"Moved {DayTotals.FormatCompact(source.Total)} of {source.TaskName} to {target.Display}";
    }

    /// <summary>
    /// Active job codes other than the row's own, plus "No job code" when the day already has a row
    /// without a code (and the source is not one of them).
    /// </summary>
    private List<MoveTarget> MoveTargetsFor(TaskTypeRow source, IReadOnlyList<TaskTypeRow> summary)
    {
        var targets = _state.JobCodes
            .Where(c => c.IsActive && !RowTimeEdit.IsSameCode(source.JobCodeId, source.JobCodeText, c.Id, c.Display))
            .OrderBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .Select(c => new MoveTarget(c.Id, c.Display, c.Display))
            .ToList();

        static bool NoCode(TaskTypeRow r) => r.JobCodeId is null && string.IsNullOrWhiteSpace(r.JobCodeText);
        if (!NoCode(source) && summary.Any(NoCode))
            targets.Insert(0, new MoveTarget(null, string.Empty, "No job code"));

        return targets;
    }

    private void AssignAndDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var row)) return;

        var day = _state.ViewingDate;
        var summary = TaskTypeSummary.Build(_state.Settings.Entries, day, DateTime.UtcNow);
        var source = summary.FirstOrDefault(r => r.Key == row.Key);
        if (source is null || source.Segments.Count == 0)
        {
            MessageBox.Show(this,
                "That task no longer has time on this day.",
                "Assign time and Delete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new AssignAndDeleteWindow(
            source,
            summary,
            _state.JobCodes.ToList(),
            _state.Categories.ToList())
        { Owner = this };

        if (dialog.ShowDialog() != true)
            return;

        var error = _state.AssignTaskTypeSegmentsAndDelete(source.Key, dialog.Assignments);
        if (error is not null)
        {
            MessageBox.Show(this, error, "Assign time and Delete",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _expandedKeys.Remove(source.Key);
        if (_editingRowKey == source.Key)
        {
            _editingRowKey = null;
            LockJobCodeFromList(false);
        }

        QueueTaskRebuild();
        BindStatus();
        StatusText.Text = $"Assigned time from {source.TaskName} and removed that row";
    }

    private void DeleteTaskMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var row)) return;

        var confirm = MessageBox.Show(this,
            $"Delete “{row.TaskName}” for {_state.ViewingDate:d MMM yyyy}?\n\nYou can restore it today from Menu → Restore deleted tasks.",
            "Delete task",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _expandedKeys.Remove(row.Key);
        _state.SoftDeleteTaskType(row.Key);
        QueueTaskRebuild();
        BindStatus();
        StatusText.Text = $"Deleted {row.TaskName} (restorable today)";
    }

    private static bool TryGetMenuRow(object sender, out TaskTypeRowModel row)
    {
        row = null!;
        if (sender is MenuItem item)
        {
            if (item.CommandParameter is TaskTypeRowModel fromParam)
            {
                row = fromParam;
                return true;
            }

            if (item.DataContext is TaskTypeRowModel fromContext)
            {
                row = fromContext;
                return true;
            }
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static string FormatShortDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}h {d.Minutes:00}m";
        if (d.TotalMinutes >= 1)
            return $"{(int)d.TotalMinutes}m";
        return $"{d.Seconds}s";
    }

    private void TaskScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var bar = TaskScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
            ? SystemParameters.VerticalScrollBarWidth
            : 0;
        TaskHeader.Margin = new Thickness(9, 0, 9 + bar, 2);
    }

    private void PinToggle_Changed(object sender, RoutedEventArgs e)
    {
        var pinned = PinToggle.IsChecked == true;
        Topmost = pinned;
        UpdateTimerWidget();
        _state.UpdatePromptSettings(
            pinned,
            _state.Settings.RecheckPromptEnabled,
            _state.Settings.RecheckHours,
            _state.Settings.RecheckMinutes,
            _state.Settings.FifteenMinutePromptEnabled,
            _state.Settings.FifteenMinuteIntervalMinutes);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsHistoryMode) return;
        TryAddFromInputs();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopFromUser();

    /// <summary>The one user stop path (main Stop and the widget); starts No Task Selected.</summary>
    private void StopFromUser()
    {
        if (_state.IsHistoryMode) return;
        _state.StopActive(save: true);
        _scheduler.OnSessionChanged();
        BindStatus();
        _editingRowKey = null;
        LockJobCodeFromList(false);
    }

    private void TaskNameCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (_state.IsHistoryMode) return;

        // Editing a list selection: Enter saves the rename, does not start a new task.
        if (_jobCodeLockedFromList && _editingRowKey is not null)
        {
            ApplySelectedTaskRename(rebuild: false);
            return;
        }

        TryAddFromInputs();
    }

    private void TicketBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_state.IsHistoryMode) return;
        TryAddFromInputs();
    }

    /// <summary>
    /// Fills job code 03 and category Support when the ticket first reads as RITM/INC (or switches
    /// between them). Later edits to the selectors are left alone, and clearing the ticket reverts nothing.
    /// </summary>
    private void TicketBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_fillingEditors) return;

        var prefix = TicketRules.Prefix(TicketBox.Text);
        var changed = prefix != _lastTicketPrefix;
        _lastTicketPrefix = prefix;
        if (prefix is null)
        {
            TicketHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (changed)
            ApplyTicketDefaults();
    }

    private void ApplyTicketDefaults()
    {
        // A ticket means a new task, so release a row loaded from the list rather than renaming it.
        if (_jobCodeLockedFromList)
        {
            _editingRowKey = null;
            LockJobCodeFromList(false);
        }

        // Selecting a code never starts the timer; _fillingEditors also keeps the category change from renaming a row.
        _fillingEditors = true;
        try
        {
            var codes = JobCodeCombo.ItemsSource as IEnumerable<JobCode> ?? _state.JobCodes.Where(j => j.IsActive);
            var code = TicketRules.FindServiceNowCode(codes);
            if (code is not null)
                JobCodeCombo.SelectedItem = code;
            TicketHint.Visibility = code is null ? Visibility.Visible : Visibility.Collapsed;

            var category = TicketRules.FindSupportCategory(_state.Categories);
            if (category is not null)
                CategoryCombo.SelectedItem = category;
            CategoryCombo.Text = category ?? TicketRules.SupportCategory;
        }
        finally
        {
            _fillingEditors = false;
        }
    }

    private void SetTicketText(string? ticket)
    {
        var wasFilling = _fillingEditors;
        _fillingEditors = true;
        TicketBox.Text = TicketRules.Normalize(ticket);
        _fillingEditors = wasFilling;
        _lastTicketPrefix = TicketRules.Prefix(TicketBox.Text);
        TicketHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Add: logs a task with the selectors, task name and ticket and starts its timer (stopping any
    /// running task). An identical task type joins its existing row as a new segment.
    /// </summary>
    private void TryAddFromInputs()
    {
        if (_state.IsHistoryMode) return;

        // Add while a list row is loaded means "new task", so clear the edit lock first
        // after applying any pending rename.
        if (_jobCodeLockedFromList && _editingRowKey is not null)
            ApplySelectedTaskRename(rebuild: false);

        var ticket = TicketRules.Normalize(TicketBox.Text);
        var task = TaskNameCombo.Text?.Trim() ?? string.Empty;
        var category = string.IsNullOrWhiteSpace(CategoryCombo.Text) ? "General" : CategoryCombo.Text.Trim();
        var code = JobCodeCombo.SelectedItem as JobCode;

        // If typed task matches a favorite, inherit its job code when none selected
        // and the combo is not locked from a list selection.
        if (code is null && !_jobCodeLockedFromList && !string.IsNullOrWhiteSpace(task))
        {
            var fav = _state.FavoriteTasks.FirstOrDefault(f =>
                string.Equals(f.Name, task, StringComparison.OrdinalIgnoreCase));
            if (fav is not null)
            {
                code = _state.FindJobCode(fav.JobCodeId);
                if (!string.IsNullOrWhiteSpace(fav.Category))
                    category = fav.Category;
                if (code is not null)
                    JobCodeCombo.SelectedItem = _state.JobCodes.FirstOrDefault(j => j.Id == code.Id);
            }
        }

        if (task.Length == 0)
            task = ticket;

        var codeText = code is null ? JobCodeCombo.Text?.Trim() ?? string.Empty : string.Empty;
        if (task.Length == 0 && code is null && codeText.Length == 0)
        {
            MessageBox.Show(this, "Enter a task name or ticket, or pick a job code, then press Add.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (code is not null)
            _state.StartOrSwitch(task, category, code, ticket);
        else
            _state.StartOrSwitch(task, category, null, codeText, ticket);

        _scheduler.OnSessionChanged();
        BindStatus();
        QueueTaskRebuild();

        // Task name and ticket stay in the boxes; the next Add is a new task, not a rename.
        _editingRowKey = null;
        LockJobCodeFromList(false);
    }

    private void QuickAdd_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_state.FavoriteTasks.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No favorites yet — add some in Settings", IsEnabled = false });
        }
        else
        {
            foreach (var group in _state.FavoriteTasks.GroupBy(f => f.Category).OrderBy(g => g.Key))
            {
                var header = new MenuItem { Header = group.Key, IsEnabled = false, FontWeight = FontWeights.SemiBold };
                menu.Items.Add(header);
                foreach (var fav in group.OrderBy(f => f.Name))
                {
                    var item = new MenuItem { Header = fav.Name, Tag = fav };
                    item.Click += (_, _) =>
                    {
                        if (_state.IsHistoryMode)
                            PromptAddTimeForFavorite(fav);
                        else
                            StartFavorite(fav);
                    };
                    menu.Items.Add(item);
                }
            }
        }

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage favorites…" };
        manage.Click += (_, _) => OpenSettings(focusFavorites: true);
        menu.Items.Add(manage);

        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void StartFavorite(FavoriteTask fav)
    {
        if (_state.IsHistoryMode)
        {
            PromptAddTimeForFavorite(fav);
            return;
        }

        var code = _state.FindJobCode(fav.JobCodeId);
        TaskNameCombo.Text = fav.Name;
        CategoryCombo.Text = fav.Category;
        SetTicketText(null);
        if (code is not null)
            JobCodeCombo.SelectedItem = _state.JobCodes.FirstOrDefault(j => j.Id == code.Id);
        LockJobCodeFromList(true);

        _state.StartOrSwitch(fav.Name, fav.Category, code);
        _scheduler.OnSessionChanged();
        BindStatus();
    }

    private void PromptAddTimeForFavorite(FavoriteTask fav)
    {
        var dialog = new AddTimeWindow(fav.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var code = _state.FindJobCode(fav.JobCodeId);
        _state.AddTimeToTaskType(
            fav.Name,
            fav.Category,
            fav.JobCodeId,
            code?.Display ?? string.Empty,
            dialog.SelectedDuration);
        QueueTaskRebuild();
        StatusText.Text = $"Added {FormatShortDuration(dialog.SelectedDuration)} to {fav.Name}";
    }

    private void OpenSettings(bool focusFavorites)
    {
        var win = new SettingsWindow(focusFavorites) { Owner = this };
        win.ShowDialog();
        ReloadSelectors();
        Topmost = _state.Settings.AlwaysOnTop;
        PinToggle.IsChecked = _state.Settings.AlwaysOnTop;
    }

    private void OpenDayLog()
    {
        var win = new DayLogWindow(_state.ViewingDate) { Owner = this };
        win.ShowDialog();
        QueueTaskRebuild();
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var appTrack = new MenuItem
        {
            Header = "App Track",
            IsCheckable = true,
            IsChecked = _state.Settings.AppTrackEnabled,
            ToolTip = "Log time per program (program name only; never window titles, web pages or email)"
        };
        appTrack.Click += (_, _) => App.SetAppTrack(appTrack.IsChecked);
        menu.Items.Add(appTrack);

        var appUsage = new MenuItem { Header = "App usage…" };
        appUsage.Click += (_, _) => new AppUsageWindow { Owner = this }.ShowDialog();
        menu.Items.Add(appUsage);

        menu.Items.Add(new Separator());

        var dayLog = new MenuItem { Header = "Day log…" };
        dayLog.Click += (_, _) => OpenDayLog();
        menu.Items.Add(dayLog);

        var weeks = new MenuItem { Header = "Weeks & export…" };
        weeks.Click += (_, _) => OpenWeekBrowser();
        menu.Items.Add(weeks);

        var changeLog = new MenuItem { Header = "Change log…", ToolTip = "Recent changes from every PC using this data folder" };
        changeLog.Click += (_, _) => new ChangeLogWindow { Owner = this }.ShowDialog();
        menu.Items.Add(changeLog);

        var jobCodes = new MenuItem { Header = "Job code view" };
        jobCodes.Click += (_, _) => OpenJobCodeView();
        menu.Items.Add(jobCodes);

        menu.Items.Add(new Separator());

        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => OpenSettings(false);
        menu.Items.Add(settings);

        menu.Items.Add(new Separator());

        var addCode = new MenuItem { Header = "Add job code…" };
        addCode.Click += (_, _) =>
        {
            var win = new QuickAddJobCodeWindow { Owner = this };
            if (win.ShowDialog() == true)
                ReloadSelectors();
        };
        menu.Items.Add(addCode);

        var restore = new MenuItem { Header = "Restore deleted tasks…" };
        restore.Click += (_, _) =>
        {
            var win = new RestoreDeletedWindow { Owner = this };
            if (win.ShowDialog() == true)
                QueueTaskRebuild();
        };
        menu.Items.Add(restore);

        menu.Items.Add(new Separator());

        var load = new MenuItem { Header = "Load timesheet…" };
        load.Click += (_, _) => LoadTimesheet();
        menu.Items.Add(load);

        if (_state.IsHistoryMode)
        {
            var today = new MenuItem { Header = "Back to today" };
            today.Click += (_, _) => BackToToday_Click(today, new RoutedEventArgs());
            menu.Items.Add(today);
        }

        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void LoadTimesheet()
    {
        var win = new LoadTimesheetWindow { Owner = this };
        if (win.ShowDialog() != true) return;

        if (win.SelectedDay.Date == DateTime.Today)
            _state.ExitHistoryMode();
        else
            _state.EnterHistoryMode(win.SelectedDay);

        ApplyModeUi();
        QueueTaskRebuild();
        BindStatus();
    }

    private void BackToToday_Click(object sender, RoutedEventArgs e)
    {
        _state.ExitHistoryMode();
        ApplyModeUi();
        QueueTaskRebuild();
        BindStatus();
    }

    private void OpenWeekBrowser()
    {
        var win = new WeekBrowserWindow { Owner = this };
        win.ShowDialog();
    }

    private void OpenJobCodeView()
    {
        if (_jobCodeView is { IsVisible: true })
        {
            if (_jobCodeView.WindowState == WindowState.Minimized)
                _jobCodeView.WindowState = WindowState.Normal;
            _jobCodeView.Activate();
            return;
        }

        _jobCodeView = new JobCodeViewWindow { Owner = this };
        _jobCodeView.Closed += (_, _) => _jobCodeView = null;
        _jobCodeView.Show();
        _jobCodeView.Activate();
    }

    private void OnScreenUnlocked(LockIntervalSplit.Snapshot snapshot, DateTime unlockedAtUtc, TimeSpan lockedFor)
    {
        if (_closing || _state.IsHistoryMode)
            return;

        EnsureVisibleForPrompt();
        var dialog = new LockPromptWindow(
            LockIntervalSplit.PromptMessage(lockedFor),
            LockIntervalSplit.FormatActiveLabel(snapshot),
            _state.JobCodes.ToList(),
            snapshot.JobCodeId)
        { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var applied = _state.ApplyScreenLockChoice(
            snapshot,
            unlockedAtUtc,
            dialog.Choice,
            dialog.SelectedCode);
        if (!applied && dialog.Choice != LockIntervalSplit.Choice.KeepOnCurrent)
        {
            MessageBox.Show(this,
                "Those minutes stayed on the task that was running. The timer had already stopped, so they were not moved.",
                "Timesheet Buddy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        QueueTaskRebuild();
        RefreshDayCodeTotals();
    }

    private void OnRecheckDue() => Dispatcher.Invoke(ShowRecheck);

    private void ShowRecheck()
    {
        if (_state.IsHistoryMode) return;
        if (_scheduler.RecheckOpen || _state.ActiveSession is null) return;
        _scheduler.RecheckOpen = true;
        try
        {
            EnsureVisibleForPrompt();
            var win = new RecheckWindow { Owner = this };
            win.ShowDialog();
        }
        finally
        {
            Topmost = _state.Settings.AlwaysOnTop;
            _scheduler.RecheckOpen = false;
            _scheduler.ResetRecheck();
            // A covered window would only ask "still working?" again.
            if (_state.MeasureCatchUpWindow().UnaccountedMinutes == 0)
                _scheduler.ResetFifteen();
        }
    }

    private void OnFifteenDue()
    {
        Dispatcher.Invoke(() =>
        {
            if (_state.IsHistoryMode) return;
            if (_scheduler.FifteenOpen || _scheduler.RecheckOpen) return;

            // One dialog per tick. Pending idle time wins; the unaccounted check treats it as known.
            var noTask = _state.OpenNoTaskTime();
            if (NoTaskTime.ShouldPrompt(noTask))
            {
                ShowAssignNoTask(noTask);
                return;
            }

            var gap = _state.MeasureCatchUpWindow();
            switch (UnaccountedTime.Decide(gap, _state.ActiveSession is not null))
            {
                case CatchUpPromptKind.AssignTasks:
                    ShowAssignTasks(gap);
                    break;
                case CatchUpPromptKind.StillWorking:
                    ShowStillWorkingFromCatchUp();
                    break;
                default:
                    _scheduler.ResetFifteen();
                    break;
            }
        });
    }

    private void ShowAssignTasks(UnaccountedWindow gap)
    {
        _scheduler.FifteenOpen = true;
        try
        {
            EnsureVisibleForPrompt();
            var win = new FifteenMinuteWindow(gap) { Owner = this };
            win.ShowDialog();
        }
        finally
        {
            Topmost = _state.Settings.AlwaysOnTop;
            _scheduler.FifteenOpen = false;
            _scheduler.ResetFifteen();
            _scheduler.ResetRecheckIfDue();
        }
    }

    private void ShowAssignNoTask(IReadOnlyList<UtcInterval> open)
    {
        _scheduler.FifteenOpen = true;
        try
        {
            EnsureVisibleForPrompt();
            var win = new NoTaskAssignWindow(open) { Owner = this };
            if (win.ShowDialog() == true)
                QueueTaskRebuild();
        }
        finally
        {
            Topmost = _state.Settings.AlwaysOnTop;
            _scheduler.FifteenOpen = false;
            _scheduler.ResetFifteen();
            _scheduler.ResetRecheckIfDue();
            BindStatus();
            RefreshDayCodeTotals();
        }
    }

    private void ShowStillWorkingFromCatchUp()
    {
        // Hold the catch-up flag so the recheck timer cannot open a second dialog
        // on top of this one. ShowRecheck resets the recheck deadline on dismiss.
        _scheduler.FifteenOpen = true;
        try
        {
            ShowRecheck();
        }
        finally
        {
            Topmost = _state.Settings.AlwaysOnTop;
            _scheduler.FifteenOpen = false;
            _scheduler.ResetFifteen();
        }
    }

    private void EnsureVisibleForPrompt()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        _widgetGate.Reset();
        HideTimerWidget();
        Activate();
        // Force above other windows while a prompt is shown.
        Topmost = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // Covers the minimize button, taskbar clicks and Win+M, which all change WindowState.
    private void Window_StateChanged(object? sender, EventArgs e) => UpdateTimerWidget();

    private void Window_Activated(object? sender, EventArgs e) => UpdateTimerWidget();

    private void Window_Deactivated(object? sender, EventArgs e) => UpdateTimerWidget();

    /// <summary>
    /// Shows the widget whenever the user cannot see this window on the primary monitor
    /// (<see cref="WidgetVisibilityRules.Decide"/>), through the hysteresis in <see cref="WidgetVisibilityGate"/>.
    /// </summary>
    private void UpdateTimerWidget(bool fromPoll = false)
    {
        if (_closing || !IsLoaded)
            return;

        var settings = _state.Settings;
        var options = new WidgetVisibilityOptions(
            settings.ShowTimerWidget,
            settings.ShowTimerWidgetWhenCovered,
            settings.ShowTimerWidgetOnOtherMonitor);
        var minimized = WindowState == WindowState.Minimized;
        var hidden = Visibility != Visibility.Visible || !IsVisible;
        var modal = ComponentDispatcher.IsThreadModal;

        // Only walk windows when the answer can change the outcome.
        var onPrimary = true;
        var covered = 0.0;
        if (options.Enabled && !minimized && !hidden && !modal)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (options.WhenOtherMonitor)
                onPrimary = WindowVisibility.IsOnPrimaryMonitor(hwnd);
            if (options.WhenCovered && onPrimary && !Topmost)
                covered = WindowVisibility.CoveredFraction(hwnd);
        }

        var reason = WidgetVisibilityRules.Decide(
            new MainWindowSnapshot(minimized, hidden, Topmost, covered, onPrimary, modal),
            options,
            _closing);
        if (_widgetGate.Update(reason, fromPoll))
            ShowTimerWidget();
        else
            HideTimerWidget();
    }

    private void ShowTimerWidget()
    {
        if (_closing || !_state.Settings.ShowTimerWidget)
            return;

        if (_widget is { IsVisible: true })
            return;

        if (_widget is null)
        {
            _widget = new TimerWidgetWindow();
            _widget.RestoreRequested += (_, _) => RestoreFromWidget();
            _widget.StartRequested += (_, _) => StartFromWidget();
            _widget.StopRequested += (_, _) => StopFromUser();
            _widget.Closed += (_, _) => _widget = null;
        }

        _widget.ShowWidget();
    }

    private void HideTimerWidget() => _widget?.HideWidget();

    /// <summary>Resumes the last worked task type; with nothing to resume, opens the app to pick one.</summary>
    private void StartFromWidget()
    {
        if (_closing || _state.IsHistoryMode || _state.ActiveSession is not null)
            return;

        if (!_state.ResumeLastTask())
        {
            RestoreFromWidget();
            return;
        }

        _scheduler.OnSessionChanged();
        BindStatus();
    }

    private void RestoreFromWidget()
    {
        if (_closing)
            return;
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        _widgetGate.Reset();
        HideTimerWidget();
        Activate();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        _positionDirty = true;
        UpdateTimerWidget();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _positionDirty = true;
        UpdateTimerWidget();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _lockWatcher.Dispose();

        // A hidden widget still counts as an open window and would keep the app alive.
        _widget?.Close();
        _widget = null;
        App.CloseOverlapWindows();

        // Another PC asked this one to close: nothing is saved (App.CloseWithoutSaving).
        if (App.ClosingWithoutSave)
            return;

        // A normal close finishes a running timer onto today's list and closes any running
        // No Task Selected time into pending, without starting new idle time. Neither
        // resumes on the next launch.
        _state.StopAllForShutdown();
        _scheduler.OnSessionChanged();
        App.AppTracker.Stop();

        SavePlacement();
        _state.Persist();
    }
}

public sealed class DayCodeTotalRowModel(string label, string timeText, string percentText)
{
    public string Label { get; } = label;
    public string TimeText { get; } = timeText;
    public string PercentText { get; } = percentText;
}

public sealed class TaskSegmentRowModel
{
    public Guid EntryId { get; init; }
    public string TimeOfDay { get; init; } = string.Empty;
    public string DurationDisplay { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
}

public sealed class TaskTypeRowModel : INotifyPropertyChanged
{
    private string _key = string.Empty;
    private string _taskName = string.Empty;
    private string _category = string.Empty;

    public required string Key
    {
        get => _key;
        set => Set(ref _key, value);
    }

    public required string TaskName
    {
        get => _taskName;
        set
        {
            if (Set(ref _taskName, value))
                OnPropertyChanged(nameof(TaskDisplay));
        }
    }

    public required string Category
    {
        get => _category;
        set => Set(ref _category, value);
    }

    public required Guid? JobCodeId { get; init; }
    public required string JobCodeText { get; init; }
    public string Ticket { get; init; } = string.Empty;

    /// <summary>Task column text: "INC0012345 · task name" when the row has a ticket.</summary>
    public string TaskDisplay => TicketRules.Label(Ticket, TaskName);
    public string JobCodeDisplay => JobCode.FormatLabelFromStored(JobCodeText);
    public ObservableCollection<TaskSegmentRowModel> Children { get; } = [];

    public static TaskTypeRowModel From(TaskTypeRow row) => new()
    {
        Key = row.Key,
        TaskName = row.TaskName,
        Category = row.Category,
        JobCodeId = row.JobCodeId,
        JobCodeText = row.JobCodeText,
        Ticket = row.Ticket
    };

    /// <summary>Updates identity fields after a live rename without rebuilding the list.</summary>
    public void ApplyIdentity(string key, string taskName, string category)
    {
        Key = key;
        TaskName = taskName;
        Category = category;
    }

    public void SyncSegments(IReadOnlyList<TaskSegment> segments)
    {
        Children.Clear();
        foreach (var segment in segments)
        {
            Children.Add(new TaskSegmentRowModel
            {
                EntryId = segment.EntryId,
                TimeOfDay = segment.StartUtc.ToLocalTime().ToString("HH:mm"),
                DurationDisplay = DayTotals.FormatCompact(segment.Duration),
                IsRunning = segment.IsRunning
            });
        }

        OnPropertyChanged(nameof(ChildrenVisibility));
    }

    private string _totalText = "0";
    public string TotalText
    {
        get => _totalText;
        set => Set(ref _totalText, value);
    }

    private int _segmentCount;
    public int SegmentCount
    {
        get => _segmentCount;
        set => Set(ref _segmentCount, value);
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ArrowGlyph));
            OnPropertyChanged(nameof(ChildrenVisibility));
        }
    }

    public string ArrowGlyph => IsExpanded ? "▼" : "▶";

    public Visibility ChildrenVisibility =>
        IsExpanded && Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

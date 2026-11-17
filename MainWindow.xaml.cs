using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private readonly HashSet<string> _expandedKeys = new(StringComparer.Ordinal);
    private JobCodeViewWindow? _jobCodeView;
    private TaskTypeRowModel? _pressedRow;
    private DateTime _taskRowsDay = DateTime.Today;
    private bool _taskRebuildQueued;
    private bool _jobCodeLockedFromList;
    private bool _positionDirty;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        TaskRowsList.ItemsSource = _taskRows;
        _lockWatcher = new ScreenLockWatcher(_state);
        _lockWatcher.UnlockPrompt += OnScreenUnlocked;
        _uiTimer.Tick += (_, _) => RefreshTimer();
        _state.PropertyChanged += StateOnPropertyChanged;
        _state.SettingsChanged += (_, _) => ReloadSelectors();
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

        StartButton.IsEnabled = !history;
        StartButton.Opacity = history ? 0.4 : 1;
        StopButton.IsEnabled = !history;
        StopButton.Opacity = history ? 0.4 : 1;
        TimerPanel.Opacity = history ? 0.45 : 1;
        ApplyJobCodeEditability();
        if (history)
            TimerText.Text = "--:--:--";
    }

    private void ApplyJobCodeEditability()
    {
        // List selection locks the job code; new tasks (Start) can pick freely.
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

        if (_state.IsHistoryMode)
            return;

        _state.Tick();
        TimerText.Text = _state.ElapsedDisplay;
        RefreshTaskTotals();
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
                ApplyModeUi();
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
        var activeKey = session is null
            ? null
            : TaskTypeSummary.KeyOf(session.TaskName, session.Category, session.JobCodeId, session.JobCodeText);

        foreach (var row in _taskRows)
        {
            if (!byKey.TryGetValue(row.Key, out var current))
            {
                QueueTaskRebuild();
                return;
            }

            row.TotalText = DayTotals.FormatClock(current.Total);
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
            (session is not null &&
             TaskTypeSummary.KeyOf(session.TaskName, session.Category, session.JobCodeId, session.JobCodeText) == row.Key);
        if (alreadyActive)
            return;

        ContinueTaskType(row);
    }

    private void FillEditorsFromRow(TaskTypeRowModel row)
    {
        TaskNameCombo.Text = row.TaskName;
        CategoryCombo.Text = row.Category;

        var code = _state.FindJobCode(row.JobCodeId);
        if (code is not null)
        {
            JobCodeCombo.SelectedItem = _state.JobCodes.FirstOrDefault(j => j.Id == code.Id);
        }
        else
        {
            // Keep a display value even if the code was removed from the catalog.
            JobCodeCombo.SelectedItem = null;
            JobCodeCombo.Text = string.IsNullOrWhiteSpace(row.JobCodeText) ? string.Empty : row.JobCodeText;
        }

        LockJobCodeFromList(true);
    }

    private void ContinueTaskType(TaskTypeRowModel row)
    {
        var code = _state.FindJobCode(row.JobCodeId);
        _state.StartOrSwitch(row.TaskName, row.Category, row.JobCodeId, code?.Display ?? row.JobCodeText);
        _scheduler.OnSessionChanged();
        BindStatus();
        FillEditorsFromRow(row);
    }

    private void PromptAddTime(TaskTypeRowModel row)
    {
        var dialog = new AddTimeWindow(row.TaskName) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var code = _state.FindJobCode(row.JobCodeId);
        _state.AddTimeToTaskType(
            row.TaskName,
            row.Category,
            row.JobCodeId,
            code?.Display ?? row.JobCodeText,
            dialog.SelectedDuration);

        row.IsExpanded = true;
        _expandedKeys.Add(row.Key);
        QueueTaskRebuild();
        StatusText.Text = $"Added {FormatShortDuration(dialog.SelectedDuration)} to {row.TaskName}";
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

    private void AddTimeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var row)) return;
        PromptAddTime(row);
    }

    private void StartTaskMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsHistoryMode) return;
        if (!TryGetMenuRow(sender, out var row) || row.IsActive) return;
        ContinueTaskType(row);
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
        _state.UpdatePromptSettings(
            pinned,
            _state.Settings.RecheckPromptEnabled,
            _state.Settings.RecheckHours,
            _state.Settings.RecheckMinutes,
            _state.Settings.FifteenMinutePromptEnabled,
            _state.Settings.FifteenMinuteIntervalMinutes);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsHistoryMode) return;
        TryStartFromInputs();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsHistoryMode) return;
        _state.StopActive(save: true);
        _scheduler.OnSessionChanged();
        BindStatus();
        LockJobCodeFromList(false);
    }

    private void TaskNameCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (!_state.IsHistoryMode)
                TryStartFromInputs();
            e.Handled = true;
        }
    }

    private void TryStartFromInputs()
    {
        if (_state.IsHistoryMode) return;

        var task = TaskNameCombo.Text?.Trim() ?? string.Empty;
        var category = CategoryCombo.Text?.Trim() ?? "General";
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

        if (string.IsNullOrWhiteSpace(task))
        {
            MessageBox.Show(this, "Enter a task name, then press Start.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (code is null && string.IsNullOrWhiteSpace(JobCodeCombo.Text))
        {
            MessageBox.Show(this, "Pick a job code, then press Start.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (code is not null)
            _state.StartOrSwitch(task, category, code);
        else
            _state.StartOrSwitch(task, category, null, JobCodeCombo.Text?.Trim() ?? string.Empty);

        _scheduler.OnSessionChanged();
        BindStatus();

        // Ready for the next new task after Enter / Start.
        TaskNameCombo.Text = string.Empty;
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

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(false);

    private void OpenSettings(bool focusFavorites)
    {
        var win = new SettingsWindow(focusFavorites) { Owner = this };
        win.ShowDialog();
        ReloadSelectors();
        Topmost = _state.Settings.AlwaysOnTop;
        PinToggle.IsChecked = _state.Settings.AlwaysOnTop;
    }

    private void DayLog_Click(object sender, RoutedEventArgs e)
    {
        var win = new DayLogWindow(_state.ViewingDate) { Owner = this };
        win.ShowDialog();
        QueueTaskRebuild();
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        var weeks = new MenuItem { Header = "Weeks & export…" };
        weeks.Click += (_, _) => OpenWeekBrowser();
        menu.Items.Add(weeks);

        var jobCodes = new MenuItem { Header = "Job code view" };
        jobCodes.Click += (_, _) => OpenJobCodeView();
        menu.Items.Add(jobCodes);

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

        var code = string.IsNullOrWhiteSpace(snapshot.JobCodeText)
            ? snapshot.TaskName
            : snapshot.JobCodeText;
        var message = LockIntervalSplit.PromptMessage(LockIntervalSplit.ReportedMinutes(lockedFor), code);
        var others = _state.JobCodes.Where(job => job.Id != snapshot.JobCodeId).ToList();

        EnsureVisibleForPrompt();
        var dialog = new LockPromptWindow(message, others) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var applied = _state.ApplyScreenLockChoice(snapshot, unlockedAtUtc, dialog.Choice, dialog.SelectedCode);
        if (!applied && dialog.Choice != LockIntervalSplit.Choice.KeepOnCurrent)
        {
            MessageBox.Show(this,
                "Those minutes stayed on the task that was running. The timer had already stopped, so they were not moved.",
                "Timesheet Buddy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void AddManual_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ManualEntryWindow { Owner = this };
        dialog.ShowDialog();
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
        Activate();
        // Force above other windows while a prompt is shown.
        Topmost = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Window_LocationChanged(object? sender, EventArgs e) => _positionDirty = true;

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded)
            _positionDirty = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _lockWatcher.Dispose();

        // A normal close finishes a running timer onto today's list.
        // The open entry gets EndUtc and ActiveSession is cleared, so it
        // does not resume as an active session on the next launch.
        if (_state.ActiveSession is not null)
        {
            _state.StopActive(save: true);
            _scheduler.OnSessionChanged();
        }

        SavePlacement();
        _state.Persist();
    }
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
    public required string Key { get; init; }
    public required string TaskName { get; init; }
    public required string Category { get; init; }
    public required Guid? JobCodeId { get; init; }
    public required string JobCodeText { get; init; }
    public string JobCodeDisplay => string.IsNullOrWhiteSpace(JobCodeText) ? "—" : JobCodeText;
    public ObservableCollection<TaskSegmentRowModel> Children { get; } = [];

    public static TaskTypeRowModel From(TaskTypeRow row) => new()
    {
        Key = row.Key,
        TaskName = row.TaskName,
        Category = row.Category,
        JobCodeId = row.JobCodeId,
        JobCodeText = row.JobCodeText
    };

    public void SyncSegments(IReadOnlyList<TaskSegment> segments)
    {
        Children.Clear();
        foreach (var segment in segments)
        {
            Children.Add(new TaskSegmentRowModel
            {
                EntryId = segment.EntryId,
                TimeOfDay = segment.StartUtc.ToLocalTime().ToString("HH:mm"),
                DurationDisplay = DayTotals.FormatClock(segment.Duration),
                IsRunning = segment.IsRunning
            });
        }

        OnPropertyChanged(nameof(ChildrenVisibility));
    }

    private string _totalText = "00:00:00";
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

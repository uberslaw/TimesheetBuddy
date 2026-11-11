using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private JobCodeViewWindow? _jobCodeView;
    private TaskTypeRowModel? _pressedRow;
    private DateTime _taskRowsDay = DateTime.Today;
    private bool _taskRebuildQueued;
    private bool _suppressStart;
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
        RefreshTimer();
        _uiTimer.Start();
        _scheduler.Start();
        _lockWatcher.Start();
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

    private void ReloadSelectors()
    {
        _suppressStart = true;
        JobCodeCombo.ItemsSource = _state.JobCodes.Where(j => j.IsActive).ToList();
        CategoryCombo.ItemsSource = _state.Categories.ToList();
        TaskNameCombo.ItemsSource = _state.FavoriteTasks
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        if (CategoryCombo.Items.Count > 0 && CategoryCombo.SelectedItem is null)
            CategoryCombo.SelectedIndex = 0;

        _suppressStart = false;
        BindStatus();
    }

    private void BindStatus()
    {
        StatusText.Text = _state.StatusText;
        TimerText.Text = _state.ElapsedDisplay;
    }

    private void RefreshTimer()
    {
        _state.Tick();
        TimerText.Text = _state.ElapsedDisplay;
        RefreshTaskTotals();
        if (_positionDirty)
        {
            _positionDirty = false;
            SavePlacement();
        }
    }

    private void SavePlacement() =>
        _state.SaveWindowPosition(Left, Top, WindowState == WindowState.Normal ? ActualHeight : double.NaN);

    private void StateOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(BindStatus);
        if (e.PropertyName is nameof(AppState.ActiveSession) or nameof(AppState.IsRunning))
            _scheduler.OnSessionChanged();
        if (e.PropertyName is nameof(AppState.ActiveSession))
            QueueTaskRebuild();
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
        var day = DateTime.Today;
        var summary = TaskTypeSummary.Build(_state.Settings.Entries, day, DateTime.UtcNow);
        _taskRowsDay = day;

        _taskRows.Clear();
        foreach (var row in summary)
            _taskRows.Add(TaskTypeRowModel.From(row));

        ApplyTaskTotals(summary);
        var empty = _taskRows.Count == 0;
        TaskEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TaskHeader.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshTaskTotals()
    {
        if (_taskRowsDay != DateTime.Today)
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
        var session = _state.ActiveSession;
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
        }
    }

    private void TaskRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep the window's drag handler from swallowing the click.
        _pressedRow = (sender as FrameworkElement)?.DataContext as TaskTypeRowModel;
        e.Handled = true;
    }

    private void TaskRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressedRow;
        _pressedRow = null;
        if (sender is not FrameworkElement { DataContext: TaskTypeRowModel row } || !ReferenceEquals(row, pressed))
            return;
        e.Handled = true;
        if (row.IsActive)
            return;

        var session = _state.ActiveSession;
        if (session is not null &&
            TaskTypeSummary.KeyOf(session.TaskName, session.Category, session.JobCodeId, session.JobCodeText) == row.Key)
            return;

        var code = _state.FindJobCode(row.JobCodeId);
        _state.StartOrSwitch(row.TaskName, row.Category, row.JobCodeId, code?.Display ?? row.JobCodeText);
        _scheduler.OnSessionChanged();
        BindStatus();
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

    private void Start_Click(object sender, RoutedEventArgs e) => TryStartFromInputs();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _state.StopActive(save: true);
        _scheduler.OnSessionChanged();
        BindStatus();
    }

    private void JobCodeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStart) return;
        if (JobCodeCombo.SelectedItem is not JobCode code) return;

        // Selecting a job code starts the timer; task name optional.
        var task = TaskNameCombo.Text?.Trim() ?? string.Empty;
        var category = CategoryCombo.Text?.Trim() ?? "General";
        _state.StartOrSwitch(task, category, code);
        _scheduler.OnSessionChanged();
        BindStatus();
    }

    private void TaskNameCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryStartFromInputs();
            e.Handled = true;
        }
    }

    private void TryStartFromInputs()
    {
        var task = TaskNameCombo.Text?.Trim() ?? string.Empty;
        var category = CategoryCombo.Text?.Trim() ?? "General";
        var code = JobCodeCombo.SelectedItem as JobCode;

        // If typed task matches a favorite, inherit its job code when none selected.
        if (code is null && !string.IsNullOrWhiteSpace(task))
        {
            var fav = _state.FavoriteTasks.FirstOrDefault(f =>
                string.Equals(f.Name, task, StringComparison.OrdinalIgnoreCase));
            if (fav is not null)
            {
                code = _state.FindJobCode(fav.JobCodeId);
                if (!string.IsNullOrWhiteSpace(fav.Category))
                    category = fav.Category;
                if (code is not null)
                {
                    _suppressStart = true;
                    JobCodeCombo.SelectedItem = _state.JobCodes.FirstOrDefault(j => j.Id == code.Id);
                    _suppressStart = false;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(task) && code is null)
        {
            MessageBox.Show(this, "Pick a job code or enter a task name to start timing.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _state.StartOrSwitch(task, category, code);
        _scheduler.OnSessionChanged();
        BindStatus();
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
                    item.Click += (_, _) => StartFavorite(fav);
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
        var code = _state.FindJobCode(fav.JobCodeId);
        _suppressStart = true;
        TaskNameCombo.Text = fav.Name;
        CategoryCombo.Text = fav.Category;
        if (code is not null)
            JobCodeCombo.SelectedItem = _state.JobCodes.FirstOrDefault(j => j.Id == code.Id);
        _suppressStart = false;

        _state.StartOrSwitch(fav.Name, fav.Category, code);
        _scheduler.OnSessionChanged();
        BindStatus();
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
        var win = new DayLogWindow { Owner = this };
        win.ShowDialog();
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
        menu.PlacementTarget = sender as UIElement;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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
        if (_closing)
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

public sealed class TaskTypeRowModel : INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required string TaskName { get; init; }
    public required string Category { get; init; }
    public required Guid? JobCodeId { get; init; }
    public required string JobCodeText { get; init; }
    public string JobCodeDisplay => string.IsNullOrWhiteSpace(JobCodeText) ? "—" : JobCodeText;

    public static TaskTypeRowModel From(TaskTypeRow row) => new()
    {
        Key = row.Key,
        TaskName = row.TaskName,
        Category = row.Category,
        JobCodeId = row.JobCodeId,
        JobCodeText = row.JobCodeText
    };

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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

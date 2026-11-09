using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    private bool _suppressStart;
    private bool _positionDirty;

    public MainWindow()
    {
        InitializeComponent();
        _uiTimer.Tick += (_, _) => RefreshTimer();
        _state.PropertyChanged += StateOnPropertyChanged;
        _state.SettingsChanged += (_, _) => ReloadSelectors();
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
        _state.RestoreSessionIfAny();
        BindStatus();
    }

    private void ApplyCompactSize()
    {
        // Taskbar height ≈ screen height − work area height.
        var taskbarHeight = SystemParameters.PrimaryScreenHeight - SystemParameters.WorkArea.Height;
        if (taskbarHeight < 28) taskbarHeight = 40;

        // Approximate system tray width as a slice of the work area (typical tray cluster).
        var trayWidth = Math.Clamp(SystemParameters.WorkArea.Width * 0.12, 180, 320);

        Width = Math.Round(trayWidth * 2);
        Height = Math.Round(taskbarHeight * 4);
        MinWidth = 420;
        MinHeight = 150;
        MaxWidth = 720;
        MaxHeight = 280;
    }

    private void PlaceWindow()
    {
        if (IsSavedPosition(_state.Settings.WindowLeft, _state.Settings.WindowTop))
        {
            Left = _state.Settings.WindowLeft!.Value;
            Top = _state.Settings.WindowTop!.Value;
            return;
        }

        var work = SystemParameters.WorkArea;
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
        ActiveTaskText.Text = _state.ActiveTaskLabel;
        ActiveCodeText.Text = _state.ActiveJobCodeLabel;
        StatusText.Text = _state.StatusText;
        TimerText.Text = _state.ElapsedDisplay;
    }

    private void RefreshTimer()
    {
        _state.Tick();
        TimerText.Text = _state.ElapsedDisplay;
        if (_positionDirty)
        {
            _positionDirty = false;
            _state.SaveWindowPosition(Left, Top);
        }
    }

    private void StateOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(BindStatus);
        if (e.PropertyName is nameof(AppState.ActiveSession) or nameof(AppState.IsRunning))
            _scheduler.OnSessionChanged();
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

    private void OnRecheckDue()
    {
        Dispatcher.Invoke(() =>
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
            }
        });
    }

    private void OnFifteenDue()
    {
        Dispatcher.Invoke(() =>
        {
            if (_scheduler.FifteenOpen) return;
            _scheduler.FifteenOpen = true;
            try
            {
                EnsureVisibleForPrompt();
                var win = new FifteenMinuteWindow { Owner = this };
                win.ShowDialog();
            }
            finally
            {
                Topmost = _state.Settings.AlwaysOnTop;
                _scheduler.FifteenOpen = false;
                _scheduler.ResetFifteen();
            }
        });
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

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _state.SaveWindowPosition(Left, Top);
        _state.Persist();
    }
}

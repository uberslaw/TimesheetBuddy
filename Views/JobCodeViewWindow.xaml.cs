using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class JobCodeViewWindow : Window
{
    private readonly AppState _state = App.State;
    private readonly ObservableCollection<JobCodeRowModel> _rows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public JobCodeViewWindow()
    {
        InitializeComponent();
        RowsList.ItemsSource = _rows;
        Topmost = _state.Settings.AlwaysOnTop;

        _state.SettingsChanged += OnSettingsChanged;
        _state.PropertyChanged += OnStateChanged;
        _timer.Tick += (_, _) =>
        {
            _state.Tick();
            RefreshTotals();
        };

        Loaded += (_, _) =>
        {
            ReloadRows();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _state.SettingsChanged -= OnSettingsChanged;
            _state.PropertyChanged -= OnStateChanged;
        };
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(ReloadRows);

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.ActiveSession) or nameof(AppState.IsRunning))
            Dispatcher.Invoke(RefreshTotals);
    }

    private void ReloadRows()
    {
        var visible = JobCodeViewList.Resolve(_state.JobCodes.ToList(), _state.Settings.JobCodeViewIds);
        _rows.Clear();
        foreach (var code in visible)
        {
            _rows.Add(new JobCodeRowModel
            {
                Id = code.Id,
                Code = code.Code,
                Description = code.Description?.Trim() ?? string.Empty
            });
        }

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Topmost = _state.Settings.AlwaysOnTop;
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        var now = DateTime.UtcNow;
        var today = _state.EntriesForDay(DateTime.Today).ToList();
        var activeId = _state.ActiveSession?.JobCodeId;

        foreach (var row in _rows)
        {
            row.DurationText = DayTotals.FormatClock(DayTotals.ForJobCode(today, row.Id, now));
            row.IsActive = activeId == row.Id;
        }

        TodayTotalText.Text = DayTotals.FormatClock(DayTotals.ForDay(today, now));
        StopButton.IsEnabled = _state.ActiveSession is not null;
    }

    private List<Guid> CurrentOrderedIds() => _rows.Select(row => row.Id).ToList();

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsFromButton(e.OriginalSource as DependencyObject))
            return;
        if (sender is not FrameworkElement { DataContext: JobCodeRowModel row })
            return;

        var code = _state.FindJobCode(row.Id);
        if (code is null)
            return;
        if (_state.ActiveSession?.JobCodeId == code.Id)
            return;

        _state.StartOrSwitch(string.Empty, "General", code);
        RefreshTotals();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: JobCodeRowModel row })
            return;

        var ids = CurrentOrderedIds().Where(id => id != row.Id).ToList();
        _state.SetJobCodeViewIds(ids);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddJobCodeWindow(_state.JobCodes.ToList(), CurrentOrderedIds()) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (dialog.CreateNew)
        {
            var created = _state.AddJobCode(dialog.NewCode ?? string.Empty, dialog.NewDescription ?? string.Empty);
            if (created is null)
            {
                MessageBox.Show(this, "Enter a job code that is not already in the list.",
                    "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ids = CurrentOrderedIds();
            if (!ids.Contains(created.Id))
                ids.Add(created.Id);
            _state.SetJobCodeViewIds(ids);
            return;
        }

        if (dialog.ExistingId is Guid existing && _state.FindJobCode(existing) is not null)
        {
            var ids = CurrentOrderedIds();
            if (!ids.Contains(existing))
                ids.Add(existing);
            _state.SetJobCodeViewIds(ids);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _state.StopActive(save: true);
        RefreshTotals();
    }

    private void Compact_Click(object sender, RoutedEventArgs e)
    {
        var owner = Owner;
        Close();
        if (owner is null)
            return;
        if (owner.WindowState == WindowState.Minimized)
            owner.WindowState = WindowState.Normal;
        owner.Activate();
    }

    private static bool IsFromButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}

public sealed class JobCodeRowModel : INotifyPropertyChanged
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Description { get; init; }

    private string _durationText = "00:00:00";
    public string DurationText
    {
        get => _durationText;
        set => Set(ref _durationText, value);
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

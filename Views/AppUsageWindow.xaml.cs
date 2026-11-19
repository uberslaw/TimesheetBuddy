using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class AppUsageWindow : Window
{
    private readonly ObservableCollection<AppUsageRow> _rows = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    public AppUsageWindow()
    {
        InitializeComponent();
        AppsGrid.ItemsSource = _rows;
        _refresh.Tick += (_, _) => Reload();
        Loaded += (_, _) => _refresh.Start();
        Closed += (_, _) => _refresh.Stop();
        Reload();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private void Reload()
    {
        if (_day > Today) _day = Today;

        DayText.Text = _day == Today
            ? "Today, " + _day.ToString("dddd d MMMM yyyy")
            : _day.ToString("dddd d MMMM yyyy");
        NextDayButton.IsEnabled = _day < Today;
        TodayButton.IsEnabled = _day != Today;
        OffHint.Visibility = App.AppTracker.IsRunning ? Visibility.Collapsed : Visibility.Visible;

        var totals = App.AppTracker.Totals(_day);
        var shares = PercentBreakdown.Shares(totals.Select(t => t.Time).ToList());
        for (var i = 0; i < totals.Count; i++)
        {
            var t = totals[i];
            var percent = shares.Count == totals.Count ? $"{shares[i]}%" : string.Empty;
            var exe = string.IsNullOrEmpty(t.Exe) ? "Program file unknown" : t.Exe;
            if (i < _rows.Count)
                _rows[i].Update(t.Name, exe, DayTotals.FormatCompact(t.Time), percent);
            else
                _rows.Add(new AppUsageRow(t.Name, exe, DayTotals.FormatCompact(t.Time), percent));
        }

        while (_rows.Count > totals.Count)
            _rows.RemoveAt(_rows.Count - 1);

        TotalRun.Text = DayTotals.FormatCompact(totals.Aggregate(TimeSpan.Zero, (sum, t) => sum + t.Time));
        EmptyText.Visibility = totals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PreviousDay_Click(object sender, RoutedEventArgs e)
    {
        _day = _day.AddDays(-1);
        Reload();
    }

    private void NextDay_Click(object sender, RoutedEventArgs e)
    {
        _day = _day.AddDays(1);
        Reload();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _day = Today;
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class AppUsageRow(string name, string exe, string timeText, string percentText) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; private set; } = name;
        public string Exe { get; private set; } = exe;
        public string TimeText { get; private set; } = timeText;
        public string PercentText { get; private set; } = percentText;

        public void Update(string name, string exe, string timeText, string percentText)
        {
            Name = name;
            Exe = exe;
            TimeText = timeText;
            PercentText = percentText;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}

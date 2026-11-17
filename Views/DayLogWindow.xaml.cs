using System.IO;
using System.Windows;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Views;

public partial class DayLogWindow : Window
{
    private readonly DateTime _day;
    private readonly string _notesPath;

    public DayLogWindow() : this(App.State.ViewingDate)
    {
    }

    public DayLogWindow(DateTime dayLocal)
    {
        InitializeComponent();
        _day = dayLocal.Date;

        var folder = Path.GetDirectoryName(App.Store.StoragePath)!;
        _notesPath = Path.Combine(folder, $"day-notes-{_day:yyyyMMdd}.txt");
        if (File.Exists(_notesPath))
            DayNotesBox.Text = File.ReadAllText(_notesPath);

        Title = _day == DateTime.Today
            ? "Today's timesheet — Timesheet Buddy 3.0"
            : $"{_day:dddd d MMM yyyy} — Timesheet Buddy 3.0";

        Reload();
    }

    private void Reload()
    {
        var rows = App.State.EntriesForDay(_day).Select(e => new
        {
            StartLocal = e.StartUtc.ToLocalTime().ToString("HH:mm"),
            EndLocal = e.EndUtc is null ? "…" : e.EndUtc.Value.ToLocalTime().ToString("HH:mm"),
            Duration = FormatDuration(e.Duration),
            Task = e.TaskName,
            Code = JobCode.FormatLabelFromStored(e.JobCodeText),
            Category = e.Category,
            Notes = e.Notes,
            Source = e.Source
        }).ToList();

        EntriesGrid.ItemsSource = rows;
        var total = App.State.EntriesForDay(_day).Aggregate(TimeSpan.Zero, (a, e) => a + e.Duration);
        SummaryText.Text = $"{_day:dddd, d MMM yyyy}  ·  {rows.Count} entries  ·  {FormatDuration(total)} logged";
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours}h {d.Minutes:00}m"
            : $"{d.Minutes}m {d.Seconds:00}s";

    private void AddManual_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ManualEntryWindow { Owner = this };
        if (dialog.ShowDialog() == true)
            Reload();
    }

    private void DayNotesBox_LostFocus(object sender, RoutedEventArgs e)
    {
        File.WriteAllText(_notesPath, DayNotesBox.Text ?? string.Empty);
    }
}

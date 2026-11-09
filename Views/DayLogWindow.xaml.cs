using System.IO;
using System.Windows;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Views;

public partial class DayLogWindow : Window
{
    private readonly string _notesPath;

    public DayLogWindow()
    {
        InitializeComponent();

        var folder = Path.GetDirectoryName(App.Store.StoragePath)!;
        _notesPath = Path.Combine(folder, $"day-notes-{DateTime.Today:yyyyMMdd}.txt");
        if (File.Exists(_notesPath))
            DayNotesBox.Text = File.ReadAllText(_notesPath);

        Reload();
    }

    private void Reload()
    {
        var rows = App.State.EntriesForDay(DateTime.Today).Select(e => new
        {
            StartLocal = e.StartUtc.ToLocalTime().ToString("HH:mm"),
            EndLocal = e.EndUtc is null ? "…" : e.EndUtc.Value.ToLocalTime().ToString("HH:mm"),
            Duration = FormatDuration(e.Duration),
            Task = e.TaskName,
            Code = e.JobCodeText,
            Category = e.Category,
            Notes = e.Notes,
            Source = e.Source
        }).ToList();

        EntriesGrid.ItemsSource = rows;
        var total = App.State.EntriesForDay(DateTime.Today).Aggregate(TimeSpan.Zero, (a, e) => a + e.Duration);
        SummaryText.Text = $"{DateTime.Today:dddd, d MMM yyyy}  ·  {rows.Count} entries  ·  {FormatDuration(total)} logged";
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

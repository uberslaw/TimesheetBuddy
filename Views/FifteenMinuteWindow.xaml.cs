using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Views;

public partial class FifteenMinuteWindow : Window
{
    private readonly ObservableCollection<CatchUpRow> _rows = [];
    private readonly int _intervalMinutes;
    private readonly List<int> _minuteChoices;

    public FifteenMinuteWindow()
    {
        InitializeComponent();

        _intervalMinutes = Math.Max(5, App.State.Settings.FifteenMinuteIntervalMinutes);
        PromptTitle.Text = $"What have you done for the last {_intervalMinutes} minutes?";
        MinutesHint.Text = $"Aim to account for about {_intervalMinutes} minutes total.";

        _minuteChoices = Enumerable.Range(1, Math.Max(_intervalMinutes, 30)).ToList();
        MinutesCol.ItemsSource = _minuteChoices;

        var codes = App.State.JobCodes.Where(j => j.IsActive).ToList();
        CodeCol.ItemsSource = codes;

        var favorites = App.State.FavoriteTasks.ToList();
        CommonTasksCombo.ItemsSource = favorites;
        CommonCol.ItemsSource = favorites;

        var first = CreateDefaultRow();
        first.PropertyChanged += RowOnPropertyChanged;
        _rows.Add(first);
        RowsGrid.ItemsSource = _rows;

        // Stay until the user saves — block casual close.
        Closing += OnClosingGuard;
    }

    private void OnClosingGuard(object? sender, CancelEventArgs e)
    {
        if (DialogResult is null) e.Cancel = true;
    }

    private CatchUpRow CreateDefaultRow()
    {
        var session = App.State.ActiveSession;
        return new CatchUpRow
        {
            Notes = session?.TaskName ?? string.Empty,
            JobCodeId = session?.JobCodeId,
            Minutes = Math.Min(5, _intervalMinutes),
            CommonTaskName = null
        };
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var row = new CatchUpRow { Minutes = 5 };
        row.PropertyChanged += RowOnPropertyChanged;
        _rows.Add(row);
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatchUpRow row } && _rows.Count > 1)
            _rows.Remove(row);
    }

    private void InsertCommon_Click(object sender, RoutedEventArgs e)
    {
        if (CommonTasksCombo.SelectedItem is not FavoriteTask fav) return;

        var row = new CatchUpRow
        {
            CommonTaskName = fav.Name,
            Notes = fav.Name,
            JobCodeId = fav.JobCodeId,
            Minutes = Math.Min(5, _intervalMinutes),
            Category = fav.Category
        };
        row.PropertyChanged += RowOnPropertyChanged;
        _rows.Add(row);
    }

    private void RowOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CatchUpRow row) return;
        if (e.PropertyName != nameof(CatchUpRow.CommonTaskName)) return;
        if (string.IsNullOrWhiteSpace(row.CommonTaskName)) return;

        var fav = App.State.FavoriteTasks.FirstOrDefault(f =>
            string.Equals(f.Name, row.CommonTaskName, StringComparison.OrdinalIgnoreCase));
        if (fav is null) return;

        if (string.IsNullOrWhiteSpace(row.Notes)) row.Notes = fav.Name;
        row.JobCodeId = fav.JobCodeId;
        row.Category = fav.Category;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        RowsGrid.CommitEdit();
        RowsGrid.CommitEdit();

        // Wire common-task changes for any rows edited in-grid.
        foreach (var row in _rows)
        {
            if (!string.IsNullOrWhiteSpace(row.CommonTaskName))
            {
                var fav = App.State.FavoriteTasks.FirstOrDefault(f =>
                    string.Equals(f.Name, row.CommonTaskName, StringComparison.OrdinalIgnoreCase));
                if (fav is not null)
                {
                    if (string.IsNullOrWhiteSpace(row.Notes)) row.Notes = fav.Name;
                    row.JobCodeId ??= fav.JobCodeId;
                    if (string.IsNullOrWhiteSpace(row.Category)) row.Category = fav.Category;
                }
            }
        }

        var total = _rows.Sum(r => r.Minutes);
        if (total <= 0)
        {
            MessageBox.Show(this, "Add at least one row with minutes greater than zero.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = now.AddMinutes(-total);
        var entries = new List<TimeEntry>();

        foreach (var row in _rows.Where(r => r.Minutes > 0))
        {
            var code = App.State.FindJobCode(row.JobCodeId);
            var taskName = !string.IsNullOrWhiteSpace(row.CommonTaskName)
                ? row.CommonTaskName!
                : (string.IsNullOrWhiteSpace(row.Notes) ? "Catch-up entry" : row.Notes.Trim());

            var start = cursor;
            var end = cursor.AddMinutes(row.Minutes);
            cursor = end;

            entries.Add(new TimeEntry
            {
                StartUtc = start,
                EndUtc = end,
                TaskName = taskName,
                Category = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category,
                JobCodeId = row.JobCodeId,
                JobCodeText = code?.Display ?? string.Empty,
                Notes = row.Notes?.Trim() ?? string.Empty,
                ManualMinutes = row.Minutes,
                Source = "fifteen"
            });
        }

        App.State.AddFifteenMinuteRows(entries);
        Closing -= OnClosingGuard;
        DialogResult = true;
        Close();
    }

}

public sealed class CatchUpRow : INotifyPropertyChanged
{
    private string? _commonTaskName;
    private string _notes = string.Empty;
    private Guid? _jobCodeId;
    private int _minutes = 5;
    private string _category = "General";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? CommonTaskName
    {
        get => _commonTaskName;
        set { _commonTaskName = value; OnPropertyChanged(); }
    }

    public string Notes
    {
        get => _notes;
        set { _notes = value; OnPropertyChanged(); }
    }

    public Guid? JobCodeId
    {
        get => _jobCodeId;
        set { _jobCodeId = value; OnPropertyChanged(); }
    }

    public int Minutes
    {
        get => _minutes;
        set { _minutes = value; OnPropertyChanged(); }
    }

    public string Category
    {
        get => _category;
        set { _category = value; OnPropertyChanged(); }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

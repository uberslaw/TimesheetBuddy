using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>
/// Asks where all pending "No Task Selected" time should go. Not now leaves it pending.
/// </summary>
public partial class NoTaskAssignWindow : Window
{
    private readonly ObservableCollection<NoTaskRow> _rows = [];
    private readonly int _pendingMinutes;

    public NoTaskAssignWindow(IReadOnlyList<UtcInterval> open)
    {
        InitializeComponent();

        _pendingMinutes = NoTaskTime.WholeMinutes(NoTaskTime.Total(open));
        PromptTitle.Text = NoTaskTime.PromptText(_pendingMinutes);
        SpansText.Text = DescribeSpans(open);

        MinutesCol.ItemsSource = Enumerable.Range(1, Math.Max(_pendingMinutes, 1)).ToList();
        CodeCol.ItemsSource = App.State.JobCodes.Where(j => j.IsActive).ToList();

        var categories = App.State.Categories.ToList();
        if (categories.Count == 0) categories.Add("General");
        if (!categories.Contains(LockIntervalSplit.BreakCategory, StringComparer.OrdinalIgnoreCase))
            categories.Add(LockIntervalSplit.BreakCategory);
        CategoryCol.ItemsSource = categories;

        var favorites = App.State.FavoriteTasks.ToList();
        CommonTasksCombo.ItemsSource = favorites;
        CommonCol.ItemsSource = favorites;

        AddRow(new NoTaskRow { Minutes = Math.Max(1, _pendingMinutes), Category = DefaultCategory() });
        _rows.CollectionChanged += (_, _) => RefreshHint();
        RowsGrid.ItemsSource = _rows;
        RowsGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshHint);
        RefreshHint();
    }

    private static string DescribeSpans(IReadOnlyList<UtcInterval> open)
    {
        if (open.Count == 0) return string.Empty;

        var parts = open
            .OrderBy(s => s.StartUtc)
            .Select(s =>
            {
                var start = s.StartUtc.ToLocalTime();
                var end = s.EndUtc.ToLocalTime();
                var day = start.Date == DateTime.Today ? string.Empty : start.ToString("ddd d MMM ");
                return $"{day}{start:HH:mm}–{end:HH:mm}";
            })
            .ToList();
        return "No task was running " + string.Join(", ", parts) + ".";
    }

    private static string DefaultCategory()
    {
        var first = App.State.Categories.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        return string.IsNullOrWhiteSpace(first) ? "General" : first;
    }

    private void AddRow(NoTaskRow row)
    {
        row.PropertyChanged += RowOnPropertyChanged;
        _rows.Add(row);
    }

    private void AddRow_Click(object sender, RoutedEventArgs e) =>
        AddRow(new NoTaskRow { Minutes = MinutesForNewRow(), Category = DefaultCategory() });

    private int MinutesForNewRow()
    {
        var remaining = _pendingMinutes - _rows.Sum(r => r.Minutes);
        if (remaining >= 1) return remaining;

        var donor = _rows.LastOrDefault(r => r.Minutes > 1);
        if (donor is not null)
            donor.Minutes -= 1;
        return 1;
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NoTaskRow row } && _rows.Count > 1)
            _rows.Remove(row);
    }

    private void InsertCommon_Click(object sender, RoutedEventArgs e)
    {
        if (CommonTasksCombo.SelectedItem is not FavoriteTask fav) return;

        AddRow(new NoTaskRow
        {
            CommonTaskName = fav.Name,
            TaskName = fav.Name,
            JobCodeId = fav.JobCodeId,
            Minutes = MinutesForNewRow(),
            Category = string.IsNullOrWhiteSpace(fav.Category) ? DefaultCategory() : fav.Category
        });
    }

    private void RowOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NoTaskRow row) return;
        if (e.PropertyName == nameof(NoTaskRow.CommonTaskName))
            ApplyCommonTask(row);

        RefreshHint();
    }

    private static void ApplyCommonTask(NoTaskRow row)
    {
        var fav = FindFavorite(row.CommonTaskName);
        if (fav is null) return;

        row.TaskName = fav.Name;
        row.JobCodeId = fav.JobCodeId;
        if (!string.IsNullOrWhiteSpace(fav.Category))
            row.Category = fav.Category;
    }

    private static FavoriteTask? FindFavorite(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return App.State.FavoriteTasks.FirstOrDefault(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshHint()
    {
        var total = _rows.Sum(r => r.Minutes);
        if (total <= 0)
        {
            MinutesHint.Text = $"Assign at least 1 minute, or choose Not now. {_pendingMinutes} waiting.";
            return;
        }

        if (total > _pendingMinutes)
        {
            MinutesHint.Text = $"Assigned {total} of {_pendingMinutes}. That is more than the No Task Selected time.";
            return;
        }

        var left = _pendingMinutes - total;
        MinutesHint.Text = left == 0
            ? $"Assigned {total} of {_pendingMinutes} minutes."
            : $"Assigned {total} of {_pendingMinutes} minutes. {left} stay as No Task Selected.";
    }

    private void AllToBreak_Click(object sender, RoutedEventArgs e) =>
        Commit(
        [
            new CatchUpSlice
            {
                TaskName = LockIntervalSplit.BreakTaskName,
                Category = LockIntervalSplit.BreakCategory,
                Minutes = _pendingMinutes
            }
        ]);

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var slices = new List<CatchUpSlice>();
        foreach (var row in _rows.Where(r => r.Minutes > 0))
        {
            ApplyFavoriteDefaults(row);
            var code = App.State.FindJobCode(row.JobCodeId);
            var taskName = FirstText(row.TaskName, row.CommonTaskName, row.Notes, code?.Description, code?.Code);
            if (taskName is null)
            {
                MessageBox.Show(this, "Give each row a task name, a common task, or a job code.",
                    "No Task Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            slices.Add(new CatchUpSlice
            {
                TaskName = taskName,
                Category = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category.Trim(),
                JobCodeId = code?.Id,
                JobCodeText = code?.Display ?? string.Empty,
                Notes = row.Notes?.Trim() ?? string.Empty,
                Minutes = row.Minutes
            });
        }

        var total = slices.Sum(s => s.Minutes);
        if (total <= 0 || total > _pendingMinutes)
        {
            MessageBox.Show(this,
                total <= 0
                    ? "Assign at least 1 minute, or choose Not now."
                    : $"Assigned minutes cannot be more than the {_pendingMinutes} No Task Selected minutes.",
                "No Task Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshHint();
            return;
        }

        Commit(slices);
    }

    private void Commit(IReadOnlyList<CatchUpSlice> slices)
    {
        if (_pendingMinutes <= 0)
        {
            DialogResult = false;
            Close();
            return;
        }

        var error = App.State.AssignNoTaskTime(slices);
        if (error is not null)
        {
            MessageBox.Show(this, error, "No Task Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void NotNow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static void ApplyFavoriteDefaults(NoTaskRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.CommonTaskName))
        {
            ApplyCommonTask(row);
            return;
        }

        if (row.JobCodeId is not null) return;
        var fav = FindFavorite(row.TaskName);
        if (fav is null) return;

        row.JobCodeId = fav.JobCodeId;
        if (!string.IsNullOrWhiteSpace(fav.Category) &&
            (string.IsNullOrWhiteSpace(row.Category) || row.Category == "General"))
            row.Category = fav.Category;
    }

    private static string? FirstText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}

public sealed class NoTaskRow : INotifyPropertyChanged
{
    private string? _commonTaskName;
    private string _taskName = string.Empty;
    private string _notes = string.Empty;
    private Guid? _jobCodeId;
    private int _minutes = 1;
    private string _category = "General";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? CommonTaskName
    {
        get => _commonTaskName;
        set { _commonTaskName = value; OnPropertyChanged(); }
    }

    public string TaskName
    {
        get => _taskName;
        set { _taskName = value; OnPropertyChanged(); }
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

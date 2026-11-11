using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class FifteenMinuteWindow : Window
{
    private readonly ObservableCollection<CatchUpRow> _rows = [];
    private readonly UnaccountedWindow _window;
    private readonly int _unaccountedMinutes;
    private readonly List<int> _minuteChoices;

    public FifteenMinuteWindow(UnaccountedWindow window)
    {
        InitializeComponent();

        _window = window;
        _unaccountedMinutes = Math.Max(0, window.UnaccountedMinutes);
        PromptTitle.Text = UnaccountedTime.AssignPromptText(_unaccountedMinutes);

        _minuteChoices = Enumerable.Range(1, Math.Max(_unaccountedMinutes, 1)).ToList();
        MinutesCol.ItemsSource = _minuteChoices;

        var codes = App.State.JobCodes.Where(j => j.IsActive).ToList();
        CodeCol.ItemsSource = codes;

        var categories = App.State.Categories.ToList();
        if (categories.Count == 0) categories.Add("General");
        CategoryCol.ItemsSource = categories;

        var favorites = App.State.FavoriteTasks.ToList();
        CommonTasksCombo.ItemsSource = favorites;
        CommonCol.ItemsSource = favorites;

        var first = CreateDefaultRow();
        first.PropertyChanged += RowOnPropertyChanged;
        _rows.Add(first);
        _rows.CollectionChanged += (_, _) => RefreshAssignmentState();
        RowsGrid.ItemsSource = _rows;
        RowsGrid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(RefreshAssignmentState);
        RefreshAssignmentState();
    }

    private CatchUpRow CreateDefaultRow()
    {
        return new CatchUpRow
        {
            Minutes = Math.Max(1, _unaccountedMinutes),
            Category = DefaultCategory(),
            CommonTaskName = null
        };
    }

    private static string DefaultCategory()
    {
        var first = App.State.Categories.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        return string.IsNullOrWhiteSpace(first) ? "General" : first;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var row = new CatchUpRow
        {
            Minutes = MinutesForNewRow(),
            Category = DefaultCategory()
        };
        row.PropertyChanged += RowOnPropertyChanged;
        _rows.Add(row);
    }

    private int MinutesForNewRow()
    {
        var remaining = _unaccountedMinutes - _rows.Sum(r => r.Minutes);
        if (remaining >= 1) return remaining;

        var donor = _rows.LastOrDefault(r => r.Minutes > 1);
        if (donor is not null)
        {
            donor.Minutes -= 1;
            return 1;
        }

        return 1;
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
            TaskName = fav.Name,
            JobCodeId = fav.JobCodeId,
            Minutes = MinutesForNewRow(),
            Category = string.IsNullOrWhiteSpace(fav.Category) ? DefaultCategory() : fav.Category
        };
        row.PropertyChanged += RowOnPropertyChanged;
        _rows.Add(row);
    }

    private void RowOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CatchUpRow row) return;
        if (e.PropertyName == nameof(CatchUpRow.CommonTaskName))
            ApplyCommonTask(row);

        RefreshAssignmentState();
    }

    private void ApplyCommonTask(CatchUpRow row)
    {
        if (string.IsNullOrWhiteSpace(row.CommonTaskName)) return;
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

    private void RefreshAssignmentState()
    {
        var total = _rows.Sum(r => r.Minutes);
        if (total <= 0)
        {
            MinutesHint.Text = $"Assign at least 1 minute, or choose Not now. {_unaccountedMinutes} unaccounted.";
            return;
        }

        if (total > _unaccountedMinutes)
        {
            MinutesHint.Text = $"Assigned {total} of {_unaccountedMinutes}. That is more than the unaccounted time.";
            return;
        }

        var left = _unaccountedMinutes - total;
        MinutesHint.Text = left == 0
            ? $"Assigned {total} of {_unaccountedMinutes} minutes."
            : $"Assigned {total} of {_unaccountedMinutes} minutes. {left} can stay unaccounted.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);

        foreach (var row in _rows)
            ApplyFavoriteDefaults(row);

        var total = _rows.Sum(r => r.Minutes);
        if (total <= 0)
        {
            MessageBox.Show(this, "Assign at least 1 minute, or choose Not now.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAssignmentState();
            return;
        }

        if (total > _unaccountedMinutes)
        {
            MessageBox.Show(this,
                $"Assigned minutes cannot exceed the {_unaccountedMinutes} unaccounted minutes.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshAssignmentState();
            return;
        }

        var slices = new List<CatchUpSlice>();
        foreach (var row in _rows.Where(r => r.Minutes > 0))
        {
            var code = App.State.FindJobCode(row.JobCodeId);
            var taskName = FirstText(row.TaskName, row.CommonTaskName, row.Notes, code?.Description, code?.Code)
                           ?? "Catch-up entry";
            slices.Add(new CatchUpSlice
            {
                TaskName = taskName,
                Category = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category.Trim(),
                JobCodeId = row.JobCodeId,
                JobCodeText = code?.Display ?? string.Empty,
                Notes = row.Notes?.Trim() ?? string.Empty,
                Minutes = row.Minutes
            });
        }

        // Place into the gaps measured when the prompt opened, not "now",
        // so a pause on this dialog cannot overlap the running timer.
        App.State.AddCatchUpAssignments(slices, _window);
        DialogResult = true;
        Close();
    }

    private void ApplyFavoriteDefaults(CatchUpRow row)
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

public sealed class CatchUpRow : INotifyPropertyChanged
{
    private string? _commonTaskName;
    private string _taskName = string.Empty;
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

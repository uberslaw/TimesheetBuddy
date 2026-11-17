using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class LockPromptWindow : Window
{
    private readonly ObservableCollection<LockRow> _rows = [];
    private readonly TimeSpan _lockedFor;
    private readonly List<int> _minuteChoices;

    public LockPromptWindow(string message, TimeSpan lockedFor, IReadOnlyList<JobCode> codes, IReadOnlyList<string> categories)
    {
        InitializeComponent();
        MessageText.Text = message;
        _lockedFor = lockedFor < TimeSpan.Zero ? TimeSpan.Zero : lockedFor;

        var maxMinutes = Math.Max(1, (int)Math.Ceiling(_lockedFor.TotalMinutes));
        _minuteChoices = Enumerable.Range(1, Math.Max(maxMinutes, 60)).ToList();
        MinutesCol.ItemsSource = _minuteChoices;
        CodeCol.ItemsSource = codes.Where(c => c.IsActive).ToList();
        CategoryCol.ItemsSource = categories.ToList();

        MinutesHint.Text = $"Locked for {LockIntervalSplit.FormatLockClock(_lockedFor)} — optional rows should not exceed that.";
        _rows.Add(new LockRow { Minutes = Math.Min(5, maxMinutes), Category = categories.FirstOrDefault() ?? "General" });
        RowsGrid.ItemsSource = _rows;

        Closing += OnClosingGuard;
    }

    public IReadOnlyList<LockIntervalSplit.AssignmentSlice> Assignments { get; private set; } = [];

    private void OnClosingGuard(object? sender, CancelEventArgs e)
    {
        if (DialogResult is null)
            e.Cancel = true;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e) =>
        _rows.Add(new LockRow
        {
            Minutes = 5,
            Category = App.State.Categories.FirstOrDefault() ?? "General"
        });

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LockRow row } && _rows.Count > 0)
            _rows.Remove(row);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        RowsGrid.CommitEdit();
        RowsGrid.CommitEdit();

        var slices = new List<LockIntervalSplit.AssignmentSlice>();
        foreach (var row in _rows.Where(r => r.Minutes > 0 && !string.IsNullOrWhiteSpace(r.TaskName)))
        {
            var code = App.State.FindJobCode(row.JobCodeId);
            slices.Add(new LockIntervalSplit.AssignmentSlice
            {
                TaskName = row.TaskName.Trim(),
                Category = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category.Trim(),
                JobCodeId = row.JobCodeId,
                JobCodeText = code?.Display ?? string.Empty,
                Duration = TimeSpan.FromMinutes(row.Minutes)
            });
        }

        var used = slices.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration);
        if (used > _lockedFor + TimeSpan.FromSeconds(30))
        {
            MessageBox.Show(this,
                $"Those rows add up to more than the lock ({LockIntervalSplit.FormatLockClock(_lockedFor)}).",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Assignments = slices;
        DialogResult = true;
        Close();
    }

    private sealed class LockRow : INotifyPropertyChanged
    {
        private string _taskName = string.Empty;
        private string _category = "General";
        private Guid? _jobCodeId;
        private int _minutes = 5;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string TaskName
        {
            get => _taskName;
            set { _taskName = value; OnPropertyChanged(); }
        }

        public string Category
        {
            get => _category;
            set { _category = value; OnPropertyChanged(); }
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

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

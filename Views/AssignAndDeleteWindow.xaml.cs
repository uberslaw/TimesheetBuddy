using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class AssignAndDeleteWindow : Window
{
    private readonly ObservableCollection<SegmentRow> _rows = [];
    private readonly string _sourceKey;
    private readonly IReadOnlyList<JobCode> _codes;

    public AssignAndDeleteWindow(
        TaskTypeRow source,
        IReadOnlyList<TaskTypeRow> otherTasks,
        IReadOnlyList<JobCode> codes,
        IReadOnlyList<string> categories)
    {
        InitializeComponent();
        _sourceKey = source.Key;
        _codes = codes;

        IntroText.Text =
            $"Assign time from “{TicketRules.Label(source.Ticket, source.TaskName)}” ({source.Category} · {JobCode.FormatLabelFromStored(source.JobCodeText)}) " +
            $"— {source.SegmentCount} portion{(source.SegmentCount == 1 ? "" : "s")}, {DayTotals.FormatCompact(source.Total)} total.";

        var options = otherTasks
            .Where(t => t.Key != source.Key)
            .Select(ExistingTaskOption.From)
            .ToList();

        BulkTargetCombo.ItemsSource = options;
        if (options.Count > 0)
            BulkTargetCombo.SelectedIndex = 0;

        ExistingCol.ItemsSource = options;
        CategoryCol.ItemsSource = categories.ToList();
        CodeCol.ItemsSource = codes.Where(c => c.IsActive).ToList();

        foreach (var segment in source.Segments)
        {
            _rows.Add(new SegmentRow(segment, categories.FirstOrDefault() ?? "General", OnExistingPicked));
        }

        RowsGrid.ItemsSource = _rows;
    }

    public IReadOnlyList<TaskTypeReassign.Assignment> Assignments { get; private set; } = [];

    private void OnExistingPicked(SegmentRow row, ExistingTaskOption? option)
    {
        if (option is null) return;
        row.ApplyOption(option);
    }

    private void ApplyToAll_Click(object sender, RoutedEventArgs e)
    {
        if (BulkTargetCombo.SelectedItem is not ExistingTaskOption option)
        {
            MessageBox.Show(this,
                "Pick an existing task first, or fill each portion’s destination manually.",
                "Assign time and Delete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        foreach (var row in _rows)
            row.ApplyOption(option);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        RowsGrid.CommitEdit();
        RowsGrid.CommitEdit();

        var assignments = new List<TaskTypeReassign.Assignment>();
        foreach (var row in _rows)
        {
            var name = row.TaskName?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                MessageBox.Show(this,
                    "Every portion needs a destination task name.",
                    "Assign time and Delete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var code = _codes.FirstOrDefault(c => c.Id == row.JobCodeId);
            var codeText = code?.Display ?? row.ResolvedJobCodeText;
            var category = string.IsNullOrWhiteSpace(row.Category) ? "General" : row.Category.Trim();
            var ticket = row.TicketFor(name, category);
            var destKey = TaskTypeSummary.KeyOf(name, category, row.JobCodeId, codeText, ticket);
            if (destKey == _sourceKey)
            {
                MessageBox.Show(this,
                    "Choose a different task for each portion — the source task is being deleted.",
                    "Assign time and Delete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            assignments.Add(new TaskTypeReassign.Assignment
            {
                EntryId = row.EntryId,
                TaskName = name,
                Category = category,
                JobCodeId = row.JobCodeId,
                JobCodeText = codeText,
                Ticket = ticket
            });
        }

        Assignments = assignments;
        DialogResult = true;
        Close();
    }

    public sealed record ExistingTaskOption(
        string Key,
        string TaskName,
        string Category,
        Guid? JobCodeId,
        string JobCodeText,
        string Display,
        string Ticket = "")
    {
        public static ExistingTaskOption From(TaskTypeRow row) => new(
            row.Key,
            row.TaskName,
            row.Category,
            row.JobCodeId,
            row.JobCodeText,
            $"{TicketRules.Label(row.Ticket, row.TaskName)} · {row.Category} · {JobCode.FormatLabelFromStored(row.JobCodeText)}",
            row.Ticket);
    }

    private sealed class SegmentRow : INotifyPropertyChanged
    {
        private readonly Action<SegmentRow, ExistingTaskOption?> _onExistingPicked;
        private ExistingTaskOption? _existingTarget;
        private string _taskName = string.Empty;
        private string _category;
        private Guid? _jobCodeId;
        private string _jobCodeText = string.Empty;
        private bool _suppressExisting;

        public SegmentRow(
            TaskSegment segment,
            string defaultCategory,
            Action<SegmentRow, ExistingTaskOption?> onExistingPicked)
        {
            EntryId = segment.EntryId;
            _category = defaultCategory;
            _onExistingPicked = onExistingPicked;

            var startLocal = segment.StartUtc.ToLocalTime();
            var endLocal = startLocal + segment.Duration;
            WhenDisplay = segment.IsRunning
                ? $"{startLocal:HH:mm}–…"
                : $"{startLocal:HH:mm}–{endLocal:HH:mm}";
            DurationDisplay = DayTotals.FormatCompact(segment.Duration);
        }

        public Guid EntryId { get; }
        public string WhenDisplay { get; }
        public string DurationDisplay { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ExistingTaskOption? ExistingTarget
        {
            get => _existingTarget;
            set
            {
                if (Equals(_existingTarget, value)) return;
                _existingTarget = value;
                OnPropertyChanged();
                if (!_suppressExisting)
                    _onExistingPicked(this, value);
            }
        }

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
            set
            {
                _jobCodeId = value;
                OnPropertyChanged();
            }
        }

        public string ResolvedJobCodeText => _jobCodeText;

        /// <summary>The picked task's ticket, kept only while the name and category still match it.</summary>
        public string TicketFor(string name, string category) =>
            ExistingTarget is { } target &&
            string.Equals(target.TaskName.Trim(), name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(target.Category.Trim(), category, StringComparison.OrdinalIgnoreCase)
                ? target.Ticket
                : string.Empty;

        public void ApplyOption(ExistingTaskOption option)
        {
            _suppressExisting = true;
            ExistingTarget = option;
            _suppressExisting = false;
            TaskName = option.TaskName;
            Category = option.Category;
            _jobCodeText = option.JobCodeText;
            JobCodeId = option.JobCodeId;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

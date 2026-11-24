using System.Windows;
using System.Windows.Input;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>
/// A job code (or "No job code") that a row's time can move to. For No Task Selected time a target can
/// also be a whole task (<see cref="TaskName"/> set); a code-only target then asks for a task name.
/// </summary>
public sealed record MoveTarget(Guid? JobCodeId, string JobCodeText, string Display)
{
    public string? TaskName { get; init; }
    public string? Category { get; init; }
    public string Ticket { get; init; } = string.Empty;
    public string? SuggestedTaskName { get; init; }
}

/// <summary>Right-click → Move Time…: pick a code, confirm, and the row's time moves there.</summary>
public partial class MoveTimeWindow : Window
{
    private readonly IReadOnlyList<MoveTarget> _targets;
    private readonly string _sourceLabel;
    private readonly TimeSpan _total;
    private readonly int _segments;
    private readonly bool _noTask;
    private MoveTarget? _suggestedFor;

    public MoveTimeWindow(
        string taskName,
        string sourceCodeLabel,
        TimeSpan total,
        int segments,
        IReadOnlyList<MoveTarget> targets)
    {
        InitializeComponent();
        _targets = targets;
        _sourceLabel = $"“{taskName}” ({sourceCodeLabel})";
        _total = total;
        _segments = segments;
        TaskLabel.Text = $"Move time from “{taskName}”";
        SourceLabel.Text = $"{sourceCodeLabel} · {DayTotals.FormatCompact(total)} in {SegmentText(segments)}";
        ApplyFilter();
        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Move Time… on the No Task Selected row: to one of the day's tasks, Break, or a code as a new task.</summary>
    public MoveTimeWindow(TimeSpan total, int spans, IReadOnlyList<MoveTarget> targets, IReadOnlyList<string> categories)
    {
        InitializeComponent();
        _noTask = true;
        _targets = targets;
        _sourceLabel = NoTaskTime.Label;
        _total = total;
        _segments = spans;
        Title = "Move No Task Selected time";
        TaskLabel.Text = $"Move {NoTaskTime.Label} time";
        SourceLabel.Text = $"{DayTotals.FormatCompact(total)} in {SpanText(spans)} · logged at the original clock times";
        TargetHeader.Text = "Move to a task, or a job code as a new task";
        FilterBox.ToolTip = "Type to filter the tasks and job codes";
        EmptyText.Text = "Nothing matches";
        NewCategoryBox.ItemsSource = categories;
        NewCategoryBox.Text = categories.FirstOrDefault() ?? "General";
        ApplyFilter();
        Loaded += (_, _) => FilterBox.Focus();
    }

    public MoveTarget? SelectedTarget { get; private set; }

    private static string SegmentText(int n) => n == 1 ? "1 segment" : $"{n} segments";

    private static string SpanText(int n) => n == 1 ? "1 span" : $"{n} spans";

    private void ApplyFilter()
    {
        var text = FilterBox.Text?.Trim() ?? string.Empty;
        var visible = text.Length == 0
            ? _targets.ToList()
            : _targets.Where(t => t.Display.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = TargetList.SelectedItem as MoveTarget;
        TargetList.ItemsSource = visible;
        TargetList.SelectedItem = keep is not null && visible.Contains(keep) ? keep : visible.FirstOrDefault();
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter();

    private void TargetList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_noTask) return;
        var target = TargetList.SelectedItem as MoveTarget;
        var needsTask = target is { TaskName: null };
        NewTaskPanel.Visibility = needsTask ? Visibility.Visible : Visibility.Collapsed;
        if (!needsTask || ReferenceEquals(target, _suggestedFor)) return;

        // Keep a name the user typed; replace only the previous suggestion.
        var current = NewTaskNameBox.Text?.Trim() ?? string.Empty;
        if (current.Length == 0 || current == _suggestedFor?.SuggestedTaskName)
            NewTaskNameBox.Text = target!.SuggestedTaskName ?? string.Empty;
        _suggestedFor = target;
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var count = TargetList.Items.Count;
        if (count == 0) return;
        if (e.Key == Key.Down)
        {
            TargetList.SelectedIndex = Math.Min(count - 1, TargetList.SelectedIndex + 1);
            TargetList.ScrollIntoView(TargetList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            TargetList.SelectedIndex = Math.Max(0, TargetList.SelectedIndex - 1);
            TargetList.ScrollIntoView(TargetList.SelectedItem);
            e.Handled = true;
        }
    }

    private void TargetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TargetList.SelectedItem is MoveTarget)
            ConfirmAndClose();
    }

    private void Move_Click(object sender, RoutedEventArgs e) => ConfirmAndClose();

    private void ConfirmAndClose()
    {
        if (TargetList.SelectedItem is not MoveTarget target)
        {
            MessageBox.Show(this, _noTask ? "Pick a task or job code to move the time to." : "Pick a job code to move the time to.",
                "Move time", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string question;
        if (_noTask)
        {
            if (target.TaskName is null)
            {
                var name = NewTaskNameBox.Text?.Trim() ?? string.Empty;
                if (name.Length == 0)
                {
                    MessageBox.Show(this, "Enter a task name for the new task.", "Move time",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    NewTaskNameBox.Focus();
                    return;
                }

                var category = NewCategoryBox.Text?.Trim() ?? string.Empty;
                target = target with { TaskName = name, Category = category.Length == 0 ? "General" : category };
            }

            var code = JobCodeLabel(target.JobCodeText);
            question = $"Move {DayTotals.FormatCompact(_total)} ({SpanText(_segments)}) of {NoTaskTime.Label} time to " +
                       $"“{TicketRules.Label(target.Ticket, target.TaskName)}” ({target.Category}, {code})?";
        }
        else
        {
            question = $"Move {DayTotals.FormatCompact(_total)} ({SegmentText(_segments)}) from {_sourceLabel} to {target.Display}?";
        }

        var confirm = MessageBox.Show(this, question, "Move time", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SelectedTarget = target;
        DialogResult = true;
        Close();
    }

    private static string JobCodeLabel(string? stored)
    {
        var label = Models.JobCode.FormatLabelFromStored(stored);
        return label == "—" ? "No job code" : label;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

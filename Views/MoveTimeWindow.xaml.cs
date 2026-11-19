using System.Windows;
using System.Windows.Input;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>A job code (or "No job code") that a row's time can move to.</summary>
public sealed record MoveTarget(Guid? JobCodeId, string JobCodeText, string Display);

/// <summary>Right-click → Move Time…: pick a code, confirm, and the row's time moves there.</summary>
public partial class MoveTimeWindow : Window
{
    private readonly IReadOnlyList<MoveTarget> _targets;
    private readonly string _sourceLabel;
    private readonly TimeSpan _total;
    private readonly int _segments;

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

    public MoveTarget? SelectedTarget { get; private set; }

    private static string SegmentText(int n) => n == 1 ? "1 segment" : $"{n} segments";

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
            MessageBox.Show(this, "Pick a job code to move the time to.", "Move time",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Move {DayTotals.FormatCompact(_total)} ({SegmentText(_segments)}) from {_sourceLabel} to {target.Display}?",
            "Move time",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SelectedTarget = target;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

using System.Windows;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>
/// After Yes: the other PC's entries for today and its running timer, each with a checkbox. Keep selected merges
/// the ticked ones; Discard all keeps nothing. Non-modal, so it never holds up closing the app; closing it with ✕
/// keeps nothing.
/// </summary>
public partial class KeepDiscardWindow : Window
{
    private readonly List<Row> _rows;
    private bool _answered;

    public KeepDiscardWindow(string machine, IReadOnlyList<OverlapMerge.Item> items)
    {
        InitializeComponent();
        Machine = machine;
        Items = items;
        _rows = items.Select(i => new Row(i)).ToList();
        ItemsList.ItemsSource = _rows;

        var running = items.FirstOrDefault(i => i.IsRunning && !i.AlreadyHere);
        var finished = items.Count(i => !i.IsRunning && !i.AlreadyHere);
        var listed = finished == 0 ? "no entries for today that this PC doesn't already have" : "these are today's entries";
        HeaderText.Text = running is null
            ? $"Timesheet Buddy is open on {machine}. {Capitalize(listed)}; nothing is running there."
            : $"Timesheet Buddy is open on {machine}. {Capitalize(listed)}, and this one is active: " +
              $"{ChangeJournal.Describe(running.Entry.TaskName, running.Entry.JobCodeText, running.Entry.Ticket)}, " +
              $"{DayTotals.FormatCompact(running.Entry.Duration)} so far.";

        var already = items.Count(i => i.AlreadyHere);
        if (already > 0)
        {
            AlreadyText.Text = $"{already} of {machine}'s entries are already in this timesheet (greyed out).";
            AlreadyText.Visibility = Visibility.Visible;
        }

        KeepButton.IsEnabled = _rows.Any(r => r.CanSelect);
        Closed += (_, _) =>
        {
            if (!_answered) Finished?.Invoke(new HashSet<Guid>());
        };
    }

    public string Machine { get; }

    public IReadOnlyList<OverlapMerge.Item> Items { get; }

    /// <summary>The ids to keep (empty for Discard all or ✕).</summary>
    public event Action<IReadOnlySet<Guid>>? Finished;

    /// <summary>Closes without an answer (the app is closing); keeps nothing.</summary>
    public void Dismiss()
    {
        _answered = true;
        Close();
    }

    private void Keep_Click(object sender, RoutedEventArgs e) =>
        Finish(_rows.Where(r => r.CanSelect && r.IsSelected).Select(r => r.Item.Id).ToHashSet());

    private void Discard_Click(object sender, RoutedEventArgs e) => Finish(new HashSet<Guid>());

    private void Finish(IReadOnlySet<Guid> keep)
    {
        if (_answered) return;
        _answered = true;
        Finished?.Invoke(keep);
        Close();
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private sealed class Row(OverlapMerge.Item item)
    {
        public OverlapMerge.Item Item { get; } = item;
        public bool CanSelect { get; } = !item.AlreadyHere;
        public bool IsSelected { get; set; } = !item.AlreadyHere;

        public string Label { get; } =
            (item.IsRunning ? "▶ Running: " : string.Empty) +
            ChangeJournal.Describe(item.Entry.TaskName, item.Entry.JobCodeText, item.Entry.Ticket) +
            $" · {item.Entry.StartUtc.ToLocalTime():HH:mm}–{(item.Entry.EndUtc ?? DateTime.UtcNow).ToLocalTime():HH:mm}" +
            (item.AlreadyHere ? " (already here)" : string.Empty);

        public string TimeText { get; } = DayTotals.FormatCompact(item.Entry.Duration);
    }
}

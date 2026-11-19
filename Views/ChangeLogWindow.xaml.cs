using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class ChangeLogWindow : Window
{
    private static readonly (string Label, int Days)[] Ranges =
    [
        ("Today", 0),
        ("Last 7 days", 7),
        ("Last 30 days", 30),
        ("Last 90 days", 90)
    ];

    private bool _ready;

    public ChangeLogWindow()
    {
        InitializeComponent();
        foreach (var (label, _) in Ranges) RangeCombo.Items.Add(label);
        RangeCombo.SelectedIndex = 1;
        _ready = true;
        Reload();
    }

    private void Reload()
    {
        if (!_ready) return;
        var days = Ranges[Math.Max(0, RangeCombo.SelectedIndex)].Days;
        var sinceLocal = days == 0 ? DateTime.Today : DateTime.Today.AddDays(-days);
        var lines = ChangeJournal.ReadRecent(App.Store.Folder, sinceLocal.ToUniversalTime());
        if (ThisPcOnlyCheck.IsChecked == true)
            lines = lines.Where(l => l.ClientId == App.Client.ClientId).ToList();

        LinesGrid.ItemsSource = lines.Select(l => new Row(l, l.ClientId == App.Client.ClientId)).ToList();
        var machines = lines.Select(l => l.Machine).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        SummaryText.Text = lines.Count == 0
            ? $"This PC: {App.Client.Machine}"
            : $"{lines.Count} change{(lines.Count == 1 ? "" : "s")} from {machines} PC{(machines == 1 ? "" : "s")} · this PC: {App.Client.Machine}";
        EmptyText.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Range_Changed(object sender, SelectionChangedEventArgs e) => Reload();

    private void Filter_Changed(object sender, RoutedEventArgs e) => Reload();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = ChangeJournal.FolderFor(App.Store.Folder);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open the folder.\n\n" + ex.Message, "Change log",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class Row(JournalLine line, bool mine)
    {
        public string When { get; } = line.TimeUtc.ToLocalTime().ToString("ddd d MMM HH:mm:ss");
        public string Machine { get; } = mine ? line.Machine + " (this PC)" : line.Machine;
        public string Action { get; } = line.Action;
        public string Summary { get; } = line.Summary;
        public string Tooltip { get; } = line.EntryId is Guid id
            ? $"{line.Summary}\nEntry {id}\nClient {line.ClientId}"
            : $"{line.Summary}\nClient {line.ClientId}";
    }
}

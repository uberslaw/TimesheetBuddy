using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class WeekBrowserWindow : Window
{
    private readonly ObservableCollection<WeekChoice> _choices = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _suppressSelection;

    public WeekBrowserWindow()
    {
        InitializeComponent();
        WeekList.ItemsSource = _choices;
        Topmost = App.State.Settings.AlwaysOnTop;
        _refreshTimer.Tick += (_, _) =>
        {
            if (HasOpenTimer())
                LoadWeeks(preserve: true);
        };
        Loaded += (_, _) =>
        {
            LoadWeeks(preserve: false);
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    private void LoadWeeks(bool preserve)
    {
        var selectedMonday = preserve && WeekList.SelectedItem is WeekChoice selected
            ? selected.Week.Monday
            : (DateTime?)null;
        var previouslyChecked = _choices.Where(choice => choice.IsChecked)
            .Select(choice => choice.Week.Monday)
            .ToHashSet();

        var weeks = WeekCalendar.Collect(App.State.Settings.Entries, DateTime.Now);

        _suppressSelection = true;
        _choices.Clear();
        foreach (var week in weeks)
        {
            _choices.Add(new WeekChoice(week)
            {
                IsChecked = previouslyChecked.Contains(week.Monday)
            });
        }
        _suppressSelection = false;

        var pick = selectedMonday is DateTime monday
            ? _choices.FirstOrDefault(choice => choice.Week.Monday == monday)
            : null;
        pick ??= _choices.FirstOrDefault(choice => choice.Week.Monday == WeekCalendar.MondayOf(DateTime.Today));
        pick ??= _choices.LastOrDefault();

        WeekList.SelectedItem = pick;
        if (pick is not null)
            WeekList.ScrollIntoView(pick);
        else
            ShowOpenWeek(null);
    }

    private void WeekList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;

        ShowOpenWeek(WeekList.SelectedItem as WeekChoice);
    }

    private void ShowOpenWeek(WeekChoice? choice)
    {
        var index = choice is null ? -1 : WeekList.SelectedIndex;
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index >= 0 && index < _choices.Count - 1;
        PositionText.Text = index >= 0 ? $"{index + 1} of {_choices.Count}" : string.Empty;

        if (choice is null)
        {
            OpenLabel.Text = "No weeks";
            OpenSummary.Text = string.Empty;
            DayList.ItemsSource = null;
        }
        else
        {
            OpenLabel.Text = choice.Label;
            OpenSummary.Text = choice.Summary;
            DayList.ItemsSource = choice.Week.BrowseDays.ToList();
        }

        UpdateExportHint();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int delta)
    {
        var next = WeekList.SelectedIndex + delta;
        if (next < 0 || next >= _choices.Count)
            return;

        WeekList.SelectedIndex = next;
        if (WeekList.SelectedItem is not null)
            WeekList.ScrollIntoView(WeekList.SelectedItem);
    }

    private void WeekCheck_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not WeekChoice choice)
            return;

        // Toggle without moving the open week or clearing any other checkbox.
        choice.IsChecked = !choice.IsChecked;
        UpdateExportHint();
        e.Handled = true;
    }

    private void UpdateExportHint()
    {
        var checkedCount = _choices.Count(choice => choice.IsChecked);
        ExportHint.Text = checkedCount switch
        {
            0 => "No weeks checked — export uses the open week.",
            1 => "Export includes 1 checked week.",
            _ => $"Export includes {checkedCount} checked weeks."
        };
    }

    private void ExportPdf_Click(object sender, RoutedEventArgs e) =>
        ExportToFile(TimesheetExport.WritePdf, "PDF document (*.pdf)|*.pdf", ".pdf", "Export timesheet to PDF");

    private void ExportExcel_Click(object sender, RoutedEventArgs e) =>
        ExportToFile(TimesheetExport.WriteExcel, "Excel workbook (*.xlsx)|*.xlsx", ".xlsx", "Export timesheet to Excel");

    private void ExportToFile(
        Action<string, IReadOnlyList<WorkWeek>> write,
        string filter,
        string extension,
        string title)
    {
        var weeks = WeeksToExport();
        if (weeks.Count == 0)
        {
            MessageBox.Show(this, "There is no week to export.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = TimesheetExport.DefaultFileName(weeks, extension),
            DefaultExt = extension.TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = true,
            Title = title,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            write(dialog.FileName, weeks);
            var noun = weeks.Count == 1 ? "week" : "weeks";
            MessageBox.Show(this, $"Exported {weeks.Count} {noun} to:\n{dialog.FileName}",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The export could not be saved.\n\n" + ex.Message,
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private IReadOnlyList<WorkWeek> WeeksToExport()
    {
        var picked = _choices.Where(choice => choice.IsChecked).Select(choice => choice.Week).ToList();
        if (picked.Count > 0)
            return picked;

        return WeekList.SelectedItem is WeekChoice open ? [open.Week] : [];
    }

    private static bool HasOpenTimer() =>
        App.State.Settings.Entries.Any(entry => entry.EndUtc is null && entry.ManualMinutes <= 0);
}

internal sealed class WeekChoice : INotifyPropertyChanged
{
    private bool _isChecked;

    public WeekChoice(WorkWeek week) => Week = week;

    public WorkWeek Week { get; }
    public string Label => Week.Label;
    public string Summary => Week.Summary;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

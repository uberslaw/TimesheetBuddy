using System.Globalization;
using System.Windows;
using System.Windows.Input;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Views;

public partial class ManualEntryWindow : Window
{
    public ManualEntryWindow()
    {
        InitializeComponent();
        CodeCombo.ItemsSource = App.State.JobCodes.Where(j => j.IsActive).ToList();
        CategoryCombo.ItemsSource = App.State.Categories.ToList();
        if (CategoryCombo.Items.Count > 0) CategoryCombo.SelectedIndex = 0;

        HoursBox.PreviewTextInput += DigitsOnly_PreviewTextInput;
        MinutesBox.PreviewTextInput += DigitsOnly_PreviewTextInput;
        DataObject.AddPastingHandler(HoursBox, DigitsOnly_Pasting);
        DataObject.AddPastingHandler(MinutesBox, DigitsOnly_Pasting);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TaskBox.Text))
        {
            MessageBox.Show(this, "Enter a task name.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadDuration(out var totalMinutes, out var error))
        {
            MessageBox.Show(this, error, "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var code = CodeCombo.SelectedItem as JobCode;
        var end = DateTime.UtcNow;
        App.State.AddManualEntry(new TimeEntry
        {
            StartUtc = end.AddMinutes(-totalMinutes),
            EndUtc = end,
            TaskName = TaskBox.Text.Trim(),
            Category = CategoryCombo.Text?.Trim() ?? "General",
            JobCodeId = code?.Id,
            JobCodeText = code?.Display ?? string.Empty,
            Notes = CommentsBox.Text?.Trim() ?? string.Empty,
            ManualMinutes = totalMinutes,
            Source = "manual"
        });

        DialogResult = true;
        Close();
    }

    private bool TryReadDuration(out int totalMinutes, out string error)
    {
        totalMinutes = 0;
        error = string.Empty;

        if (!TryReadWholeNumber(HoursBox.Text, out var hours) || hours < 0)
        {
            error = "Hours must be a whole number, 0 or greater.";
            return false;
        }

        if (!TryReadWholeNumber(MinutesBox.Text, out var minutes) || minutes is < 0 or > 59)
        {
            error = "Minutes must be a whole number from 0 to 59.";
            return false;
        }

        if (hours > int.MaxValue / 60 || hours * 60 > int.MaxValue - minutes)
        {
            error = "That duration is too large.";
            return false;
        }

        totalMinutes = hours * 60 + minutes;
        if (totalMinutes < 1)
        {
            error = "Enter a duration of at least 1 minute.";
            return false;
        }

        return true;
    }

    private static bool TryReadWholeNumber(string? text, out int value)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            value = 0;
            return false;
        }

        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static void DigitsOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(ch => !char.IsDigit(ch));
    }

    private static void DigitsOnly_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text) &&
            e.DataObject.GetData(DataFormats.Text) is string text &&
            text.Length > 0 &&
            text.All(char.IsDigit))
            return;

        e.CancelCommand();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

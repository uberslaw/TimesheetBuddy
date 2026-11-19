using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>Right-click → Modify Time…: add to or remove from one Today-by-task row.</summary>
public partial class ModifyTimeWindow : Window
{
    private const int MaxHours = 99;
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.Compiled);
    private readonly TimeSpan _removable;

    public ModifyTimeWindow(string taskName, string codeLabel, TimeSpan currentTotal, TimeSpan removable)
    {
        InitializeComponent();
        _removable = removable;
        TaskLabel.Text = $"Modify time for “{taskName}”";
        CodeLabel.Text = codeLabel;
        TotalRun.Text = DayTotals.FormatCompact(currentTotal);
        RemoveHint.Text = $"Up to {DayTotals.FormatCompact(removable)} of finished time can be removed, newest segments first. A running timer is not changed.";
        HoursBox.Text = "00";
        MinutesBox.Text = "01";
        Loaded += (_, _) =>
        {
            MinutesBox.Focus();
            MinutesBox.SelectAll();
        };
    }

    public bool IsRemove { get; private set; }

    public TimeSpan SelectedDuration { get; private set; } = TimeSpan.FromMinutes(1);

    private int Hours
    {
        get => int.TryParse(HoursBox.Text, out var h) ? Math.Clamp(h, 0, MaxHours) : 0;
        set => HoursBox.Text = Math.Clamp(value, 0, MaxHours).ToString("00");
    }

    private int Minutes
    {
        get => int.TryParse(MinutesBox.Text, out var m) ? Math.Clamp(m, 0, 59) : 0;
        set => MinutesBox.Text = Math.Clamp(value, 0, 59).ToString("00");
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (RemoveHint is null) return;
        RemoveHint.Visibility = RemoveRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HoursUp_Click(object sender, RoutedEventArgs e) => Hours = Hours + 1;
    private void HoursDown_Click(object sender, RoutedEventArgs e) => Hours = Hours - 1;

    private void MinutesUp_Click(object sender, RoutedEventArgs e)
    {
        if (Minutes >= 59)
        {
            if (Hours >= MaxHours) return;
            Minutes = 0;
            Hours = Hours + 1;
        }
        else Minutes = Minutes + 1;
    }

    private void MinutesDown_Click(object sender, RoutedEventArgs e)
    {
        if (Minutes <= 0)
        {
            if (Hours > 0)
            {
                Hours = Hours - 1;
                Minutes = 59;
            }
            else Minutes = 0;
        }
        else Minutes = Minutes - 1;
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Hours = Hours;
        Minutes = Minutes;
    }

    private void DigitsOnly_Preview(object sender, TextCompositionEventArgs e) =>
        e.Handled = !Digits.IsMatch(e.Text);

    private void DigitsOnly_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var text = (string)e.DataObject.GetData(typeof(string))!;
            if (!Digits.IsMatch(text)) e.CancelCommand();
        }
        else e.CancelCommand();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Hours = Hours;
        Minutes = Minutes;
        var duration = TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes);
        if (duration < TimeSpan.FromMinutes(1))
        {
            MessageBox.Show(this, "Enter at least 1 minute.", "Modify time",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var remove = RemoveRadio.IsChecked == true;
        if (remove && duration > _removable)
        {
            MessageBox.Show(this, RowTimeEdit.OverRemovalMessage(_removable), "Modify time",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsRemove = remove;
        SelectedDuration = duration;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

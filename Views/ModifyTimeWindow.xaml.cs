using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>Right-click → Modify Time…: add to or remove from one Today-by-task row.</summary>
public partial class ModifyTimeWindow : Window
{
    private const int MaxHours = RowTimeEdit.MaxAmountHours;
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.Compiled);
    private readonly TimeSpan _removable;
    private int _addHours;
    private int _addMinutes = 1;

    public ModifyTimeWindow(string taskName, string codeLabel, TimeSpan currentTotal, TimeSpan removable,
        bool startWithRemove = false)
    {
        InitializeComponent();
        _removable = removable;
        TaskLabel.Text = $"Modify time for “{taskName}”";
        CodeLabel.Text = codeLabel;
        TotalRun.Text = DayTotals.FormatCompact(currentTotal);
        RemoveHint.Text = $"Up to {DayTotals.FormatCompact(removable)} of finished time can be removed, newest segments first. A running timer is not changed.";
        Hours = _addHours;
        Minutes = _addMinutes;
        if (startWithRemove) RemoveRadio.IsChecked = true;
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
        var remove = RemoveRadio.IsChecked == true;
        RemoveHint.Visibility = remove ? Visibility.Visible : Visibility.Collapsed;
        if (remove)
        {
            _addHours = Hours;
            _addMinutes = Minutes;
            var (hours, minutes) = RowTimeEdit.RemovePrefill(_removable);
            Hours = hours;
            Minutes = minutes;
        }
        else
        {
            Hours = _addHours;
            Minutes = _addMinutes;
        }
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
        var remove = RemoveRadio.IsChecked == true;
        if (remove && _removable <= TimeSpan.Zero)
        {
            MessageBox.Show(this, RowTimeEdit.OverRemovalMessage(_removable), "Modify time",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var removeAll = remove && RowTimeEdit.IsRemoveAll(_removable, Hours, Minutes);
        var duration = remove
            ? RowTimeEdit.ResolveRemoveAmount(_removable, Hours, Minutes)
            : TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes);
        if (!removeAll && duration < TimeSpan.FromMinutes(1))
        {
            MessageBox.Show(this, "Enter at least 1 minute.", "Modify time",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

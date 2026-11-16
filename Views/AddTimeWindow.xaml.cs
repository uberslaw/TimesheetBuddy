using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace TimesheetBuddy.Views;

public partial class AddTimeWindow : Window
{
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.Compiled);

    public AddTimeWindow(string taskName)
    {
        InitializeComponent();
        TaskLabel.Text = $"Add time to “{taskName}”";
        HoursBox.Text = "00";
        MinutesBox.Text = "01";
        MinutesBox.SelectAll();
        Loaded += (_, _) => MinutesBox.Focus();
    }

    public TimeSpan SelectedDuration { get; private set; } = TimeSpan.FromMinutes(1);

    private int Hours
    {
        get => int.TryParse(HoursBox.Text, out var h) ? Math.Clamp(h, 0, 23) : 0;
        set => HoursBox.Text = Math.Clamp(value, 0, 23).ToString("00");
    }

    private int Minutes
    {
        get => int.TryParse(MinutesBox.Text, out var m) ? Math.Clamp(m, 0, 59) : 0;
        set => MinutesBox.Text = Math.Clamp(value, 0, 59).ToString("00");
    }

    private void HoursUp_Click(object sender, RoutedEventArgs e) => Hours = Hours + 1;
    private void HoursDown_Click(object sender, RoutedEventArgs e) => Hours = Hours - 1;

    private void MinutesUp_Click(object sender, RoutedEventArgs e)
    {
        if (Minutes >= 59)
        {
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

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Hours = Hours;
        Minutes = Minutes;
        var duration = TimeSpan.FromHours(Hours) + TimeSpan.FromMinutes(Minutes);
        if (duration <= TimeSpan.Zero)
        {
            MessageBox.Show(this, "Enter at least 1 minute.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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

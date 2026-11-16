using System.Windows;

namespace TimesheetBuddy.Views;

public partial class LoadTimesheetWindow : Window
{
    public LoadTimesheetWindow()
    {
        InitializeComponent();
        DayCalendar.SelectedDate = App.State.ViewingDate;
        DayCalendar.DisplayDate = App.State.ViewingDate;
        // Don't allow future days.
        DayCalendar.BlackoutDates.Add(new System.Windows.Controls.CalendarDateRange(
            DateTime.Today.AddDays(1), DateTime.Today.AddYears(50)));
    }

    public DateTime SelectedDay { get; private set; } = DateTime.Today;

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (DayCalendar.SelectedDate is not DateTime day)
        {
            MessageBox.Show(this, "Pick a date.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (day.Date > DateTime.Today)
        {
            MessageBox.Show(this, "You can’t load a future timesheet.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedDay = day.Date;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

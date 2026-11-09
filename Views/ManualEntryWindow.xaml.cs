using System.Windows;
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
        for (var m = 5; m <= 120; m += 5) MinutesCombo.Items.Add(m);
        MinutesCombo.SelectedItem = 15;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var minutes = MinutesCombo.SelectedItem as int? ?? 0;
        if (minutes <= 0 || string.IsNullOrWhiteSpace(TaskBox.Text))
        {
            MessageBox.Show(this, "Enter a task name and minutes.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var code = CodeCombo.SelectedItem as JobCode;
        var end = DateTime.UtcNow;
        App.State.AddManualEntry(new TimeEntry
        {
            StartUtc = end.AddMinutes(-minutes),
            EndUtc = end,
            TaskName = TaskBox.Text.Trim(),
            Category = CategoryCombo.Text?.Trim() ?? "General",
            JobCodeId = code?.Id,
            JobCodeText = code?.Display ?? string.Empty,
            Notes = NotesBox.Text?.Trim() ?? string.Empty,
            ManualMinutes = minutes,
            Source = "manual"
        });

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

using System.Windows;

namespace TimesheetBuddy.Views;

public partial class QuickAddJobCodeWindow : Window
{
    public QuickAddJobCodeWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CodeBox.Text))
        {
            MessageBox.Show(this, "Enter a job code.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var code = CodeBox.Text.Trim();
        if (App.State.JobCodes.Any(j => string.Equals(j.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"Job code “{code}” already exists.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var created = App.State.AddJobCode(code, DescriptionBox.Text ?? string.Empty);
        if (created is null)
        {
            MessageBox.Show(this, $"Could not add job code “{code}”.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

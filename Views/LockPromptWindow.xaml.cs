using System.ComponentModel;
using System.Windows;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class LockPromptWindow : Window
{
    public LockPromptWindow(string message, IReadOnlyList<JobCode> otherCodes)
    {
        InitializeComponent();
        MessageText.Text = message;
        CodeCombo.ItemsSource = otherCodes;
        if (otherCodes.Count == 0)
        {
            CodeCombo.IsEnabled = false;
            AssignButton.IsEnabled = false;
        }

        Closing += OnClosingGuard;
    }

    public LockIntervalSplit.Choice Choice { get; private set; }
    public JobCode? SelectedCode { get; private set; }

    private void OnClosingGuard(object? sender, CancelEventArgs e)
    {
        if (DialogResult is null)
            e.Cancel = true;
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Choice = LockIntervalSplit.Choice.KeepOnCurrent;
        DialogResult = true;
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (CodeCombo.SelectedItem is not JobCode code)
        {
            MessageBox.Show(this, "Choose the job code those minutes should move to.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedCode = code;
        Choice = LockIntervalSplit.Choice.AssignToJobCode;
        DialogResult = true;
    }

    private void Break_Click(object sender, RoutedEventArgs e)
    {
        Choice = LockIntervalSplit.Choice.Break;
        DialogResult = true;
    }
}

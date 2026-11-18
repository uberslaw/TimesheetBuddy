using System.Windows;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class LockPromptWindow : Window
{
    public LockPromptWindow(
        string message,
        string activeLabel,
        IReadOnlyList<JobCode> codes,
        Guid? currentJobCodeId)
    {
        InitializeComponent();
        MessageText.Text = message;
        ActiveLabel.Text = activeLabel.EndsWith(':') ? activeLabel : activeLabel + ":";

        var active = codes.Where(c => c.IsActive).ToList();
        CodeCombo.ItemsSource = active;
        if (active.Count > 0)
        {
            CodeCombo.SelectedItem = active.FirstOrDefault(c => c.Id != currentJobCodeId) ?? active[0];
        }

        Closing += OnClosingGuard;
    }

    public LockIntervalSplit.Choice Choice { get; private set; } = LockIntervalSplit.Choice.KeepOnCurrent;
    public JobCode? SelectedCode { get; private set; }

    private void OnClosingGuard(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DialogResult is null)
            e.Cancel = true;
    }

    private void AssignActive_Click(object sender, RoutedEventArgs e)
    {
        Choice = LockIntervalSplit.Choice.KeepOnCurrent;
        SelectedCode = null;
        DialogResult = true;
        Close();
    }

    private void AssignSelected_Click(object sender, RoutedEventArgs e)
    {
        if (CodeCombo.SelectedItem is not JobCode code)
        {
            MessageBox.Show(this,
                "Pick a job code, then click Assign.",
                "Timesheet Buddy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Choice = LockIntervalSplit.Choice.AssignToJobCode;
        SelectedCode = code;
        DialogResult = true;
        Close();
    }
}

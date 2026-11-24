using System.Windows;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>"Exit Timesheet Buddy?" for a user close. Cancel is the default for both Enter and Esc.</summary>
public partial class ExitConfirmWindow : Window
{
    public ExitConfirmWindow(string detail)
    {
        InitializeComponent();
        QuestionText.Text = ExitPrompt.Question;
        DetailText.Text = detail;
        Loaded += (_, _) => CancelButton.Focus();
    }

    public bool DontAskAgain => DontAskCheck.IsChecked == true;

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

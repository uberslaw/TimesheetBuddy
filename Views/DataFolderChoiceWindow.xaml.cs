using System.Windows;

namespace TimesheetBuddy.Views;

public partial class DataFolderChoiceWindow : Window
{
    public enum Result
    {
        Cancel,
        UseExisting,
        Replace
    }

    public DataFolderChoiceWindow(string targetFolder, string backupName, bool timerRunning)
    {
        InitializeComponent();
        FolderText.Text = targetFolder;
        TimerWarning.Visibility = timerRunning ? Visibility.Visible : Visibility.Collapsed;
        ReplaceText.Text =
            $"Replace: the folder's settings.json is kept as {backupName}, then your current data is copied there and used from now on.";
    }

    public Result Choice { get; private set; } = Result.Cancel;

    private void UseExisting_Click(object sender, RoutedEventArgs e) => Finish(Result.UseExisting);

    private void Replace_Click(object sender, RoutedEventArgs e) => Finish(Result.Replace);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(Result.Cancel);

    private void Finish(Result choice)
    {
        Choice = choice;
        DialogResult = choice != Result.Cancel;
        Close();
    }
}

using System.Windows;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class DataFolderChoiceWindow : Window
{
    public enum Result
    {
        Cancel,
        UseExisting,
        Replace,
        Merge,
        Copy
    }

    /// <param name="kind">CopyToEmpty asks OK / Cancel; TargetHasData offers Use existing, Merge and Replace.</param>
    public DataFolderChoiceWindow(DataFolder.ChangeKind kind, string currentFolder, string targetFolder,
        string backupName, bool timerRunning)
    {
        InitializeComponent();
        FolderText.Text = targetFolder;
        MoveCheck.IsChecked = DataFolder.DefaultMove(kind);
        MoveNote.Text = "Unticked, the old copy stays in " + currentFolder + " as a backup.";

        var empty = kind == DataFolder.ChangeKind.CopyToEmpty;
        HeadingText.Text = empty
            ? "Use this folder for your Timesheet Buddy data?"
            : "This folder already has Timesheet Buddy data. Use that data, merge yours into it, or replace it with yours?";
        CopyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ChoicesPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsDefault = empty;
        ReplaceButton.Visibility = MergeButton.Visibility = UseExistingButton.Visibility =
            empty ? Visibility.Collapsed : Visibility.Visible;
        UseExistingButton.IsDefault = !empty;

        TimerWarning.Visibility = timerRunning ? Visibility.Visible : Visibility.Collapsed;
        ReplaceText.Text =
            $"Replace: the folder's settings.json is kept as {backupName}, then your current data is copied there and used from now on.";
    }

    public Result Choice { get; private set; } = Result.Cancel;

    /// <summary>Delete the data from the old folder after Copy, Replace or Merge succeeded (never for Use existing).</summary>
    public bool Move => MoveCheck.IsChecked == true;

    private void UseExisting_Click(object sender, RoutedEventArgs e) => Finish(Result.UseExisting);

    private void Replace_Click(object sender, RoutedEventArgs e) => Finish(Result.Replace);

    private void Merge_Click(object sender, RoutedEventArgs e) => Finish(Result.Merge);

    private void Copy_Click(object sender, RoutedEventArgs e) => Finish(Result.Copy);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(Result.Cancel);

    private void Finish(Result choice)
    {
        Choice = choice;
        DialogResult = choice != Result.Cancel;
        Close();
    }
}

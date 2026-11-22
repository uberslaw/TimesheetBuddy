using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<JobCode> _jobCodes;
    private readonly ObservableCollection<FavoriteTask> _favorites;
    private readonly bool _focusFavorites;

    public SettingsWindow(bool focusFavorites = false)
    {
        InitializeComponent();
        _focusFavorites = focusFavorites;

        var state = App.State;
        _jobCodes = new ObservableCollection<JobCode>(state.JobCodes.Select(CloneJob));
        _favorites = new ObservableCollection<FavoriteTask>(state.FavoriteTasks.Select(CloneFav));

        JobCodesGrid.ItemsSource = _jobCodes;
        FavoritesGrid.ItemsSource = _favorites;
        FavoriteJobCodeColumn.ItemsSource = _jobCodes;

        CategoriesBox.Text = string.Join(Environment.NewLine, state.Categories);

        AlwaysOnTopCheck.IsChecked = state.Settings.AlwaysOnTop;
        WidgetEnableRadio.IsChecked = state.Settings.ShowTimerWidget;
        WidgetDisableRadio.IsChecked = !state.Settings.ShowTimerWidget;
        WidgetCoveredCheck.IsChecked = state.Settings.ShowTimerWidgetWhenCovered;
        WidgetOtherMonitorCheck.IsChecked = state.Settings.ShowTimerWidgetOnOtherMonitor;
        RecheckEnabledCheck.IsChecked = state.Settings.RecheckPromptEnabled;
        FifteenEnabledCheck.IsChecked = state.Settings.FifteenMinutePromptEnabled;
        AppTrackCheck.IsChecked = state.Settings.AppTrackEnabled;

        for (var h = 0; h <= 8; h++) RecheckHours.Items.Add(h);
        for (var m = 0; m < 60; m += 5) RecheckMinutes.Items.Add(m);
        foreach (var m in new[] { 5, 10, 15, 20, 30, 45, 60, 90 }) FifteenMinutes.Items.Add(m);

        RecheckHours.SelectedItem = state.Settings.RecheckHours;
        RecheckMinutes.SelectedItem = RoundToFive(state.Settings.RecheckMinutes);
        FifteenMinutes.SelectedItem = state.Settings.FifteenMinuteIntervalMinutes;

        CrashLogNote.Text = "The crash log always stays in " + DataFolder.DefaultFolder +
                            " so it works even if this folder is unavailable.";
        ShowDataFolder();

        Loaded += (_, _) =>
        {
            if (_focusFavorites) Tabs.SelectedItem = FavoritesTab;
        };
    }

    private void ShowDataFolder()
    {
        var folder = App.Store.Folder;
        DataFolderBox.Text = folder;
        DataFolderBox.ToolTip = folder;

        var saved = DataFolder.ReadPointer(DataFolder.DefaultFolder);
        var isDefault = DataFolder.SameFolder(folder, DataFolder.DefaultFolder);
        UseDefaultFolderButton.IsEnabled = !isDefault || saved is not null;
        UseDefaultFolderButton.ToolTip = "Go back to " + DataFolder.DefaultFolder;

        var unavailable = isDefault && saved is not null && App.UnavailableDataFolder is not null &&
                          DataFolder.SameFolder(saved, App.UnavailableDataFolder);
        UnavailableFolderText.Visibility = unavailable ? Visibility.Visible : Visibility.Collapsed;
        UnavailableFolderText.Text = unavailable
            ? $"Your chosen folder {saved} was not available at start-up, so this session uses the default folder. " +
              "Timesheet Buddy tries it again next start. Use default stops using it."
            : string.Empty;
    }

    private void BrowseDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for Timesheet Buddy data",
            InitialDirectory = App.Store.Folder
        };
        if (dialog.ShowDialog(this) != true) return;
        ChangeDataFolder(dialog.FolderName);
    }

    private void UseDefaultFolder_Click(object sender, RoutedEventArgs e) =>
        ChangeDataFolder(DataFolder.DefaultFolder);

    private void ImportData_Click(object sender, RoutedEventArgs e) =>
        new ImportDataWindow { Owner = this }.ShowDialog();

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Store.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Could not open the folder.\n\n" + ex.Message);
        }
    }

    private void ChangeDataFolder(string target)
    {
        var current = App.Store.Folder;
        switch (DataFolder.Plan(current, target))
        {
            case DataFolder.ChangeKind.SameFolder:
                ForgetUnavailableFolder(target);
                break;

            case DataFolder.ChangeKind.Invalid:
                ShowError("That folder can't be used for Timesheet Buddy data:\n" + target +
                          "\n\nIt must exist and you need permission to create files in it. Nothing was changed.");
                break;

            case DataFolder.ChangeKind.CopyToEmpty:
            case DataFolder.ChangeKind.TargetHasData:
                var kind = DataFolder.Plan(current, target);
                var now = DateTime.Now;
                var backupName = DataFolder.BackupName(DataFolder.SettingsFileName, now);
                var choice = new DataFolderChoiceWindow(kind, current, target, backupName, App.State.ActiveSession is not null)
                    { Owner = this };
                choice.ShowDialog();
                switch (choice.Choice)
                {
                    case DataFolderChoiceWindow.Result.Copy:
                    case DataFolderChoiceWindow.Result.Replace:
                        CopyAndSwitch(current, target, choice.Move, now);
                        break;
                    case DataFolderChoiceWindow.Result.Merge:
                        MergeAndSwitch(current, target, choice.Move, now);
                        break;
                    case DataFolderChoiceWindow.Result.UseExisting:
                        UseExistingAndRestart(target);
                        break;
                }

                break;
        }
    }

    /// <summary>Picking the folder already in use only matters when a saved choice was unavailable.</summary>
    private void ForgetUnavailableFolder(string target)
    {
        var saved = DataFolder.ReadPointer(DataFolder.DefaultFolder);
        if (saved is null || DataFolder.SameFolder(saved, target)) return;

        try
        {
            DataFolder.WritePointer(DataFolder.DefaultFolder, target);
        }
        catch (Exception ex)
        {
            ShowError("Could not save the folder choice.\n\n" + ex.Message);
            return;
        }

        App.ClearUnavailableDataFolder();
        ShowDataFolder();
        MessageBox.Show(this,
            "Timesheet Buddy keeps using:\n" + target + "\n\nand no longer looks for:\n" + saved,
            "Data folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Copy (empty folder) or Replace: the files are copied and checked, the store switches and saves, the pointer
    /// is written; any failure undoes the copies. With <paramref name="move"/> the old files are deleted afterwards.
    /// </summary>
    private void CopyAndSwitch(string current, string target, bool move, DateTime nowLocal)
    {
        DataFolder.TransferResult result;
        try
        {
            App.State.Persist();
            App.AppTracker.Flush();
            result = DataFolder.Transfer(current, target, move, nowLocal, commit: () =>
            {
                App.Store.SwitchFolder(target);
                App.State.Persist();
                DataFolder.WritePointer(DataFolder.DefaultFolder, target);
            });
        }
        catch (Exception ex)
        {
            App.Store.SwitchFolder(current);
            ShowError("Could not move your data to:\n" + target +
                      "\n\nTimesheet Buddy keeps using:\n" + current + "\n\n" + ex.Message);
            ShowDataFolder();
            return;
        }

        AfterSwitch(current, target, move ? "Moved" : "Copied");
        var backup = result.BackedUp.FirstOrDefault(n => n.StartsWith(DataFolder.SettingsFileName, StringComparison.OrdinalIgnoreCase));
        MessageBox.Show(this,
            "Timesheet Buddy now saves to:\n" + App.Store.Folder +
            OldCopyNote(current, move, result.Removed.Count, result.NotRemoved) +
            (backup is null ? string.Empty : $"\n\nThe folder's previous settings.json was kept as {backup}."),
            "Data folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Merge: the current data is added to the folder's data (checked by reading it back), then the store switches
    /// and saves, which brings the folder's entries into the open timesheet. The running timer keeps running.
    /// </summary>
    private void MergeAndSwitch(string current, string target, bool move, DateTime nowLocal)
    {
        DataImport.FolderMergeResult merged;
        try
        {
            App.State.Persist();
            App.AppTracker.Flush();
            merged = DataImport.MergeFolder(current, App.State.Settings, target, removeOriginals: false, nowLocal);
            App.Store.SwitchFolder(target);
            App.State.Persist();
            DataFolder.WritePointer(DataFolder.DefaultFolder, target);
        }
        catch (Exception ex)
        {
            App.Store.SwitchFolder(current);
            ShowError("Could not merge your data into:\n" + target +
                      "\n\nTimesheet Buddy keeps using:\n" + current + "\n\n" + ex.Message);
            ShowDataFolder();
            return;
        }

        IReadOnlyList<string> removed = [];
        IReadOnlyList<string> notRemoved = [];
        if (move)
            (removed, notRemoved) = DataFolder.RemoveFiles(current, DataFolder.DataFiles(current));

        App.AppTracker.ReloadFromDisk();
        AfterSwitch(current, target, "Merged");
        var s = merged.Settings;
        MessageBox.Show(this,
            "Timesheet Buddy now saves to:\n" + App.Store.Folder +
            $"\n\nAdded {s.EntriesAdded} of your entries and updated {s.EntriesUpdated}, " +
            $"{merged.Files.NotesNew.Count + merged.Files.NotesAppended.Count} day notes and " +
            $"{merged.Files.UsageNew.Count + merged.Files.UsageMerged.Count} App Track days." +
            (merged.SettingsBackup is null ? string.Empty : $"\n\nThe folder's previous settings.json was kept as {merged.SettingsBackup}.") +
            OldCopyNote(current, move, removed.Count, notRemoved),
            "Data folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void AfterSwitch(string current, string target, string verb)
    {
        App.ClearUnavailableDataFolder();
        SessionFiles.Delete(current, App.Client.ClientId);
        App.State.LogFolderChange($"{verb} data from {current} to {target}");
        App.Presence?.Tick();
        ShowDataFolder();
    }

    private static string OldCopyNote(string current, bool move, int removed, IReadOnlyList<string> notRemoved)
    {
        if (!move) return "\n\nThe old copy was left in place in:\n" + current;
        if (notRemoved.Count == 0) return $"\n\n{removed} file(s) were removed from:\n" + current;
        return $"\n\n{removed} file(s) were removed from {current}. These could not be removed and were left there: " +
               string.Join(", ", notRemoved);
    }

    private void UseExistingAndRestart(string target)
    {
        try
        {
            App.State.StopAllForShutdown();
            DataFolder.WritePointer(DataFolder.DefaultFolder, target);
        }
        catch (Exception ex)
        {
            ShowError("Could not switch to:\n" + target + "\n\nNothing was changed.\n\n" + ex.Message);
            return;
        }

        MessageBox.Show(this,
            "Timesheet Buddy will restart now and use the data in:\n" + target,
            "Data folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        App.Restart();
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Data folder", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static int RoundToFive(int minutes)
    {
        var rounded = (int)Math.Round(minutes / 5.0) * 5;
        return Math.Clamp(rounded, 0, 55);
    }

    private static JobCode CloneJob(JobCode j) => new()
    {
        Id = j.Id,
        Code = j.Code,
        Description = j.Description,
        IsActive = j.IsActive
    };

    private static FavoriteTask CloneFav(FavoriteTask f) => new()
    {
        Id = f.Id,
        Name = f.Name,
        Category = f.Category,
        JobCodeId = f.JobCodeId
    };

    private void AddJobCode_Click(object sender, RoutedEventArgs e) =>
        _jobCodes.Add(new JobCode { Code = "NEW", Description = "New job code" });

    private void RemoveJobCode_Click(object sender, RoutedEventArgs e)
    {
        if (JobCodesGrid.SelectedItem is JobCode selected)
            _jobCodes.Remove(selected);
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e) =>
        _favorites.Add(new FavoriteTask
        {
            Name = "New task",
            Category = App.State.Categories.FirstOrDefault() ?? "General",
            JobCodeId = _jobCodes.FirstOrDefault()?.Id
        });

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesGrid.SelectedItem is FavoriteTask selected)
            _favorites.Remove(selected);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Commit any in-progress cell edits.
        JobCodesGrid.CommitEdit();
        FavoritesGrid.CommitEdit();

        var categories = CategoriesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        App.State.ReplaceJobCodes(_jobCodes);
        App.State.ReplaceFavorites(_favorites);
        App.State.ReplaceCategories(categories);
        App.State.UpdatePromptSettings(
            AlwaysOnTopCheck.IsChecked == true,
            RecheckEnabledCheck.IsChecked == true,
            RecheckHours.SelectedItem as int? ?? 0,
            RecheckMinutes.SelectedItem as int? ?? 30,
            FifteenEnabledCheck.IsChecked == true,
            FifteenMinutes.SelectedItem as int? ?? 15);
        App.State.SetTimerWidgetOptions(
            WidgetDisableRadio.IsChecked != true,
            WidgetCoveredCheck.IsChecked == true,
            WidgetOtherMonitorCheck.IsChecked == true);
        if ((AppTrackCheck.IsChecked == true) != App.State.Settings.AppTrackEnabled)
            App.SetAppTrack(AppTrackCheck.IsChecked == true);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

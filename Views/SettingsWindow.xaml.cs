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
                if (MessageBox.Show(this,
                        "Move your Timesheet Buddy data to:\n" + target +
                        "\n\nYour settings, time entries, day notes and App Track usage are copied there now and used from now on. " +
                        "The copy in\n" + current + "\nis left in place as a backup.",
                        "Data folder",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;
                CopyAndSwitch(current, target, backupNote: null);
                break;

            case DataFolder.ChangeKind.TargetHasData:
                var now = DateTime.Now;
                var backupName = DataFolder.BackupName(DataFolder.SettingsFileName, now);
                var choice = new DataFolderChoiceWindow(target, backupName, App.State.ActiveSession is not null)
                    { Owner = this };
                choice.ShowDialog();
                if (choice.Choice == DataFolderChoiceWindow.Result.Replace)
                    CopyAndSwitch(current, target, $"\n\nThe folder's previous settings.json was kept as {backupName}.", now);
                else if (choice.Choice == DataFolderChoiceWindow.Result.UseExisting)
                    UseExistingAndRestart(target);
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

    private void CopyAndSwitch(string current, string target, string? backupNote, DateTime? nowLocal = null)
    {
        try
        {
            App.State.Persist();
            App.AppTracker.Flush();
            DataFolder.CopyData(current, target, nowLocal ?? DateTime.Now);
            App.Store.SwitchFolder(target);
            App.State.Persist();
            DataFolder.WritePointer(DataFolder.DefaultFolder, target);
        }
        catch (Exception ex)
        {
            App.Store.SwitchFolder(current);
            ShowError("Could not move your data to:\n" + target +
                      "\n\nTimesheet Buddy keeps using:\n" + current + "\n\n" + ex.Message);
            ShowDataFolder();
            return;
        }

        App.ClearUnavailableDataFolder();
        ShowDataFolder();
        MessageBox.Show(this,
            "Timesheet Buddy now saves to:\n" + App.Store.Folder +
            "\n\nThe old copy was left in place in:\n" + current + backupNote,
            "Data folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

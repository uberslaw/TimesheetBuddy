using System.Collections.ObjectModel;
using System.Windows;
using TimesheetBuddy.Models;

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
        RecheckEnabledCheck.IsChecked = state.Settings.RecheckPromptEnabled;
        FifteenEnabledCheck.IsChecked = state.Settings.FifteenMinutePromptEnabled;

        for (var h = 0; h <= 8; h++) RecheckHours.Items.Add(h);
        for (var m = 0; m < 60; m += 5) RecheckMinutes.Items.Add(m);
        foreach (var m in new[] { 5, 10, 15, 20, 30, 45, 60, 90 }) FifteenMinutes.Items.Add(m);

        RecheckHours.SelectedItem = state.Settings.RecheckHours;
        RecheckMinutes.SelectedItem = RoundToFive(state.Settings.RecheckMinutes);
        FifteenMinutes.SelectedItem = state.Settings.FifteenMinuteIntervalMinutes;

        Loaded += (_, _) =>
        {
            if (_focusFavorites) Tabs.SelectedItem = FavoritesTab;
        };
    }

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

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

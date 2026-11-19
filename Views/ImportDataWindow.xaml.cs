using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>
/// Menu → Import data… and Settings → Data → Import data…: pick a copy, see what importing would add (and which
/// release wrote it), then import. Reading goes through <see cref="LegacyData"/>, so older releases' files work.
/// </summary>
public partial class ImportDataWindow : Window
{
    private DataImport.Source? _source;
    private DataImport.Preview? _preview;
    private string _label = string.Empty;

    public ImportDataWindow()
    {
        InitializeComponent();
        CandidatesList.ItemsSource = DataImport.FindCandidates(App.Store.Folder, DataFolder.DefaultFolder);
        if (CandidatesList.Items.Count == 0)
            PreviewText.Text = "No backups or conflict copies were found in the data folder. Choose a folder or a settings file.";
    }

    private void CandidatesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesList.SelectedItem is DataImport.Candidate candidate)
            Load(candidate.Path, candidate.Label);
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder with Timesheet Buddy data" };
        if (dialog.ShowDialog(this) != true) return;
        CandidatesList.SelectedItem = null;
        Load(dialog.FolderName, dialog.FolderName);
    }

    private void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a Timesheet Buddy settings file",
            Filter = "Timesheet Buddy settings|settings*.json;settings.json.bak-*;*.json|All files|*.*",
            InitialDirectory = App.Store.Folder
        };
        if (dialog.ShowDialog(this) != true) return;
        CandidatesList.SelectedItem = null;
        Load(dialog.FileName, dialog.FileName);
    }

    private void Load(string path, string label)
    {
        _label = label;
        _source = DataImport.Open(path, App.Store.Folder);
        _preview = null;
        if (_source.Error is not null)
        {
            PreviewText.Text = _source.Error;
            ImportButton.IsEnabled = false;
            return;
        }

        try
        {
            _preview = DataImport.PreviewImport(App.State.Settings, _source, App.Store.Folder, DateTime.Now);
        }
        catch (Exception ex)
        {
            PreviewText.Text = "That copy can't be read.\n\n" + ex.Message;
            ImportButton.IsEnabled = false;
            return;
        }

        PreviewText.Text = Describe(_source, _preview);
        ImportButton.IsEnabled = !_preview.NothingToAdd;
    }

    private static string Describe(DataImport.Source source, DataImport.Preview preview)
    {
        var text = new StringBuilder();
        if (source.Release is not null)
            text.AppendLine("Written by: Timesheet Buddy " + (source.Release == "current" ? "(this release)" : source.Release));
        foreach (var upgrade in source.Upgrades)
            text.AppendLine("Upgraded on reading: " + upgrade);
        if (text.Length > 0) text.AppendLine();

        if (preview.NothingToAdd)
        {
            text.Append("Nothing to import: your data already has everything in this copy.");
            return text.ToString();
        }

        if (preview.Settings is { } s)
        {
            var range = s.FirstAddedDay is { } first && s.LastAddedDay is { } last
                ? first.Date == last.Date ? $" (on {Day(first)})" : $" ({Day(first)} to {Day(last)})"
                : string.Empty;
            text.AppendLine($"Time entries to add: {s.EntriesAdded}{range}");
            if (s.EntriesUpdated > 0) text.AppendLine($"Time entries to update: {s.EntriesUpdated}");
            if (s.DuplicateEntries > 0) text.AppendLine($"Already in your data: {s.DuplicateEntries}");
            if (s.JobCodesAdded.Count > 0) text.AppendLine("Job codes to add: " + string.Join(", ", s.JobCodesAdded));
            if (s.CategoriesAdded.Count > 0) text.AppendLine("Categories to add: " + string.Join(", ", s.CategoriesAdded));
            if (s.FavoritesAdded.Count > 0) text.AppendLine("Favourite tasks to add: " + string.Join(", ", s.FavoritesAdded));
            if (s.SkippedRemoved > 0) text.AppendLine($"Not brought back (you deleted them here): {s.SkippedRemoved}");
            if (s.SkippedDeleted > 0) text.AppendLine($"Not imported (deleted in that copy): {s.SkippedDeleted}");
            if (s.SkippedRunning > 0) text.AppendLine($"Not imported (a timer still running in that copy): {s.SkippedRunning}");
        }

        var f = preview.Files;
        if (f.NotesNew.Count > 0) text.AppendLine($"Day notes to add: {f.NotesNew.Count}");
        if (f.NotesAppended.Count > 0) text.AppendLine($"Day notes to append under a separator: {f.NotesAppended.Count}");
        if (f.UsageNew.Count + f.UsageMerged.Count > 0)
            text.AppendLine($"App Track days to add or merge: {f.UsageNew.Count + f.UsageMerged.Count}");
        if (f.Unreadable > 0) text.AppendLine($"Unreadable files skipped: {f.Unreadable}");
        return text.ToString().TrimEnd();
    }

    private static string Day(DateTime day) => day.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || _preview is null) return;
        var now = DateTime.Now;
        string? backup;
        SettingsMerge.Report? report = null;
        DataImport.FilesReport? files = null;
        try
        {
            App.State.Persist();
            App.AppTracker.Flush();
            backup = DataImport.BackupSettings(App.Store.Folder, now);
            if (_source.Settings is not null)
                report = App.State.ImportSettings(_source.Settings, _label);

            if (_source.FilesFolder is not null)
            {
                var transaction = new FileTransaction(now);
                try
                {
                    files = DataImport.MergeFiles(_source.FilesFolder, App.Store.Folder, now, dryRun: false, transaction);
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }

                App.AppTracker.ReloadFromDisk();
                if (report is null)
                    App.State.LogImport($"Imported {files.NotesNew.Count + files.NotesAppended.Count} day notes and " +
                                        $"{files.UsageNew.Count + files.UsageMerged.Count} App Track days from {_label}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "The import did not finish.\n\n" + ex.Message, "Import data",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var message = new StringBuilder("Imported from " + _label + ".\n");
        if (report is not null)
            message.Append($"\n{report.EntriesAdded} time entries added, {report.EntriesUpdated} updated, " +
                           $"{report.JobCodesAdded.Count} job codes and {report.CategoriesAdded.Count} categories added.");
        if (files is not null)
            message.Append($"\n{files.NotesNew.Count + files.NotesAppended.Count} day notes and " +
                           $"{files.UsageNew.Count + files.UsageMerged.Count} App Track days added.");
        if (backup is not null)
            message.Append($"\n\nYour settings.json from before the import was kept as {backup} in\n{App.Store.Folder}");
        MessageBox.Show(this, message.ToString(), "Import data", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

using System.Windows;
using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

public partial class FifteenMinuteWindow : Window
{
    private readonly UnaccountedWindow _window;
    private readonly int _unaccountedMinutes;
    private readonly CatchUpTarget? _activeTarget;

    public FifteenMinuteWindow(UnaccountedWindow window)
    {
        InitializeComponent();

        _window = window;
        _unaccountedMinutes = Math.Max(0, window.UnaccountedMinutes);
        PromptTitle.Text = UnaccountedTime.AssignPromptText(_unaccountedMinutes);
        MinutesHint.Text = _unaccountedMinutes == 1
            ? "Assign all 1 minute to one task, or choose Not now."
            : $"Assign all {_unaccountedMinutes} minutes to one task, or choose Not now.";

        var today = TaskTypeSummary.Build(
            App.State.Settings.Entries,
            App.State.ViewingDate,
            DateTime.UtcNow);

        _activeTarget = ResolveActiveTarget(today);
        if (_activeTarget is null)
        {
            ActiveRow.Visibility = Visibility.Collapsed;
            AssignActiveButton.IsDefault = false;
        }
        else
        {
            ActiveLabel.Text = _activeTarget.Display.EndsWith(':')
                ? _activeTarget.Display
                : _activeTarget.Display + ":";
        }

        var todayOptions = today
            .Select(CatchUpTarget.FromRow)
            .Where(t => _activeTarget is null || t.Key != _activeTarget.Key)
            .ToList();
        TodayTasksCombo.ItemsSource = todayOptions;
        if (todayOptions.Count > 0)
        {
            TodayTasksCombo.SelectedIndex = 0;
            if (_activeTarget is null)
                AssignTodayButton.IsDefault = true;
        }
        else
        {
            TodayTasksCombo.IsEnabled = false;
        }

        var categories = App.State.Categories.ToList();
        if (categories.Count == 0) categories.Add("General");
        NewCategoryCombo.ItemsSource = categories;
        NewCategoryCombo.Text = categories[0];

        var codes = App.State.JobCodes.Where(j => j.IsActive).ToList();
        NewCodeCombo.ItemsSource = codes;
        if (codes.Count > 0)
            NewCodeCombo.SelectedIndex = 0;
    }

    private static CatchUpTarget? ResolveActiveTarget(IReadOnlyList<TaskTypeRow> today)
    {
        var session = App.State.ActiveSession;
        if (session is not null)
        {
            return new CatchUpTarget(
                TaskTypeSummary.KeyOf(session),
                session.TaskName,
                string.IsNullOrWhiteSpace(session.Category) ? "General" : session.Category,
                session.JobCodeId,
                session.JobCodeText ?? string.Empty,
                TicketRules.Normalize(session.Ticket));
        }

        var latest = today.FirstOrDefault();
        return latest is null ? null : CatchUpTarget.FromRow(latest);
    }

    private void AssignActive_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTarget is null) return;
        Commit(_activeTarget);
    }

    private void AssignToday_Click(object sender, RoutedEventArgs e)
    {
        if (TodayTasksCombo.SelectedItem is not CatchUpTarget target)
        {
            MessageBox.Show(this,
                TodayTasksCombo.Items.Count == 0
                    ? "No other tasks are logged today — enter a new task below, or assign the last active one."
                    : "Pick one of today’s logged tasks, then click Assign.",
                "Unaccounted time",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Commit(target);
    }

    private void AssignNew_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTaskBox.Text?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            MessageBox.Show(this,
                "Enter a task name for the new task.",
                "Unaccounted time",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            NewTaskBox.Focus();
            return;
        }

        var category = string.IsNullOrWhiteSpace(NewCategoryCombo.Text)
            ? "General"
            : NewCategoryCombo.Text.Trim();
        var code = NewCodeCombo.SelectedItem as JobCode;
        if (code is null)
        {
            MessageBox.Show(this,
                "Pick a job code for the new task.",
                "Unaccounted time",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Commit(new CatchUpTarget(
            TaskTypeSummary.KeyOf(name, category, code.Id, code.Display),
            name,
            category,
            code.Id,
            code.Display));
    }

    private void NotNow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Commit(CatchUpTarget target)
    {
        if (_unaccountedMinutes <= 0)
        {
            DialogResult = false;
            Close();
            return;
        }

        var slice = new CatchUpSlice
        {
            TaskName = target.TaskName,
            Category = target.Category,
            JobCodeId = target.JobCodeId,
            JobCodeText = JobCode.FormatLabelFromStored(target.JobCodeText),
            Ticket = target.Ticket,
            Minutes = _unaccountedMinutes
        };

        App.State.AddCatchUpAssignments([slice], _window);
        DialogResult = true;
        Close();
    }

    private sealed record CatchUpTarget(
        string Key,
        string TaskName,
        string Category,
        Guid? JobCodeId,
        string JobCodeText,
        string Ticket = "")
    {
        public string Display
        {
            get
            {
                var code = JobCode.FormatLabelFromStored(JobCodeText);
                if (code == "—")
                    code = "No job code";
                return $"{code}, {Category}, {TicketRules.Label(Ticket, TaskName)}";
            }
        }

        public static CatchUpTarget FromRow(TaskTypeRow row) => new(
            row.Key,
            row.TaskName,
            row.Category,
            row.JobCodeId,
            row.JobCodeText,
            row.Ticket);
    }
}

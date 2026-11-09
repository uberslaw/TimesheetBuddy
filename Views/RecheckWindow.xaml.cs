using System.Windows;

namespace TimesheetBuddy.Views;

public partial class RecheckWindow : Window
{
    public RecheckWindow()
    {
        InitializeComponent();

        for (var h = 0; h <= 8; h++) HoursCombo.Items.Add(h);
        for (var m = 0; m < 60; m += 5) MinutesCombo.Items.Add(m);

        var s = App.State.Settings;
        HoursCombo.SelectedItem = s.RecheckHours;
        MinutesCombo.SelectedItem = RoundToFive(s.RecheckMinutes);

        var session = App.State.ActiveSession;
        TaskLabel.Text = session?.TaskName ?? "Unknown task";
        CodeLabel.Text = string.IsNullOrWhiteSpace(session?.JobCodeText)
            ? "No job code"
            : session!.JobCodeText;
        ElapsedLabel.Text = $"Elapsed {App.State.ElapsedDisplay}";

        // Modal until acknowledged — disable Alt+F4 close without choosing.
        Closing += (_, e) =>
        {
            if (DialogResult is null) e.Cancel = true;
        };
    }

    private static int RoundToFive(int minutes)
    {
        var rounded = (int)Math.Round(minutes / 5.0) * 5;
        return Math.Clamp(rounded, 0, 55);
    }

    private void ApplyRecheckInterval()
    {
        var hours = HoursCombo.SelectedItem as int? ?? 0;
        var minutes = MinutesCombo.SelectedItem as int? ?? 15;
        App.State.SetRecheckInterval(hours, minutes);
        App.Scheduler.ResetRecheck();
    }

    private void StillWorking_Click(object sender, RoutedEventArgs e)
    {
        ApplyRecheckInterval();
        DialogResult = true;
        Close();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        ApplyRecheckInterval();
        App.State.StopActive(save: true);
        App.Scheduler.OnSessionChanged();
        DialogResult = true;
        Close();
    }

    private void Switch_Click(object sender, RoutedEventArgs e)
    {
        ApplyRecheckInterval();
        DialogResult = true;
        Close();
        if (Owner is MainWindow main)
            main.Activate();
    }
}

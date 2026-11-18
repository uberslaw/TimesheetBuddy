using System.Windows;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Views;

public partial class ChangeJobCodeWindow : Window
{
    public ChangeJobCodeWindow(string taskName, IReadOnlyList<JobCode> codes, Guid? currentJobCodeId)
    {
        InitializeComponent();
        TaskLabel.Text = $"Change job code for “{taskName}”";

        var active = codes.Where(c => c.IsActive).ToList();
        CodeCombo.ItemsSource = active;
        if (active.Count > 0)
        {
            CodeCombo.SelectedItem = active.FirstOrDefault(c => c.Id == currentJobCodeId) ?? active[0];
        }
    }

    public JobCode? SelectedCode { get; private set; }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        if (CodeCombo.SelectedItem is not JobCode code)
        {
            MessageBox.Show(this,
                "Pick a job code.",
                "Change job code",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SelectedCode = code;
        DialogResult = true;
        Close();
    }
}

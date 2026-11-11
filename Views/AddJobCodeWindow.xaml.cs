using System.Windows;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Views;

public partial class AddJobCodeWindow : Window
{
    private readonly IReadOnlyList<JobCode> _all;

    public AddJobCodeWindow(IReadOnlyList<JobCode> allCodes, IReadOnlyList<Guid> visibleIds)
    {
        InitializeComponent();
        _all = allCodes;
        var hidden = new HashSet<Guid>(visibleIds);
        var available = allCodes.Where(code => !hidden.Contains(code.Id)).ToList();
        CodeCombo.ItemsSource = available;
        AddExistingButton.IsEnabled = available.Count > 0;
        if (available.Count == 0)
            CodeCombo.IsEnabled = false;
    }

    public bool CreateNew { get; private set; }
    public Guid? ExistingId { get; private set; }
    public string? NewCode { get; private set; }
    public string? NewDescription { get; private set; }

    private void AddExisting_Click(object sender, RoutedEventArgs e)
    {
        if (CodeCombo.SelectedItem is not JobCode code)
        {
            MessageBox.Show(this, "Choose a job code to add.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExistingId = code.Id;
        CreateNew = false;
        DialogResult = true;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0)
        {
            MessageBox.Show(this, "Enter a job code.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_all.Any(existing => string.Equals(existing.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That job code already exists. Add it from the existing list.",
                "Timesheet Buddy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        NewCode = code;
        NewDescription = DescriptionBox.Text.Trim();
        CreateNew = true;
        DialogResult = true;
    }
}

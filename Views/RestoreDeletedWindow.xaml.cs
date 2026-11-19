using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace TimesheetBuddy.Views;

public partial class RestoreDeletedWindow : Window
{
    private readonly ObservableCollection<DeletedRow> _rows = [];

    public RestoreDeletedWindow()
    {
        InitializeComponent();
        Reload();
        DeletedGrid.ItemsSource = _rows;
    }

    private void Reload()
    {
        _rows.Clear();
        foreach (var group in App.State.GetDeletedTaskGroupsToday())
        {
            _rows.Add(new DeletedRow
            {
                Key = group.Key,
                Name = Services.TicketRules.Label(group.Ticket, group.TaskName),
                Category = group.Category,
                DeletedAtDisplay = group.DeletedAtLocal.ToString("HH:mm:ss"),
                IsSelected = false
            });
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = true;
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var keys = _rows.Where(r => r.IsSelected).Select(r => r.Key).ToList();
        if (keys.Count == 0)
        {
            MessageBox.Show(this, "Select at least one task to restore.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        App.State.RestoreDeletedGroups(keys);
        DialogResult = true;
        Close();
    }

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "Nothing to restore today.", "Timesheet Buddy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        App.State.RestoreAllDeletedToday();
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class DeletedRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string DeletedAtDisplay { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}

using System.Windows;
using System.Windows.Threading;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>
/// Small non-modal, always-on-top popup: "Timesheet Buddy is also open on PC. Are you active on this one?"
/// Never takes focus and never blocks closing the app. Closing it with ✕ counts as Not now. Also shows the short
/// "Already confirmed on …" notice before a close.
/// </summary>
public partial class OverlapPromptWindow : Window
{
    private bool _answered;

    public OverlapPromptWindow(SessionFile other)
    {
        InitializeComponent();
        Other = other;
        MessageText.Text = $"Timesheet Buddy is also open on {other.Machine}. Are you active on this one?";
        Loaded += (_, _) => PlaceBottomRight();
        Closed += (_, _) =>
        {
            if (!_answered) Answered?.Invoke(false);
        };
    }

    private OverlapPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PlaceBottomRight();
    }

    public SessionFile? Other { get; }

    /// <summary>True for Yes, false for Not now (or ✕).</summary>
    public event Action<bool>? Answered;

    /// <summary>Shows a message without buttons, then closes it after <paramref name="duration"/>.</summary>
    public static OverlapPromptWindow ShowNotice(string message, TimeSpan duration)
    {
        var window = new OverlapPromptWindow();
        window.MessageText.Text = message;
        window.DetailText.Text = "This copy closes now without saving.";
        window.Buttons.Visibility = Visibility.Collapsed;
        window.Show();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        timer.Start();
        return window;
    }

    /// <summary>Closes without counting as an answer (the overlap ended or the app is closing).</summary>
    public void Dismiss()
    {
        _answered = true;
        Close();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => Answer(true);

    private void NotNow_Click(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool yes)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(yes);
        Close();
    }

    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 130; // clear of the timer widget's default bottom-right spot
    }
}

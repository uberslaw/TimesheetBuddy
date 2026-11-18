using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>
/// Small always-on-top timer shown while the main window is minimized.
/// It has no Owner on purpose: owned windows are hidden when their owner minimizes.
/// </summary>
public partial class TimerWidgetWindow : Window
{
    private const double ClickSlop = 3;
    private readonly AppState _state = App.State;
    private bool _listening;

    public TimerWidgetWindow()
    {
        InitializeComponent();
        Closed += (_, _) => StopListening();
    }

    public event EventHandler? RestoreRequested;

    public void ShowWidget()
    {
        PlaceOnScreen();
        Refresh();
        StartListening();
        if (!IsVisible)
            Show();
    }

    public void HideWidget()
    {
        StopListening();
        if (IsVisible)
            Hide();
    }

    private void PlaceOnScreen()
    {
        var work = SystemParameters.WorkArea;
        var primaryWork = new WidgetArea(work.Left, work.Top, work.Right, work.Bottom);
        var virtualScreen = new WidgetArea(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight);
        var singleMonitor =
            Math.Abs(SystemParameters.VirtualScreenWidth - SystemParameters.PrimaryScreenWidth) < 1 &&
            Math.Abs(SystemParameters.VirtualScreenHeight - SystemParameters.PrimaryScreenHeight) < 1;

        var (left, top) = TimerWidgetLayout.Resolve(
            _state.Settings.WidgetLeft,
            _state.Settings.WidgetTop,
            Width,
            Height,
            primaryWork,
            virtualScreen,
            singleMonitor);
        Left = left;
        Top = top;
    }

    private void StartListening()
    {
        if (_listening) return;
        _state.PropertyChanged += StateOnPropertyChanged;
        _listening = true;
    }

    private void StopListening()
    {
        if (!_listening) return;
        _state.PropertyChanged -= StateOnPropertyChanged;
        _listening = false;
    }

    // MainWindow's one-second tick calls AppState.Tick, which raises ElapsedDisplay; no second timer needed.
    private void StateOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
            Refresh();
        else
            Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var session = _state.IsHistoryMode ? null : _state.ActiveSession;
        var running = session is not null;
        var code = running ? _state.FindJobCode(session!.JobCodeId)?.Code : null;

        HeaderText.Text = TimerWidgetLayout.HeaderLine(running, code, session?.JobCodeText, session?.Category);
        TaskText.Text = TimerWidgetLayout.TaskLine(running, session?.TaskName);
        TaskText.ToolTip = TaskText.Text;
        ElapsedText.Text = running ? _state.ElapsedDisplay : "00:00:00";
    }

    private void Widget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            RestoreRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var startLeft = Left;
        var startTop = Top;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Button already released before DragMove started; treat as a click.
        }

        var moved = Math.Abs(Left - startLeft) > ClickSlop || Math.Abs(Top - startTop) > ClickSlop;
        if (moved)
            _state.SaveWidgetPosition(Left, Top);
        else
            RestoreRequested?.Invoke(this, EventArgs.Empty);
    }
}

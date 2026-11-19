using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TimesheetBuddy.Services;

namespace TimesheetBuddy.Views;

/// <summary>
/// Small always-on-top timer shown while the main window can't be seen (see <see cref="WidgetVisibilityRules"/>).
/// It has no Owner on purpose: owned windows are hidden when their owner minimizes.
/// </summary>
/// <remarks>
/// Start, Stop and the corner arrows are buttons, which handle their own mouse-down, so they
/// never reach <see cref="Widget_MouseLeftButtonDown"/> (drag or open). Anywhere else: press,
/// move and release drags; a press and release without moving opens the app.
/// </remarks>
public partial class TimerWidgetWindow : Window
{
    private const double ClickSlop = 3;
    private const uint MonitorDefaultToNearest = 2;
    private readonly AppState _state = App.State;
    private bool _listening;

    public TimerWidgetWindow()
    {
        InitializeComponent();
        Closed += (_, _) => StopListening();
    }

    public event EventHandler? RestoreRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;

    public void ShowWidget()
    {
        PlaceOnScreen();
        Refresh(full: true);
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
        var full = e.PropertyName is not (nameof(AppState.Elapsed) or nameof(AppState.ElapsedDisplay));
        if (Dispatcher.CheckAccess())
            Refresh(full);
        else
            Dispatcher.BeginInvoke(() => Refresh(full));
    }

    private void Refresh(bool full)
    {
        var history = _state.IsHistoryMode;
        var session = history ? null : _state.ActiveSession;
        var running = session is not null;
        var noTask = !history && _state.IsNoTaskRunning;
        ElapsedText.Text = running || noTask ? _state.ElapsedDisplay : DayTotals.FormatCompact(TimeSpan.Zero);
        if (!full)
            return;

        var code = running ? _state.FindJobCode(session!.JobCodeId)?.Code : null;
        HeaderText.Text = TimerWidgetLayout.HeaderLine(running, code, session?.JobCodeText, session?.Category);
        TaskText.Text = TimerWidgetLayout.TaskLine(running, session?.TaskName, noTask);
        TaskText.ToolTip = TaskText.Text;

        StartButton.IsEnabled = !history && !running;
        StopButton.IsEnabled = running;
        if (history)
        {
            StartButton.ToolTip = "Start (not while editing an older timesheet)";
        }
        else if (running)
        {
            StartButton.ToolTip = "Start (a task is already running)";
        }
        else
        {
            var last = _state.LastWorkedEntry();
            StartButton.ToolTip = last is null
                ? "Start: open Timesheet Buddy to pick a task"
                : $"Start: resume {last.TaskName.Trim()}";
        }

        StopButton.ToolTip = running ? $"Stop {session!.TaskName}" : "Stop (no task is running)";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ControlSlot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Dock_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<WidgetCorner>(tag, out var corner))
            return;

        var (left, top) = TimerWidgetLayout.CornerPosition(CurrentWorkArea(), Width, Height, corner);
        Left = left;
        Top = top;
        _state.SaveWidgetPosition(Left, Top);
    }

    /// <summary>Work area (excluding the taskbar) of the monitor the widget is on, in WPF units.</summary>
    private WidgetArea CurrentWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var pixels = new WidgetArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
                return TimerWidgetLayout.FromDevice(pixels, toDip.M11, toDip.M22);
            }
        }

        var work = SystemParameters.WorkArea;
        return new WidgetArea(work.Left, work.Top, work.Right, work.Bottom);
    }

    private void Widget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnControl(e.OriginalSource as DependencyObject))
            return;

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

    private bool IsOnControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase || ReferenceEquals(source, StartSlot) || ReferenceEquals(source, StopSlot))
                return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}

using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TimesheetBuddy.Services;

/// <summary>
/// Listens for Windows session lock and unlock. The callback is not on the UI thread,
/// so the decision and prompt are posted to the WPF dispatcher.
/// </summary>
public sealed class ScreenLockWatcher : IDisposable
{
    private readonly AppState _state;
    private readonly ScreenLockTracker _tracker = new();
    private bool _hooked;

    public ScreenLockWatcher(AppState state) => _state = state;

    public event Action<LockIntervalSplit.Snapshot, DateTime, TimeSpan>? UnlockPrompt;

    public void Start()
    {
        if (_hooked) return;

        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _hooked = true;
        }
        catch (PlatformNotSupportedException ex)
        {
            App.WriteCrashLog(ex);
        }
    }

    public void Dispose()
    {
        if (!_hooked) return;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hooked = false;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        var reason = e.Reason;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        dispatcher.BeginInvoke(new Action(() => Handle(reason)));
    }

    private void Handle(SessionSwitchReason reason)
    {
        var now = DateTime.UtcNow;
        if (reason == SessionSwitchReason.SessionLock)
        {
            _tracker.NoteLock(_state.ActiveSession, now);
            return;
        }

        if (reason != SessionSwitchReason.SessionUnlock)
            return;

        if (!_tracker.TryTakeUnlock(_state.ActiveSession, now, out var snapshot, out var lockedFor))
            return;
        if (snapshot is null)
            return;

        UnlockPrompt?.Invoke(snapshot, now, lockedFor);
    }
}

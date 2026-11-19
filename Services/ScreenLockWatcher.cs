using System.Runtime.InteropServices;
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

    /// <summary>True on session lock, false on unlock (on the UI thread).</summary>
    public event Action<bool>? LockChanged;

    /// <summary>Whether the session is locked now: probed at <see cref="Start"/>, then kept by the lock events.</summary>
    public bool IsLocked { get; private set; }

    public void Start()
    {
        if (_hooked) return;
        IsLocked = SessionLockProbe.IsLocked();

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
            IsLocked = true;
            _tracker.NoteLock(_state.ActiveSession, now);
            LockChanged?.Invoke(true);
            return;
        }

        if (reason != SessionSwitchReason.SessionUnlock)
            return;

        IsLocked = false;
        LockChanged?.Invoke(false);

        if (!_tracker.TryTakeUnlock(_state.ActiveSession, now, out var snapshot, out var lockedFor))
            return;
        if (snapshot is null)
            return;

        UnlockPrompt?.Invoke(snapshot, now, lockedFor);
    }
}

/// <summary>
/// Whether the workstation is locked right now (used once at start-up; afterwards the SessionSwitch events are
/// followed). The input desktop can't be switched to while the lock screen (or a secure desktop) is showing.
/// </summary>
public static class SessionLockProbe
{
    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SwitchDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    public static bool IsLocked()
    {
        try
        {
            var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
            if (desktop == IntPtr.Zero) return true;
            try
            {
                return !SwitchDesktop(desktop);
            }
            finally
            {
                CloseDesktop(desktop);
            }
        }
        catch
        {
            return false;
        }
    }
}

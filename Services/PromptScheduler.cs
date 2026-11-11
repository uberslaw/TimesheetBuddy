using System.Windows.Threading;

namespace TimesheetBuddy.Services;

public sealed class PromptScheduler
{
    private readonly AppState _state;
    private readonly DispatcherTimer _recheckTimer;
    private readonly DispatcherTimer _fifteenTimer;
    private DateTime _nextRecheckUtc = DateTime.MaxValue;
    private DateTime _nextFifteenUtc = DateTime.MaxValue;

    public PromptScheduler(AppState state)
    {
        _state = state;
        _recheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fifteenTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recheckTimer.Tick += (_, _) => CheckRecheck();
        _fifteenTimer.Tick += (_, _) => CheckFifteen();
    }

    public event Action? RecheckDue;
    public event Action? FifteenMinuteDue;

    public bool RecheckOpen { get; set; }
    public bool FifteenOpen { get; set; }

    public void Start()
    {
        ResetRecheck();
        ResetFifteen();
        _recheckTimer.Start();
        _fifteenTimer.Start();
        _state.SettingsChanged += (_, _) =>
        {
            ResetRecheck();
            ResetFifteen();
        };
    }

    public void ResetRecheck()
    {
        if (!_state.Settings.RecheckPromptEnabled || _state.ActiveSession is null)
        {
            _nextRecheckUtc = DateTime.MaxValue;
            return;
        }

        var delay = TimeSpan.FromHours(_state.Settings.RecheckHours)
                    + TimeSpan.FromMinutes(_state.Settings.RecheckMinutes);
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMinutes(15);
        _nextRecheckUtc = DateTime.UtcNow + delay;
    }

    public void ResetFifteen()
    {
        if (!_state.Settings.FifteenMinutePromptEnabled)
        {
            _nextFifteenUtc = DateTime.MaxValue;
            return;
        }

        var minutes = _state.CatchUpIntervalMinutes;
        _nextFifteenUtc = DateTime.UtcNow.AddMinutes(minutes);
    }

    /// <summary>
    /// If the long-running recheck is already due, push it forward so a catch-up
    /// cycle does not stack a second "still working?" dialog.
    /// </summary>
    public void ResetRecheckIfDue()
    {
        if (_nextRecheckUtc <= DateTime.UtcNow)
            ResetRecheck();
    }

    public void OnSessionChanged()
    {
        ResetRecheck();
    }

    private void CheckRecheck()
    {
        if (RecheckOpen || FifteenOpen) return;
        if (!_state.Settings.RecheckPromptEnabled) return;
        if (_state.ActiveSession is null) return;
        if (DateTime.UtcNow < _nextRecheckUtc) return;

        RecheckDue?.Invoke();
    }

    private void CheckFifteen()
    {
        if (FifteenOpen || RecheckOpen) return;
        if (!_state.Settings.FifteenMinutePromptEnabled) return;
        if (DateTime.UtcNow < _nextFifteenUtc) return;

        FifteenMinuteDue?.Invoke();
    }
}

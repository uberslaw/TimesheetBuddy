using System.Windows.Threading;

namespace TimesheetBuddy.Services;

/// <summary>
/// App Track: samples the foreground program once a second on the UI thread while switched on,
/// feeds <see cref="AppUsageAccumulator"/>, and writes <c>app-usage-yyyy-MM-dd.json</c> to the
/// current data folder every minute, when switched off and on exit.
/// </summary>
public sealed class AppTracker
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(1);

    private readonly Func<string> _folder;
    private readonly AppUsageAccumulator _usage;
    private readonly AppNameCache _names = new(ForegroundApp.Describe);
    private readonly DispatcherTimer _timer;
    private DateTime _lastFlushUtc = DateTime.UtcNow;
    private bool _locked;
    private bool _sampleErrorLogged;

    public AppTracker(Func<string> folder)
    {
        _folder = folder;
        _usage = new AppUsageAccumulator(AppUsageAccumulator.DefaultIdleThreshold,
            day => AppUsageFiles.Load(_folder(), day));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SampleInterval };
        _timer.Tick += (_, _) => Tick();
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        if (IsRunning) return;
        _lastFlushUtc = DateTime.UtcNow;
        _timer.Start();
        Tick();
    }

    /// <summary>Stops sampling and saves. Safe to call when already stopped.</summary>
    public void Stop()
    {
        if (IsRunning)
        {
            _timer.Stop();
            _usage.Pause(DateTime.Now, SafeIdleTime());
        }

        Flush();
    }

    /// <summary>From the session lock watcher; while locked nothing accrues.</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (IsRunning) Tick();
    }

    /// <summary>Today's or another day's totals, including time not yet saved.</summary>
    public IReadOnlyList<AppUsageTotal> Totals(DateOnly day) => _usage.Totals(day);

    /// <summary>Writes every day with unsaved time to the current data folder.</summary>
    public void Flush()
    {
        _lastFlushUtc = DateTime.UtcNow;
        try
        {
            var folder = _folder();
            foreach (var day in _usage.DirtyDays)
            {
                AppUsageFiles.Save(folder, day, _usage.Export(day));
                _usage.MarkSaved(day);
            }

            _usage.EvictSaved(DateOnly.FromDateTime(DateTime.Now));
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex);
        }
    }

    private void Tick()
    {
        try
        {
            var app = _locked ? null : ForegroundApp.Current(_names);
            _usage.Sample(DateTime.Now, app, SafeIdleTime(), _locked);
        }
        catch (Exception ex)
        {
            if (!_sampleErrorLogged)
            {
                _sampleErrorLogged = true;
                App.WriteCrashLog(ex);
            }
        }

        if (DateTime.UtcNow - _lastFlushUtc >= FlushInterval)
            Flush();
    }

    private static TimeSpan SafeIdleTime()
    {
        try
        {
            return ForegroundApp.IdleTime();
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}

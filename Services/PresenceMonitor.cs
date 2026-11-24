using System.Diagnostics;
using System.Windows.Threading;

namespace TimesheetBuddy.Services;

/// <summary>
/// Runs the two-PCs-open presence on the UI thread: every <see cref="PresenceTiming.Heartbeat"/> (and once at
/// start) it pulls other PCs' saved changes, rewrites this app's <c>sessions\&lt;clientid&gt;.json</c>, reads the
/// other session files and asks <see cref="OverlapRules.Decide"/> what to do. The UI acts on
/// <see cref="DecisionMade"/>.
/// </summary>
public sealed class PresenceMonitor
{
    private readonly DispatcherTimer _timer;
    private readonly Func<string> _folder;
    private readonly ClientIdentity _client;
    private readonly AppState _state;
    private readonly DateTime _processStartUtc;
    private readonly HashSet<string> _dismissed = [];
    private readonly HashSet<string> _announced = [];
    private readonly HashSet<string> _pullErrors = [];
    private DateTime? _confirmedAtUtc;
    private CloseRequest? _request;
    private bool _stopped;
    private bool _ticking;

    public PresenceMonitor(Func<string> folder, ClientIdentity client, AppState state)
    {
        _folder = folder;
        _client = client;
        _state = state;
        try
        {
            _processStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            _processStartUtc = DateTime.UtcNow;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PresenceTiming.Heartbeat };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Raised on the UI thread for every decision other than None.</summary>
    public event Action<OverlapRules.Decision>? DecisionMade;

    public Func<bool> IsScreenLocked { get; set; } = () => false;

    /// <summary>Set by the UI while the "Are you active on this one?" prompt is showing.</summary>
    public bool PromptOpen { get; set; }

    private OverlapRules.Me Me => new(_client.ClientId, _client.Machine, _processStartUtc, _confirmedAtUtc);

    public void Start()
    {
        if (_stopped) return;
        _timer.Start();
        Tick();
    }

    /// <summary>One heartbeat; also called after an unlock so a held-back prompt shows at once.</summary>
    public void Tick()
    {
        if (_stopped || _ticking) return;
        _ticking = true;
        try
        {
            var now = DateTime.UtcNow;
            try
            {
                _state.SyncFromDisk();
            }
            catch (Exception ex)
            {
                // The save path merges again; a failed pull only delays showing the other PC's entries.
                // Logged once per kind of failure: this runs every 20 s.
                if (_pullErrors.Add($"{ex.GetType().FullName}: {ex.Message}"))
                    App.WriteCrashLog(ex);
            }

            var others = SessionFiles.ReadOthers(_folder(), _client.ClientId);
            if (OverlapRules.ShouldClearConfirmation(Me, _request, others, now))
            {
                _state.LogOverlap(_request is null ? "Overlap over" : "Overlap over: the other PC's session has ended");
                _confirmedAtUtc = null;
                _request = null;
            }

            WriteOwn(now);
            foreach (var other in OverlapRules.LiveOthers(others, Me, now))
            {
                if (_announced.Add(OverlapRules.SessionKey(other)))
                    _state.LogOverlap($"Also open on {other.Machine}");
            }

            var decision = OverlapRules.Decide(Me, others, now, IsScreenLocked(), PromptOpen, _dismissed);
            if (decision.Kind != OverlapRules.Kind.None && !_stopped)
                DecisionMade?.Invoke(decision);
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>Yes on the prompt: this PC is the active one; the other session is asked to close.</summary>
    public void Confirm(SessionFile other)
    {
        var now = DateTime.UtcNow;
        _confirmedAtUtc = now;
        _request = OverlapRules.RequestFor(other, Me, now);
        WriteOwn(now);
        _state.LogOverlap($"Confirmed active on this PC; asked {other.Machine} to close");
    }

    /// <summary>Not now: no more asking about that session until its app restarts.</summary>
    public void Dismiss(SessionFile other)
    {
        _dismissed.Add(OverlapRules.SessionKey(other));
        _state.LogOverlap($"Not now: both stay open (also open on {other.Machine})");
    }

    /// <summary>The other session's file as it is now, or null once that app has closed.</summary>
    public SessionFile? Read(Guid clientId) =>
        SessionFiles.ReadOthers(_folder(), _client.ClientId).FirstOrDefault(s => s.ClientId == clientId);

    /// <summary>Stops the heartbeat; on exit the own session file is deleted so other PCs stop seeing it.</summary>
    public void Stop(bool deleteFile)
    {
        if (_stopped) return;
        _stopped = true;
        _timer.Stop();
        if (deleteFile) SessionFiles.Delete(_folder(), _client.ClientId);
    }

    private void WriteOwn(DateTime nowUtc)
    {
        var today = DateTime.Today;
        var (entries, running) = OverlapMerge.Snapshot(_state.Settings, today);
        SessionFiles.Write(_folder(), new SessionFile
        {
            ClientId = _client.ClientId,
            Machine = _client.Machine,
            ProcessStartUtc = _processStartUtc,
            LastHeartbeatUtc = nowUtc,
            ConfirmedActiveAtUtc = _confirmedAtUtc,
            CloseRequest = _request,
            TodayDate = today.ToString("yyyy-MM-dd"),
            TodayEntries = entries,
            Running = running
        });
    }
}

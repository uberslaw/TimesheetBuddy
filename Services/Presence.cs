using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>Timings for two PCs open on one data folder. Change them here.</summary>
public static class PresenceTiming
{
    /// <summary>How often each running app rewrites its session file and reads the others.</summary>
    public static TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>A session file whose heartbeat is younger than this belongs to a running app.</summary>
    public static TimeSpan LiveWindow { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>How long "Already confirmed on …" shows before the app closes.</summary>
    public static TimeSpan AlreadyConfirmedNotice { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>A close without saving that has not finished by then ends the process.</summary>
    public static TimeSpan ForcedExitFallback { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>"Please close": written by the PC where the user said Yes, aimed at one running session.</summary>
public sealed class CloseRequest
{
    public Guid TargetClientId { get; set; }

    /// <summary>The target session's own process start, so a later launch of that install is never closed by it.</summary>
    public DateTime TargetProcessStartUtc { get; set; }

    public DateTime RequestedAtUtc { get; set; }
    public Guid ByClientId { get; set; }
    public string ByMachine { get; set; } = string.Empty;
}

/// <summary>The running timer as the session's app holds it in memory (it may not be in settings.json yet).</summary>
public sealed class RunningSnapshot
{
    public Guid EntryId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public Guid? JobCodeId { get; set; }
    public string JobCodeText { get; set; } = string.Empty;
    public string Ticket { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
}

/// <summary><c>sessions\&lt;clientid&gt;.json</c>: one running app's presence, rewritten every heartbeat by that app only.</summary>
public sealed class SessionFile
{
    public Guid ClientId { get; set; }
    public string Machine { get; set; } = string.Empty;
    public DateTime ProcessStartUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public DateTime? ConfirmedActiveAtUtc { get; set; }
    public CloseRequest? CloseRequest { get; set; }

    /// <summary>Local day the snapshot below is for (yyyy-MM-dd).</summary>
    public string TodayDate { get; set; } = string.Empty;

    /// <summary>Today's finished, non-deleted entries as this app holds them.</summary>
    public List<TimeEntry> TodayEntries { get; set; } = [];

    public RunningSnapshot? Running { get; set; }
}

/// <summary>Reads and writes the session files in the data folder. Never throws. No WPF here.</summary>
public static class SessionFiles
{
    public const string FolderName = "sessions";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FolderFor(string dataFolder) => Path.Combine(dataFolder, FolderName);

    public static string PathFor(string dataFolder, Guid clientId) => Path.Combine(FolderFor(dataFolder), $"{clientId:N}.json");

    public static bool Write(string dataFolder, SessionFile session)
    {
        try
        {
            Directory.CreateDirectory(FolderFor(dataFolder));
            IoRetry.WriteAllTextAtomic(PathFor(dataFolder, session.ClientId), JsonSerializer.Serialize(session, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Delete(string dataFolder, Guid clientId)
    {
        try
        {
            File.Delete(PathFor(dataFolder, clientId));
        }
        catch
        {
            // A stale file is ignored by everyone after LiveWindow.
        }
    }

    /// <summary>Every readable session file except this client's own.</summary>
    public static IReadOnlyList<SessionFile> ReadOthers(string dataFolder, Guid clientId)
    {
        var list = new List<SessionFile>();
        try
        {
            var folder = FolderFor(dataFolder);
            if (!Directory.Exists(folder)) return list;
            foreach (var path in Directory.GetFiles(folder, "*.json"))
            {
                if (Read(path) is { } s && s.ClientId != clientId && s.ClientId != Guid.Empty)
                    list.Add(s);
            }
        }
        catch
        {
            // Folder unavailable: nobody else is seen this tick.
        }

        return list;
    }

    public static SessionFile? Read(string path)
    {
        try
        {
            var session = JsonSerializer.Deserialize<SessionFile>(IoRetry.ReadAllText(path), JsonOptions);
            if (session is null) return null;
            session.TodayEntries ??= [];
            session.Machine ??= string.Empty;
            session.ProcessStartUtc = AsUtc(session.ProcessStartUtc);
            session.LastHeartbeatUtc = AsUtc(session.LastHeartbeatUtc);
            if (session.ConfirmedActiveAtUtc is DateTime c) session.ConfirmedActiveAtUtc = AsUtc(c);
            if (session.CloseRequest is { } r)
            {
                r.RequestedAtUtc = AsUtc(r.RequestedAtUtc);
                r.TargetProcessStartUtc = AsUtc(r.TargetProcessStartUtc);
            }

            return session;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

/// <summary>
/// What a running app does about other running apps on the same data folder. Pure: every input is passed in.
/// Input-based activity (GetLastInputInfo) is deliberately not used; only the user's Yes counts.
/// </summary>
public static class OverlapRules
{
    public enum Kind
    {
        None,

        /// <summary>Ask "Are you active on this one?" about <see cref="Decision.Other"/>.</summary>
        ShowPrompt,

        /// <summary>The prompt is open but nothing overlaps any more; close it.</summary>
        HidePrompt,

        /// <summary>Another PC confirmed and asked this one to close: close now without saving.</summary>
        CloseNow,

        /// <summary>Both said Yes and the other was first: show "Already confirmed on …", then close without saving.</summary>
        AlreadyConfirmedThenClose
    }

    public sealed record Decision(Kind Kind, SessionFile? Other = null);

    /// <summary>This app's side of the decision.</summary>
    public sealed record Me(Guid ClientId, string Machine, DateTime ProcessStartUtc, DateTime? ConfirmedActiveAtUtc);

    public static bool IsLive(SessionFile s, Guid myClientId, DateTime nowUtc) =>
        s.ClientId != myClientId && nowUtc - s.LastHeartbeatUtc < PresenceTiming.LiveWindow;

    public static bool OtherMachine(SessionFile s, string myMachine) =>
        !string.Equals(s.Machine?.Trim(), myMachine?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Live sessions of other installs on other machines.</summary>
    public static IReadOnlyList<SessionFile> LiveOthers(IEnumerable<SessionFile> sessions, Me me, DateTime nowUtc) =>
        sessions.Where(s => IsLive(s, me.ClientId, nowUtc) && OtherMachine(s, me.Machine))
            .OrderBy(s => s.ProcessStartUtc)
            .ToList();

    /// <summary>When the other side said Yes: its confirmation, else the time it asked this one to close.</summary>
    public static DateTime? ConfirmedAt(SessionFile s) => s.ConfirmedActiveAtUtc ?? s.CloseRequest?.RequestedAtUtc;

    /// <summary>Both said Yes: the earlier confirmation wins; on a tie the smaller client id wins.</summary>
    public static bool FirstWins(DateTime aAt, Guid a, DateTime bAt, Guid b) =>
        aAt < bAt || (aAt == bAt && a.CompareTo(b) < 0);

    /// <summary>A close request counts only when aimed at this very session (client id and process start).</summary>
    public static bool TargetsMe(CloseRequest? request, Me me) =>
        request is not null && request.TargetClientId == me.ClientId &&
        Math.Abs((request.TargetProcessStartUtc - me.ProcessStartUtc).TotalSeconds) < 1;

    /// <summary>
    /// The decision for one tick. Order: a close request aimed at this session (unless this one said Yes first),
    /// then both-said-Yes, then the prompt. A locked screen never confirms and never denies: it only holds the
    /// prompt back, and closing still happens when asked. <paramref name="dismissed"/> holds
    /// <see cref="SessionKey"/>s the user answered Not now for.
    /// </summary>
    public static Decision Decide(Me me, IEnumerable<SessionFile> sessions, DateTime nowUtc, bool screenLocked,
        bool promptOpen, IReadOnlySet<string> dismissed)
    {
        var others = LiveOthers(sessions, me, nowUtc);

        foreach (var other in others)
        {
            if (!TargetsMe(other.CloseRequest, me)) continue;
            var theirAt = ConfirmedAt(other)!.Value;
            if (me.ConfirmedActiveAtUtc is DateTime mine)
            {
                if (FirstWins(mine, me.ClientId, theirAt, other.ClientId)) continue;
                return new Decision(Kind.AlreadyConfirmedThenClose, other);
            }

            return new Decision(Kind.CloseNow, other);
        }

        if (me.ConfirmedActiveAtUtc is DateTime myAt)
        {
            foreach (var other in others)
            {
                if (ConfirmedAt(other) is DateTime theirAt && !FirstWins(myAt, me.ClientId, theirAt, other.ClientId))
                    return new Decision(Kind.AlreadyConfirmedThenClose, other);
            }

            return new Decision(Kind.None);
        }

        var candidate = others.FirstOrDefault(o => !dismissed.Contains(SessionKey(o)));
        if (candidate is null) return new Decision(promptOpen ? Kind.HidePrompt : Kind.None);
        if (screenLocked || promptOpen) return new Decision(Kind.None, candidate);
        return new Decision(Kind.ShowPrompt, candidate);
    }

    /// <summary>One running session of one install: Not now applies to it until that app restarts.</summary>
    public static string SessionKey(SessionFile s) => $"{s.ClientId:N}@{s.ProcessStartUtc.Ticks}";

    /// <summary>
    /// After the overlap is over (no live session on another machine), the Yes and any close request are cleared,
    /// so a later overlap starts fresh and a stale request can never close anything.
    /// </summary>
    public static bool ShouldClearConfirmation(Me me, CloseRequest? myRequest, IEnumerable<SessionFile> sessions, DateTime nowUtc)
    {
        if (me.ConfirmedActiveAtUtc is null && myRequest is null) return false;
        var others = LiveOthers(sessions, me, nowUtc);
        if (myRequest is not null && others.Any(o => o.ClientId == myRequest.TargetClientId &&
                                                     o.ProcessStartUtc == myRequest.TargetProcessStartUtc))
            return false;
        return others.Count == 0 || myRequest is not null;
    }

    /// <summary>The close request written with Yes, aimed at the other session.</summary>
    public static CloseRequest RequestFor(SessionFile other, Me me, DateTime nowUtc) => new()
    {
        TargetClientId = other.ClientId,
        TargetProcessStartUtc = other.ProcessStartUtc,
        RequestedAtUtc = nowUtc,
        ByClientId = me.ClientId,
        ByMachine = me.Machine
    };
}

/// <summary>The Keep / Discard choice after Yes: what the other PC has that this one does not. Pure.</summary>
public static class OverlapMerge
{
    public sealed record Item(Guid Id, TimeEntry Entry, bool IsRunning, bool AlreadyHere);

    /// <summary>A running timer ends now while its app is live, else at its last heartbeat.</summary>
    public static DateTime RunningEnd(SessionFile other, DateTime nowUtc) =>
        nowUtc - other.LastHeartbeatUtc < PresenceTiming.LiveWindow ? nowUtc : other.LastHeartbeatUtc;

    /// <summary>
    /// The other session's entries for today plus its running timer (as a finished entry). Entries this data
    /// already holds (same id) are marked <see cref="Item.AlreadyHere"/>; entries this data deleted for good
    /// (tombstones) are left out.
    /// </summary>
    public static IReadOnlyList<Item> Items(AppSettings mine, SessionFile other, DateTime nowUtc)
    {
        var have = (mine.Entries ?? []).Select(e => e.Id).ToHashSet();
        var removed = (mine.RemovedEntries ?? []).Select(r => r.Id).ToHashSet();
        var items = new List<Item>();
        var seen = new HashSet<Guid>();
        foreach (var e in other.TodayEntries ?? [])
        {
            if (e is null || e.IsDeleted || e.StartUtc == default || !seen.Add(e.Id) || removed.Contains(e.Id)) continue;
            if (e.EndUtc is null && e.ManualMinutes <= 0) continue;
            items.Add(new Item(e.Id, SettingsMerge.Clone(e), false, have.Contains(e.Id)));
        }

        if (other.Running is { } r && r.StartedUtc != default && !removed.Contains(r.EntryId) && seen.Add(r.EntryId))
        {
            var end = RunningEnd(other, nowUtc);
            if (end > r.StartedUtc)
            {
                var entry = new TimeEntry
                {
                    Id = r.EntryId == Guid.Empty ? Guid.NewGuid() : r.EntryId,
                    StartUtc = r.StartedUtc,
                    EndUtc = end,
                    TaskName = r.TaskName ?? string.Empty,
                    Category = string.IsNullOrWhiteSpace(r.Category) ? "General" : r.Category,
                    JobCodeId = r.JobCodeId,
                    JobCodeText = r.JobCodeText ?? string.Empty,
                    Ticket = r.Ticket ?? string.Empty,
                    Notes = "Running on " + other.Machine,
                    Source = "timer",
                    CreatedByClientId = other.ClientId,
                    CreatedByMachine = other.Machine
                };
                var closedHere = (mine.Entries ?? []).FirstOrDefault(e => e.Id == entry.Id);
                items.Add(new Item(entry.Id, entry, true, closedHere is { EndUtc: not null }));
            }
        }

        return items.OrderBy(i => i.Entry.StartUtc).ToList();
    }

    /// <summary>
    /// Keep selected: adds the chosen items that are not already here (a job code unknown here is matched by its
    /// label, else kept as text). Discard all is simply an empty selection. Returns the added entries.
    /// </summary>
    public static IReadOnlyList<TimeEntry> Keep(AppSettings mine, IEnumerable<Item> items, IReadOnlySet<Guid> selected)
    {
        mine.Entries ??= [];
        var have = mine.Entries.Select(e => e.Id).ToHashSet();
        var added = new List<TimeEntry>();
        foreach (var item in items)
        {
            if (!selected.Contains(item.Id) || item.AlreadyHere || !have.Add(item.Id)) continue;
            var entry = SettingsMerge.Clone(item.Entry);
            if (entry.JobCodeId is Guid id && mine.JobCodes.All(c => c.Id != id))
            {
                var byLabel = mine.JobCodes.FirstOrDefault(c =>
                    string.Equals(c.Display, entry.JobCodeText?.Trim(), StringComparison.OrdinalIgnoreCase));
                entry.JobCodeId = byLabel?.Id;
            }

            mine.Entries.Add(entry);
            added.Add(entry);
        }

        return added;
    }

    /// <summary>The snapshot each app publishes in its session file: today's finished entries and the running timer.</summary>
    public static (List<TimeEntry> Today, RunningSnapshot? Running) Snapshot(AppSettings settings, DateTime todayLocal)
    {
        var activeId = settings.ActiveSession?.EntryId;
        var today = (settings.Entries ?? [])
            .Where(e => !e.IsDeleted && e.Id != activeId && (e.EndUtc is not null || e.ManualMinutes > 0) &&
                        (e.StartUtc.ToLocalTime().Date == todayLocal.Date || (e.EndUtc ?? e.StartUtc).ToLocalTime().Date == todayLocal.Date))
            .Select(SettingsMerge.Clone)
            .ToList();
        RunningSnapshot? running = settings.ActiveSession is { } s
            ? new RunningSnapshot
            {
                EntryId = s.EntryId,
                TaskName = s.TaskName,
                Category = s.Category,
                JobCodeId = s.JobCodeId,
                JobCodeText = s.JobCodeText,
                Ticket = s.Ticket,
                StartedUtc = s.StartedUtc
            }
            : null;
        return (today, running);
    }
}

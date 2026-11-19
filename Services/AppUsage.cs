using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TimesheetBuddy.Services;

/// <summary>
/// One program as App Track records it: the exe file name (no folder) and a friendly name.
/// Window titles, URLs and document text are never part of it.
/// </summary>
public sealed record AppIdentity(string Exe, string Name)
{
    /// <summary>Totals are keyed on the exe file name, so an update that moves the exe keeps one row.</summary>
    public string Key => AppNames.KeyFor(Exe, Name);
}

/// <summary>One row of a day's App Track totals.</summary>
public sealed record AppUsageTotal(string Exe, string Name, TimeSpan Time);

/// <summary>Contents of <c>app-usage-yyyy-MM-dd.json</c>.</summary>
public sealed class AppUsageDay
{
    public string Date { get; set; } = string.Empty;
    public List<AppUsageApp> Apps { get; set; } = [];
}

public sealed class AppUsageApp
{
    public string Exe { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Seconds { get; set; }
}

public static class AppNames
{
    public const string Unknown = "Unknown app";
    public const string Self = "Timesheet Buddy";
    public const string SelfExe = "TimesheetBuddy.exe";

    /// <summary>File name after the last <c>\</c> or <c>/</c>, so Windows paths parse on any OS.</summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.Trim();
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        return trimmed[(slash + 1)..];
    }

    public static string WithoutExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>File description, else product name, else the exe name without its extension, else "Unknown app".</summary>
    public static string Friendly(string? fileDescription, string? productName, string? exePath)
    {
        if (!string.IsNullOrWhiteSpace(fileDescription)) return fileDescription.Trim();
        if (!string.IsNullOrWhiteSpace(productName)) return productName.Trim();
        var stem = WithoutExtension(FileName(exePath)).Trim();
        return stem.Length > 0 ? stem : Unknown;
    }

    public static string KeyFor(string? exe, string? name)
    {
        if (!string.IsNullOrWhiteSpace(exe)) return exe.Trim().ToLowerInvariant();
        return "name:" + (string.IsNullOrWhiteSpace(name) ? Unknown : name.Trim()).ToLowerInvariant();
    }
}

/// <summary>
/// Turns an exe path (or, when the process could not be opened, its process name) into an
/// <see cref="AppIdentity"/>. Version-info lookups are cached per exe path.
/// </summary>
public sealed class AppNameCache(Func<string, (string? Description, string? Product)> describe)
{
    private readonly Dictionary<string, AppIdentity> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byPath.Count;

    public AppIdentity Identify(string? exePath, string? processName)
    {
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var path = exePath.Trim();
            if (_byPath.TryGetValue(path, out var cached)) return cached;

            string? description = null, product = null;
            try
            {
                (description, product) = describe(path);
            }
            catch
            {
                // No version info: fall back to the exe name.
            }

            var identity = new AppIdentity(AppNames.FileName(path), AppNames.Friendly(description, product, path));
            _byPath[path] = identity;
            return identity;
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            var name = processName.Trim();
            var stem = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (stem.Length > 0) return new AppIdentity(stem + ".exe", stem);
        }

        return new AppIdentity(string.Empty, AppNames.Unknown);
    }
}

/// <summary>
/// Per-app, per-day time from one-second foreground samples. Pure: the caller supplies the clock,
/// the foreground app, the time since the last keyboard/mouse input and the lock state.
/// <para>
/// Time after the last input stays pending. Once input arrives it becomes committed; if the user
/// goes idle past the threshold, or the session locks, the pending part after the last input is
/// dropped, so an idle stretch never counts. Spans crossing midnight are split across days.
/// </para>
/// </summary>
public sealed class AppUsageAccumulator
{
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(5);

    /// <summary>A longer gap between samples (sleep, a stalled UI thread) is not credited.</summary>
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _idleThreshold;
    private readonly Func<DateOnly, AppUsageDay?> _load;
    private readonly Dictionary<DateOnly, Dictionary<string, AppUsageApp>> _days = [];
    private readonly HashSet<DateOnly> _dirty = [];
    private readonly List<Slice> _pending = [];
    private DateTime? _lastSample;

    private sealed record Slice(AppIdentity App, DateTime Start, DateTime End);

    public AppUsageAccumulator(TimeSpan idleThreshold, Func<DateOnly, AppUsageDay?>? load = null)
    {
        _idleThreshold = idleThreshold;
        _load = load ?? (_ => null);
    }

    public TimeSpan IdleThreshold => _idleThreshold;

    /// <summary>Uncommitted time since the last input (shown live, not yet saved).</summary>
    public TimeSpan PendingTime => _pending.Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.End - s.Start));

    public IReadOnlyList<DateOnly> DirtyDays => _dirty.Order().ToList();

    /// <summary>
    /// One sample. The interval since the previous sample goes to <paramref name="app"/> (null: no
    /// foreground app, nothing credited). Idle past the threshold or a locked session credits nothing.
    /// </summary>
    public void Sample(DateTime nowLocal, AppIdentity? app, TimeSpan idle, bool locked)
    {
        if (idle < TimeSpan.Zero) idle = TimeSpan.Zero;
        if (locked || idle > _idleThreshold)
        {
            Settle(nowLocal - idle, dropAfterInput: true);
            _lastSample = null;
            return;
        }

        if (app is not null && _lastSample is { } last && nowLocal > last && nowLocal - last <= MaxSampleGap)
            AddPending(app, last, nowLocal);
        _lastSample = nowLocal;
        Settle(nowLocal - idle, dropAfterInput: false);
    }

    /// <summary>
    /// Tracking stops (App Track off, exit). Pending time is kept unless the user has been idle past
    /// the threshold, in which case the part after the last input is dropped.
    /// </summary>
    public void Pause(DateTime nowLocal, TimeSpan idle)
    {
        if (idle < TimeSpan.Zero) idle = TimeSpan.Zero;
        if (idle > _idleThreshold)
            Settle(nowLocal - idle, dropAfterInput: true);
        else
            Settle(DateTime.MaxValue, dropAfterInput: false);
        _lastSample = null;
    }

    /// <summary>Committed plus pending time for a day, longest first (ties by name).</summary>
    public IReadOnlyList<AppUsageTotal> Totals(DateOnly day)
    {
        var rows = new Dictionary<string, (string Exe, string Name, TimeSpan Time)>();
        foreach (var (key, app) in Day(day))
            rows[key] = (app.Exe, app.Name, TimeSpan.FromSeconds(app.Seconds));

        foreach (var slice in _pending)
        {
            ForEachDayPiece(slice.Start, slice.End, (pieceDay, span) =>
            {
                if (pieceDay != day) return;
                var key = slice.App.Key;
                rows[key] = rows.TryGetValue(key, out var row)
                    ? (row.Exe, slice.App.Name, row.Time + span)
                    : (slice.App.Exe, slice.App.Name, span);
            });
        }

        return rows.Values
            .Where(r => r.Time > TimeSpan.Zero)
            .Select(r => new AppUsageTotal(r.Exe, r.Name, r.Time))
            .OrderByDescending(r => r.Time)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Committed totals of a day in file form (pending time is not included).</summary>
    public AppUsageDay Export(DateOnly day) => new()
    {
        Date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Apps = Day(day).Values
            .Where(a => a.Seconds > 0)
            .OrderByDescending(a => a.Seconds)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AppUsageApp { Exe = a.Exe, Name = a.Name, Seconds = Math.Round(a.Seconds, 3) })
            .ToList()
    };

    public void MarkSaved(DateOnly day) => _dirty.Remove(day);

    /// <summary>Forgets saved days other than <paramref name="keep"/>; they are read from disk again if needed.</summary>
    public void EvictSaved(DateOnly keep)
    {
        foreach (var day in _days.Keys.Where(d => d != keep && !_dirty.Contains(d)).ToList())
            _days.Remove(day);
    }

    private Dictionary<string, AppUsageApp> Day(DateOnly day)
    {
        if (_days.TryGetValue(day, out var apps)) return apps;

        apps = [];
        AppUsageDay? file;
        try
        {
            file = _load(day);
        }
        catch
        {
            // File busy or folder gone: not cached, so the saved totals are read again next time.
            return apps;
        }

        if (file is not null)
        {
            foreach (var app in file.Apps)
            {
                if (app is null || !double.IsFinite(app.Seconds) || app.Seconds <= 0) continue;
                var key = AppNames.KeyFor(app.Exe, app.Name);
                if (apps.TryGetValue(key, out var existing))
                    existing.Seconds += app.Seconds;
                else
                    apps[key] = new AppUsageApp
                    {
                        Exe = app.Exe ?? string.Empty,
                        Name = string.IsNullOrWhiteSpace(app.Name) ? AppNames.Unknown : app.Name,
                        Seconds = app.Seconds
                    };
            }
        }

        _days[day] = apps;
        return apps;
    }

    private void AddPending(AppIdentity app, DateTime start, DateTime end)
    {
        if (_pending.Count > 0 && _pending[^1] is { } tail && tail.End == start && tail.App.Key == app.Key)
            _pending[^1] = tail with { App = app, End = end };
        else
            _pending.Add(new Slice(app, start, end));
    }

    /// <summary>Commits pending time up to <paramref name="lastInput"/>; the rest is dropped or kept.</summary>
    private void Settle(DateTime lastInput, bool dropAfterInput)
    {
        if (_pending.Count == 0) return;

        var keep = new List<Slice>();
        foreach (var slice in _pending)
        {
            if (slice.End <= lastInput)
            {
                Commit(slice.App, slice.Start, slice.End);
                continue;
            }

            var after = slice;
            if (slice.Start < lastInput)
            {
                Commit(slice.App, slice.Start, lastInput);
                after = slice with { Start = lastInput };
            }

            if (!dropAfterInput)
                keep.Add(after);
        }

        _pending.Clear();
        _pending.AddRange(keep);
    }

    private void Commit(AppIdentity app, DateTime start, DateTime end)
    {
        ForEachDayPiece(start, end, (day, span) =>
        {
            var apps = Day(day);
            if (apps.TryGetValue(app.Key, out var row))
            {
                row.Seconds += span.TotalSeconds;
                row.Name = app.Name;
                if (row.Exe.Length == 0) row.Exe = app.Exe;
            }
            else
            {
                apps[app.Key] = new AppUsageApp { Exe = app.Exe, Name = app.Name, Seconds = span.TotalSeconds };
            }

            _dirty.Add(day);
        });
    }

    private static void ForEachDayPiece(DateTime start, DateTime end, Action<DateOnly, TimeSpan> piece)
    {
        var cursor = start;
        while (cursor < end)
        {
            var nextMidnight = cursor.Date.AddDays(1);
            var pieceEnd = end < nextMidnight ? end : nextMidnight;
            piece(DateOnly.FromDateTime(cursor), pieceEnd - cursor);
            cursor = pieceEnd;
        }
    }
}

/// <summary>Reads and writes <c>app-usage-yyyy-MM-dd.json</c> in the data folder.</summary>
public static class AppUsageFiles
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string FileName(DateOnly day) =>
        "app-usage-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json";

    /// <summary>
    /// The day's file, or null when there is none. An unreadable file is renamed to
    /// <c>….unreadable-yyyyMMddHHmmss</c> so the next save does not overwrite it.
    /// </summary>
    public static AppUsageDay? Load(string folder, DateOnly day)
    {
        var path = Path.Combine(folder, FileName(day));
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<AppUsageDay>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            try
            {
                File.Move(path, $"{path}.unreadable-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
            catch
            {
                // Leave it; the next save replaces it.
            }

            return null;
        }
    }

    public static void Save(string folder, DateOnly day, AppUsageDay data)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName(day));
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

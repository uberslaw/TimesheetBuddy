using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Import data… and the Merge choice of a data-folder change: read an older copy (a data folder, a
/// settings.json, a <c>.bak-…</c> backup, a daily snapshot or a OneDrive conflict copy), preview it and merge
/// it in without losing anything. Settings rules are in <see cref="SettingsMerge"/>; this adds day notes and
/// App Track files. No WPF here.
/// </summary>
public static class DataImport
{
    public const string NoteSeparatorPrefix = "--- Imported ";

    private static readonly JsonSerializerOptions UsageReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Regex ConflictCopy = new(@"^settings[-_ (].+\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public enum CandidateKind
    {
        ConflictCopy,
        Snapshot,
        Backup,
        Folder
    }

    public sealed record Candidate(string Path, string Label, CandidateKind Kind, DateTime Modified);

    /// <summary>What a chosen path offers: settings (may be null) and a folder to take day notes and App Track files from.</summary>
    public sealed class Source
    {
        public required string Path { get; init; }
        public AppSettings? Settings { get; init; }
        public string? FilesFolder { get; init; }
        public string? Error { get; init; }
    }

    public enum NoteOutcome
    {
        Unchanged,
        New,
        Appended
    }

    public sealed class FilesReport
    {
        public List<DateOnly> NotesNew { get; } = [];
        public List<DateOnly> NotesAppended { get; } = [];
        public int NotesSame { get; set; }
        public List<DateOnly> UsageNew { get; } = [];
        public List<DateOnly> UsageMerged { get; } = [];
        public int Unreadable { get; set; }

        public bool NothingToAdd => NotesNew.Count + NotesAppended.Count + UsageNew.Count + UsageMerged.Count == 0;
    }

    public sealed class Preview
    {
        public SettingsMerge.Report? Settings { get; init; }
        public required FilesReport Files { get; init; }

        public bool NothingToAdd => (Settings?.NothingToAdd ?? true) && Files.NothingToAdd;
    }

    /// <summary>Opens a folder or a file chosen in Import data….</summary>
    public static Source Open(string path, string currentFolder)
    {
        if (Directory.Exists(path))
        {
            if (DataFolder.SameFolder(path, currentFolder))
                return new Source { Path = path, Error = "That is the data folder in use. Pick an older copy instead." };

            var settingsPath = System.IO.Path.Combine(path, DataFolder.SettingsFileName);
            var settings = SettingsStore.ReadFile(settingsPath);
            if (File.Exists(settingsPath) && settings is null)
                return new Source { Path = path, Error = "The settings.json in that folder can't be read." };
            var hasFiles = DataFolder.DataFiles(path).Any(n => n != DataFolder.SettingsFileName);
            if (settings is null && !hasFiles)
                return new Source { Path = path, Error = "That folder has no Timesheet Buddy data." };
            return new Source { Path = path, Settings = settings, FilesFolder = path };
        }

        if (!File.Exists(path))
            return new Source { Path = path, Error = "That file or folder no longer exists." };

        var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var isSettings = string.Equals(System.IO.Path.GetFileName(path), DataFolder.SettingsFileName, StringComparison.OrdinalIgnoreCase);
        if (isSettings && DataFolder.SameFolder(folder, currentFolder))
            return new Source { Path = path, Error = "That is the settings file in use. Pick an older copy instead." };

        var parsed = SettingsStore.ReadFile(path);
        if (parsed is null)
            return new Source { Path = path, Error = "That file isn't a Timesheet Buddy settings file, or it can't be read." };

        return new Source { Path = path, Settings = parsed, FilesFolder = isSettings ? folder : null };
    }

    /// <summary>What importing would do, computed on copies; nothing is written.</summary>
    public static Preview PreviewImport(AppSettings current, Source source, string currentFolder, DateTime nowLocal) => new()
    {
        Settings = source.Settings is null
            ? null
            : SettingsMerge.Import(SettingsStore.Clone(current), SettingsStore.Clone(source.Settings), nowLocal.ToUniversalTime()),
        Files = source.FilesFolder is null
            ? new FilesReport()
            : MergeFiles(source.FilesFolder, currentFolder, nowLocal, dryRun: true)
    };

    /// <summary>
    /// Merges day notes and App Track files from <paramref name="fromFolder"/> into <paramref name="toFolder"/>.
    /// Notes: copied for days without notes; differing notes are appended under a separator. App usage: per
    /// program the larger time wins. With <paramref name="transaction"/>, each rewritten file is backed up first.
    /// </summary>
    public static FilesReport MergeFiles(string fromFolder, string toFolder, DateTime nowLocal, bool dryRun,
        FileTransaction? transaction = null)
    {
        var report = new FilesReport();
        foreach (var name in DataFolder.DataFiles(fromFolder))
        {
            if (name == DataFolder.SettingsFileName) continue;
            var source = Path.Combine(fromFolder, name);
            var target = Path.Combine(toFolder, name);

            if (name.StartsWith("day-notes-", StringComparison.OrdinalIgnoreCase))
            {
                var day = ParseDay(name, "day-notes-", "yyyyMMdd");
                if (day is null) continue;
                string imported;
                try { imported = IoRetry.ReadAllText(source); } catch { report.Unreadable++; continue; }
                var current = File.Exists(target) ? IoRetry.ReadAllText(target) : null;
                var (text, outcome) = MergeNote(current, imported, nowLocal);
                switch (outcome)
                {
                    case NoteOutcome.Unchanged: report.NotesSame++; continue;
                    case NoteOutcome.New: report.NotesNew.Add(day.Value); break;
                    case NoteOutcome.Appended: report.NotesAppended.Add(day.Value); break;
                }

                if (dryRun) continue;
                transaction?.CopyAside(target);
                IoRetry.WriteAllTextAtomic(target, text);
            }
            else
            {
                var day = ParseDay(name, "app-usage-", "yyyy-MM-dd");
                if (day is null) continue;
                var imported = ReadUsage(source);
                if (imported is null) { report.Unreadable++; continue; }
                var exists = File.Exists(target);
                var current = exists ? ReadUsage(target) : null;
                if (exists && current is null) { report.Unreadable++; continue; }
                var merged = MergeUsage(current, imported, out var changed);
                if (!changed) continue;
                (exists ? report.UsageMerged : report.UsageNew).Add(day.Value);

                if (dryRun) continue;
                merged.Date = day.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                transaction?.CopyAside(target);
                AppUsageFiles.Save(toFolder, day.Value, merged);
            }
        }

        return report;
    }

    /// <summary>
    /// Day-note rule: a blank import changes nothing; no current note takes the import; a current note that
    /// already contains the imported text is kept; otherwise the import is appended under a dated separator.
    /// </summary>
    public static (string Text, NoteOutcome Outcome) MergeNote(string? current, string? imported, DateTime nowLocal)
    {
        var incoming = (imported ?? string.Empty).Trim();
        if (incoming.Length == 0) return (current ?? string.Empty, NoteOutcome.Unchanged);
        if (string.IsNullOrWhiteSpace(current)) return (incoming, NoteOutcome.New);
        if (Flat(current).Contains(Flat(incoming), StringComparison.Ordinal))
            return (current, NoteOutcome.Unchanged);

        const string nl = "\r\n";
        var text = current.TrimEnd() + nl + nl +
                   $"{NoteSeparatorPrefix}{nowLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} ---" +
                   nl + incoming;
        return (text, NoteOutcome.Appended);
    }

    /// <summary>Per program (exe key) the larger time is kept; the current friendly name wins.</summary>
    public static AppUsageDay MergeUsage(AppUsageDay? current, AppUsageDay imported, out bool changed)
    {
        changed = false;
        var rows = new Dictionary<string, AppUsageApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in current?.Apps ?? [])
        {
            if (app is null || !double.IsFinite(app.Seconds) || app.Seconds <= 0) continue;
            var key = AppNames.KeyFor(app.Exe, app.Name);
            if (rows.TryGetValue(key, out var row)) row.Seconds += app.Seconds;
            else rows[key] = new AppUsageApp { Exe = app.Exe ?? string.Empty, Name = app.Name ?? AppNames.Unknown, Seconds = app.Seconds };
        }

        foreach (var app in imported.Apps ?? [])
        {
            if (app is null || !double.IsFinite(app.Seconds) || app.Seconds <= 0) continue;
            var key = AppNames.KeyFor(app.Exe, app.Name);
            if (rows.TryGetValue(key, out var row))
            {
                if (app.Seconds > row.Seconds + 0.0005)
                {
                    row.Seconds = app.Seconds;
                    changed = true;
                }
            }
            else
            {
                rows[key] = new AppUsageApp
                {
                    Exe = app.Exe ?? string.Empty,
                    Name = string.IsNullOrWhiteSpace(app.Name) ? AppNames.Unknown : app.Name,
                    Seconds = app.Seconds
                };
                changed = true;
            }
        }

        return new AppUsageDay
        {
            Date = current?.Date ?? imported.Date ?? string.Empty,
            Apps = rows.Values
                .OrderByDescending(a => a.Seconds)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public sealed record FolderMergeResult(
        SettingsMerge.Report Settings,
        FilesReport Files,
        string? SettingsBackup,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> NotRemoved);

    /// <summary>
    /// Merges the data in use (<paramref name="fromSettings"/>, saved in <paramref name="fromFolder"/>) into a
    /// folder that already has data, e.g. a OneDrive folder another PC uses. The target's settings.json is backed
    /// up to <c>.bak-…</c> first, entries are merged with the current copy winning on the same id, day notes and
    /// App Track files are merged, then the result is read back and checked: merging again must add nothing. Any
    /// failure restores the target's files and rethrows. Only then, with <paramref name="removeOriginals"/>, are
    /// the merged files deleted from <paramref name="fromFolder"/> (daily snapshots stay there).
    /// </summary>
    public static FolderMergeResult MergeFolder(string fromFolder, AppSettings fromSettings, string toFolder,
        bool removeOriginals, DateTime nowLocal)
    {
        var settingsPath = Path.Combine(toFolder, DataFolder.SettingsFileName);
        if (File.Exists(settingsPath) && SettingsStore.ReadFile(settingsPath) is null)
            throw new InvalidDataException("The settings.json in " + toFolder + " can't be read, so nothing was merged.");

        var nowUtc = nowLocal.ToUniversalTime();
        var transaction = new FileTransaction(nowLocal);
        SettingsMerge.Report report;
        FilesReport files;
        try
        {
            transaction.CopyAside(settingsPath);
            var store = new SettingsStore(toFolder) { Clock = () => nowLocal };
            var target = store.Load();
            var incoming = SettingsStore.Clone(fromSettings);
            report = SettingsMerge.Import(target, incoming, nowUtc, preferIncoming: true);
            foreach (var segment in incoming.UnassignedSegments)
            {
                if (!target.UnassignedSegments.Any(s => s.StartUtc == segment.StartUtc && s.EndUtc == segment.EndUtc))
                    target.UnassignedSegments.Add(segment);
            }

            store.Save(target);
            files = MergeFiles(fromFolder, toFolder, nowLocal, dryRun: false, transaction);

            var written = SettingsStore.ReadFile(settingsPath)
                          ?? throw new IOException("The merged settings.json could not be read back.");
            var again = SettingsMerge.Import(written, SettingsStore.Clone(fromSettings), nowUtc, preferIncoming: true);
            if (again.EntriesAdded + again.EntriesUpdated > 0 || again.JobCodesAdded.Count > 0)
                throw new IOException("The merged settings.json is missing some of your entries.");
            if (!MergeFiles(fromFolder, toFolder, nowLocal, dryRun: true).NothingToAdd)
                throw new IOException("Some day notes or App Track files were not merged.");
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        IReadOnlyList<string> removed = [];
        IReadOnlyList<string> notRemoved = [];
        if (removeOriginals)
            (removed, notRemoved) = DataFolder.RemoveFiles(fromFolder, DataFolder.DataFiles(fromFolder));

        var backup = transaction.BackupNames.FirstOrDefault(n => n.StartsWith(DataFolder.SettingsFileName + ".bak-", StringComparison.OrdinalIgnoreCase));
        return new FolderMergeResult(report, files, backup, removed, notRemoved);
    }

    /// <summary>Copies settings.json to <c>settings.json.bak-…</c> before an import; returns the backup name or null.</summary>
    public static string? BackupSettings(string folder, DateTime nowLocal)
    {
        var path = Path.Combine(folder, DataFolder.SettingsFileName);
        if (!File.Exists(path)) return null;
        var name = DataFolder.BackupName(DataFolder.SettingsFileName, nowLocal);
        IoRetry.Run(() => File.Copy(path, Path.Combine(folder, name), overwrite: true));
        return name;
    }

    /// <summary>
    /// Copies worth offering in Import data…: OneDrive conflict copies (<c>settings-PCNAME.json</c>) and
    /// <c>.bak-…</c> backups in the data folder, the daily snapshots, and the default folder when another one is in use.
    /// </summary>
    public static IReadOnlyList<Candidate> FindCandidates(string currentFolder, string defaultFolder)
    {
        var list = new List<Candidate>();
        if (Directory.Exists(currentFolder))
        {
            foreach (var path in Directory.GetFiles(currentFolder, "settings*.json"))
            {
                var name = Path.GetFileName(path);
                if (ConflictCopy.IsMatch(name) && !string.Equals(name, DataFolder.SettingsFileName, StringComparison.OrdinalIgnoreCase))
                    list.Add(new Candidate(path, "OneDrive conflict copy: " + name, CandidateKind.ConflictCopy, File.GetLastWriteTime(path)));
            }
        }

        foreach (var (path, day) in DailySnapshots.List(currentFolder))
            list.Add(new Candidate(path,
                $"Daily snapshot {day.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)} (data as that day began)",
                CandidateKind.Snapshot, File.GetLastWriteTime(path)));

        if (Directory.Exists(currentFolder))
        {
            foreach (var path in Directory.GetFiles(currentFolder, DataFolder.SettingsFileName + ".bak-*")
                         .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var stamp = Path.GetFileName(path)[(DataFolder.SettingsFileName.Length + 5)..];
                var label = DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? $"Backup from {at.ToString("ddd d MMM yyyy HH:mm", CultureInfo.CurrentCulture)}"
                    : "Backup " + Path.GetFileName(path);
                list.Add(new Candidate(path, label, CandidateKind.Backup, File.GetLastWriteTime(path)));
            }
        }

        if (!DataFolder.SameFolder(currentFolder, defaultFolder) && DataFolder.HasData(defaultFolder))
            list.Add(new Candidate(defaultFolder, "Default data folder (used before the folder was changed)",
                CandidateKind.Folder, File.GetLastWriteTime(Path.Combine(defaultFolder, DataFolder.SettingsFileName))));

        return list;
    }

    private static AppUsageDay? ReadUsage(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AppUsageDay>(IoRetry.ReadAllText(path), UsageReadOptions);
        }
        catch
        {
            return null;
        }
    }

    private static DateOnly? ParseDay(string fileName, string prefix, string format)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= prefix.Length) return null;
        var stamp = fileName[prefix.Length..dot];
        return DateOnly.TryParseExact(stamp, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    private static string Flat(string text) => text.Replace("\r\n", "\n").Trim();
}

using System.IO;
using System.Text.Json;

namespace TimesheetBuddy.Services;

/// <summary>
/// Where settings.json, the day notes and the App Track usage files live. The choice is kept in a pointer file that always
/// stays in the default folder, so it can be found before settings.json is. No WPF here.
/// </summary>
public static class DataFolder
{
    public const string SettingsFileName = "settings.json";
    public const string PointerFileName = "datafolder.json";
    public const string DayNotesPattern = "day-notes-*.txt";
    public const string AppUsagePattern = "app-usage-*.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimesheetBuddy");

    public enum Source
    {
        Default,
        Custom,
        /// <summary>The pointer names a folder that is missing or not writable; the default is used.</summary>
        Fallback
    }

    public sealed record Resolution(string Folder, Source Source, string? UnavailableFolder);

    public enum ChangeKind
    {
        SameFolder,
        Invalid,
        CopyToEmpty,
        TargetHasData
    }

    /// <summary>Default for "Move my data to the new folder": on for an empty target, off when the target has data.</summary>
    public static bool DefaultMove(ChangeKind kind) => kind == ChangeKind.CopyToEmpty;

    private sealed class Pointer
    {
        public string? DataFolder { get; set; }
    }

    public static string PointerPath(string defaultFolder) => Path.Combine(defaultFolder, PointerFileName);

    /// <summary>The saved custom folder, or null for the default (no file, blank, or unreadable).</summary>
    public static string? ReadPointer(string defaultFolder)
    {
        try
        {
            var path = PointerPath(defaultFolder);
            if (!File.Exists(path)) return null;
            var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(path), JsonOptions);
            var folder = pointer?.DataFolder?.Trim();
            if (string.IsNullOrEmpty(folder)) return null;
            return SameFolder(folder, defaultFolder) ? null : folder;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves the choice; the default folder (or null) removes the pointer file.</summary>
    public static void WritePointer(string defaultFolder, string? folder)
    {
        Directory.CreateDirectory(defaultFolder);
        var path = PointerPath(defaultFolder);
        if (string.IsNullOrWhiteSpace(folder) || SameFolder(folder, defaultFolder))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        var json = JsonSerializer.Serialize(new Pointer { DataFolder = Path.GetFullPath(folder) }, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Folder to use this session. A pointer to a missing or read-only folder falls back to the
    /// default without creating anything there; the pointer itself is left alone.
    /// </summary>
    public static Resolution Resolve(string defaultFolder)
    {
        var custom = ReadPointer(defaultFolder);
        if (custom is null)
            return new Resolution(defaultFolder, Source.Default, null);

        return IsWritable(custom)
            ? new Resolution(Path.GetFullPath(custom), Source.Custom, null)
            : new Resolution(defaultFolder, Source.Fallback, custom);
    }

    /// <summary>True when the folder exists and a file can be created and deleted in it.</summary>
    public static bool IsWritable(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
            var probe = Path.Combine(folder, $".timesheetbuddy-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SameFolder(string a, string b)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Normalize(a), Normalize(b), comparison);
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));

    public static bool HasData(string folder) => File.Exists(Path.Combine(folder, SettingsFileName));

    public static ChangeKind Plan(string currentFolder, string targetFolder)
    {
        if (SameFolder(currentFolder, targetFolder)) return ChangeKind.SameFolder;
        if (!IsWritable(targetFolder)) return ChangeKind.Invalid;
        return HasData(targetFolder) ? ChangeKind.TargetHasData : ChangeKind.CopyToEmpty;
    }

    /// <summary>
    /// File names that make up the app's data in a folder: settings.json, then day notes, then
    /// App Track usage files.
    /// </summary>
    public static IReadOnlyList<string> DataFiles(string folder)
    {
        var names = new List<string>();
        if (!Directory.Exists(folder)) return names;
        if (HasData(folder)) names.Add(SettingsFileName);
        names.AddRange(Matching(folder, DayNotesPattern, ".txt"));
        names.AddRange(Matching(folder, AppUsagePattern, ".json"));
        return names;
    }

    // Windows wildcards can also match longer extensions, so the extension is checked again.
    private static IEnumerable<string> Matching(string folder, string pattern, string extension) =>
        Directory.GetFiles(folder, pattern)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => n.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    public static string BackupName(string fileName, DateTime nowLocal) =>
        $"{fileName}.bak-{nowLocal:yyyyMMddHHmmss}";

    /// <summary>
    /// Copies the data files (originals stay put). A target file that would be overwritten is
    /// first renamed to its backup name. Returns the copied names.
    /// </summary>
    public static IReadOnlyList<string> CopyData(string fromFolder, string toFolder, DateTime nowLocal) =>
        Transfer(fromFolder, toFolder, move: false, nowLocal).Copied;

    /// <summary>
    /// What a folder change copies: <see cref="DataFiles"/>, then the daily snapshots in <c>backups\</c> that the
    /// target does not already have (as <c>backups\settings-….json</c> relative names).
    /// </summary>
    public static IReadOnlyList<string> TransferFiles(string fromFolder, string toFolder)
    {
        var names = DataFiles(fromFolder).ToList();
        foreach (var (path, _) in DailySnapshots.List(fromFolder).Reverse())
        {
            var relative = Path.Combine(DailySnapshots.FolderName, Path.GetFileName(path));
            if (!File.Exists(Path.Combine(toFolder, relative)))
                names.Add(relative);
        }

        return names;
    }

    public sealed record TransferResult(
        IReadOnlyList<string> Copied,
        IReadOnlyList<string> BackedUp,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> NotRemoved);

    /// <summary>
    /// Copies <see cref="TransferFiles"/> to <paramref name="toFolder"/> (an existing target file is first renamed
    /// to its backup name), checks every copy against its original (SHA-256), then runs <paramref name="commit"/>
    /// (switch the store, save, write the pointer). If any of that throws, the copies are deleted, the renamed
    /// target files are put back and the exception is rethrown; the originals are never touched.
    /// Only after a successful commit, and only with <paramref name="move"/>, each original that is still
    /// byte-for-byte what was copied is deleted. A file that can't be deleted is left and listed in
    /// <see cref="TransferResult.NotRemoved"/>. crash.log, the pointer and leftover .tmp/.bak/.unreadable files are
    /// never part of it. <paramref name="beforeCopy"/> lets checks inject a failure.
    /// </summary>
    public static TransferResult Transfer(string fromFolder, string toFolder, bool move, DateTime nowLocal,
        Action? commit = null, Action<string>? beforeCopy = null)
    {
        var names = TransferFiles(fromFolder, toFolder);
        var transaction = new FileTransaction(nowLocal);
        var hashes = new Dictionary<string, string>();
        try
        {
            foreach (var name in names)
            {
                beforeCopy?.Invoke(name);
                var source = Path.Combine(fromFolder, name);
                var target = Path.Combine(toFolder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                transaction.MoveAside(target);
                transaction.WillCreate(target);
                IoRetry.Run(() => File.Copy(source, target));
                hashes[name] = Hash(source);
            }

            foreach (var name in names)
            {
                if (Hash(Path.Combine(toFolder, name)) != hashes[name])
                    throw new IOException($"The copy of {name} in {toFolder} does not match the original.");
            }

            commit?.Invoke();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        var removed = new List<string>();
        var notRemoved = new List<string>();
        if (move)
        {
            foreach (var name in names)
            {
                var source = Path.Combine(fromFolder, name);
                try
                {
                    if (Hash(source) != hashes[name])
                    {
                        notRemoved.Add(name);
                        continue;
                    }

                    IoRetry.Run(() => File.Delete(source));
                    removed.Add(name);
                }
                catch
                {
                    notRemoved.Add(name);
                }
            }

            DeleteIfEmpty(DailySnapshots.FolderFor(fromFolder));
        }

        return new TransferResult(names, transaction.BackupNames, removed, notRemoved);
    }

    /// <summary>
    /// Deletes the given data files from <paramref name="folder"/> after their content has been merged elsewhere.
    /// Returns (removed, not removed).
    /// </summary>
    public static (IReadOnlyList<string> Removed, IReadOnlyList<string> NotRemoved) RemoveFiles(string folder,
        IEnumerable<string> names)
    {
        var removed = new List<string>();
        var notRemoved = new List<string>();
        foreach (var name in names)
        {
            try
            {
                IoRetry.Run(() => File.Delete(Path.Combine(folder, name)));
                removed.Add(name);
            }
            catch
            {
                notRemoved.Add(name);
            }
        }

        return (removed, notRemoved);
    }

    public static string Hash(string path)
    {
        using var stream = IoRetry.Run(() => File.OpenRead(path));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch
        {
            // An empty folder left behind is harmless.
        }
    }
}

/// <summary>
/// Records file changes in a target folder so they can be undone: files about to be overwritten are renamed
/// (or copied) to <c>name.bak-yyyyMMddHHmmss</c>, new files are remembered. Rollback deletes what was written and
/// puts the originals back. On success the .bak files stay, as with Replace.
/// </summary>
public sealed class FileTransaction(DateTime nowLocal)
{
    private readonly List<string> _created = [];
    private readonly List<(string Path, string Backup)> _asides = [];

    public IReadOnlyList<string> BackupNames => _asides.Select(a => Path.GetFileName(a.Backup)).ToList();

    /// <summary>Renames an existing file to its backup name so a new one can take its place.</summary>
    public void MoveAside(string path)
    {
        if (!File.Exists(path)) return;
        var backup = BackupPath(path);
        IoRetry.Run(() => File.Move(path, backup, overwrite: true));
        _asides.Add((path, backup));
    }

    /// <summary>Copies an existing file to its backup name; it will be rewritten in place.</summary>
    public void CopyAside(string path)
    {
        if (!File.Exists(path))
        {
            WillCreate(path);
            return;
        }

        var backup = BackupPath(path);
        IoRetry.Run(() => File.Copy(path, backup, overwrite: true));
        _asides.Add((path, backup));
    }

    public void WillCreate(string path)
    {
        if (!_created.Contains(path)) _created.Add(path);
    }

    public void Rollback()
    {
        foreach (var path in _created)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        foreach (var (path, backup) in Enumerable.Reverse(_asides))
        {
            try { File.Move(backup, path, overwrite: true); } catch { }
        }
    }

    private string BackupPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, DataFolder.BackupName(Path.GetFileName(path), nowLocal));
}

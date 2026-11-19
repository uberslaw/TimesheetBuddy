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
    public static IReadOnlyList<string> CopyData(string fromFolder, string toFolder, DateTime nowLocal)
    {
        var copied = new List<string>();
        foreach (var name in DataFiles(fromFolder))
        {
            var target = Path.Combine(toFolder, name);
            if (File.Exists(target))
                File.Move(target, Path.Combine(toFolder, BackupName(name, nowLocal)), overwrite: true);
            File.Copy(Path.Combine(fromFolder, name), target);
            copied.Add(name);
        }

        return copied;
    }
}

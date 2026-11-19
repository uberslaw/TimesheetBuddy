using System.Globalization;
using System.IO;

namespace TimesheetBuddy.Services;

/// <summary>
/// One copy of settings.json per day in <c>backups\settings-yyyy-MM-dd.json</c> under the data folder, taken
/// just before the first save of that day (so it holds the data as the previous session left it). The newest
/// <see cref="Keep"/> days are kept. No WPF here.
/// </summary>
public static class DailySnapshots
{
    public const string FolderName = "backups";
    public const int Keep = 7;
    private const string Prefix = "settings-";
    private const string Extension = ".json";

    public static string FolderFor(string dataFolder) => Path.Combine(dataFolder, FolderName);

    public static string FileName(DateOnly day) =>
        Prefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Extension;

    /// <summary>The day a snapshot file name stands for, or null for any other file.</summary>
    public static DateOnly? DayOf(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            return null;
        var stamp = name[Prefix.Length..^Extension.Length];
        return DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;
    }

    /// <summary>Snapshot names past the newest <paramref name="keep"/> days; other files are never listed.</summary>
    public static IReadOnlyList<string> Expired(IEnumerable<string> fileNames, int keep = Keep) =>
        fileNames
            .Select(n => (Name: Path.GetFileName(n), Day: DayOf(n)))
            .Where(x => x.Day is not null)
            .OrderByDescending(x => x.Day)
            .Skip(Math.Max(0, keep))
            .Select(x => x.Name)
            .ToList();

    /// <summary>Snapshots in a data folder, newest first.</summary>
    public static IReadOnlyList<(string Path, DateOnly Day)> List(string dataFolder)
    {
        var folder = FolderFor(dataFolder);
        if (!Directory.Exists(folder)) return [];
        return Directory.GetFiles(folder, Prefix + "*" + Extension)
            .Select(p => (Path: p, Day: DayOf(p)))
            .Where(x => x.Day is not null)
            .Select(x => (x.Path, x.Day!.Value))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    /// <summary>
    /// Copies settings.json to today's snapshot unless there already is one, then prunes old days.
    /// Returns true when a snapshot was written. Never throws: a failed snapshot must not stop a save.
    /// </summary>
    public static bool TakeIfDue(string dataFolder, DateTime nowLocal, int keep = Keep)
    {
        try
        {
            var settings = Path.Combine(dataFolder, DataFolder.SettingsFileName);
            if (!File.Exists(settings)) return false;

            var folder = FolderFor(dataFolder);
            var target = Path.Combine(folder, FileName(DateOnly.FromDateTime(nowLocal)));
            if (File.Exists(target)) return false;

            Directory.CreateDirectory(folder);
            var temp = target + ".tmp";
            File.Copy(settings, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);

            foreach (var name in Expired(Directory.GetFiles(folder), keep))
            {
                try { File.Delete(Path.Combine(folder, name)); } catch { }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

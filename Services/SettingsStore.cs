using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Reads and writes settings.json in the data folder. Saves are atomic and retried while the file is briefly
/// locked. Before each save the file on disk is checked: if another process (the app on another PC sharing a
/// OneDrive folder) changed it since this one last read or wrote it, its changes are merged in first with
/// <see cref="SettingsMerge.Concurrent"/>. The first save of each day keeps a copy in <c>backups\</c>.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private string _path;
    private string? _baselineJson;
    private (long Length, DateTime WriteUtc)? _stamp;
    private HashSet<Guid> _baselineIds = [];
    private Dictionary<Guid, string> _baselinePrints = [];
    private bool _suspended;

    public SettingsStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, DataFolder.SettingsFileName);
    }

    /// <summary>Raised after a save that merged in changes another process had written.</summary>
    public event Action<SettingsMerge.Report>? ExternalChangesMerged;

    /// <summary>
    /// This install. When set, saves stamp new and changed entries with it, and loads drop another install's
    /// running timer (<see cref="SettingsMerge.DropForeignRunning"/>).
    /// </summary>
    public ClientIdentity? Client { get; set; }

    /// <summary>Local clock for the daily snapshot; replaceable in checks.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public string StoragePath => _path;

    /// <summary>Data folder in use: settings.json and the day notes live here.</summary>
    public string Folder => Path.GetDirectoryName(_path)!;

    /// <summary>The report of the last save that found the file changed on disk, if any.</summary>
    public SettingsMerge.Report? LastMerge { get; private set; }

    /// <summary>
    /// Later saves go to settings.json in <paramref name="folder"/>; nothing is read or written now. The first
    /// save there merges with whatever file is already in place (without deleting anything from it).
    /// </summary>
    public void SwitchFolder(string folder)
    {
        _path = Path.Combine(Path.GetFullPath(folder), DataFolder.SettingsFileName);
        _baselineJson = null;
        _stamp = null;
    }

    /// <summary>Saves become no-ops (the app is about to restart on data written elsewhere).</summary>
    public void Suspend() => _suspended = true;

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            var defaults = CreateDefaults();
            Save(defaults);
            return defaults;
        }

        string json;
        try
        {
            json = IoRetry.ReadAllText(_path);
        }
        catch
        {
            return CreateDefaults();
        }

        var result = ParseWithUpgrades(json);
        if (result.Settings is not { } parsed)
        {
            SetAside(_path);
            return CreateDefaults();
        }

        LastLoad = result;
        if (Client is not null)
            SettingsMerge.DropForeignRunning(parsed, Client.ClientId);
        if (result.Release != "current")
            UpgradeBackup = KeepUpgradeBackup(_path, Clock());

        Adopt(json, parsed);
        return parsed;
    }

    /// <summary>What the last <see cref="Load"/> read: the release that wrote the file and the upgrades applied.</summary>
    public LegacyData.Result? LastLoad { get; private set; }

    /// <summary>Backup name kept when <see cref="Load"/> read a file from an earlier release, before it is rewritten.</summary>
    public string? UpgradeBackup { get; private set; }

    private static string? KeepUpgradeBackup(string path, DateTime nowLocal)
    {
        try
        {
            var name = DataFolder.BackupName(Path.GetFileName(path), nowLocal);
            File.Copy(path, Path.Combine(Path.GetDirectoryName(path)!, name), overwrite: false);
            return name;
        }
        catch
        {
            return null;
        }
    }

    public void Save(AppSettings settings)
    {
        if (_suspended) return;

        var nowLocal = Clock();
        var nowUtc = nowLocal.ToUniversalTime();
        if (Client is not null)
            SettingsMerge.Stamp(settings, _baselinePrints, Client.ClientId, Client.Machine, nowUtc);
        SettingsMerge.RecordRemovals(settings, _baselineIds, nowUtc);
        var report = MergeFromDisk(settings, nowUtc);
        DailySnapshots.TakeIfDue(Folder, nowLocal);

        settings.FormatVersion = LegacyData.CurrentFormat;
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        IoRetry.WriteAllTextAtomic(_path, json);
        Adopt(json, settings);

        if (report is not null)
        {
            LastMerge = report;
            if (report.Changed) ExternalChangesMerged?.Invoke(report);
        }
    }

    /// <summary>
    /// Brings changes another PC saved to settings.json into <paramref name="settings"/> in memory, without
    /// writing (so two open PCs never answer each other's saves). The file read becomes the new baseline; the
    /// next save writes the merged result. Null when the file has not changed since this process last read or
    /// wrote it, or can't be read right now.
    /// </summary>
    public SettingsMerge.Report? Pull(AppSettings settings)
    {
        if (_suspended || !File.Exists(_path) || _stamp == StampOf(_path)) return null;

        string text;
        try
        {
            text = IoRetry.ReadAllText(_path);
        }
        catch
        {
            return null;
        }

        var stamp = StampOf(_path);
        if (text == _baselineJson)
        {
            _stamp = stamp;
            return null;
        }

        var theirs = Parse(text);
        if (theirs is null) return null;

        var baseline = _baselineJson is null ? null : Parse(_baselineJson);
        var report = SettingsMerge.Concurrent(settings, theirs, baseline, Clock().ToUniversalTime());
        Adopt(text, settings);
        _stamp = stamp;
        LastMerge = report;
        if (report.Changed) ExternalChangesMerged?.Invoke(report);
        return report;
    }

    /// <summary>
    /// Reads settings.json text from any version of the app, tolerating missing properties, nulls, numbers as
    /// strings, NaN, comments and trailing commas. A property or list item that can't be read is skipped
    /// instead of failing the whole file. Null when the text is not a JSON object at all.
    /// </summary>
    public static AppSettings? Parse(string json) => ParseWithUpgrades(json).Settings;

    /// <summary>
    /// <see cref="Parse"/>, plus which <see cref="LegacyData"/> upgrades were applied and which release
    /// most likely wrote the text.
    /// </summary>
    public static LegacyData.Result ParseWithUpgrades(string json)
    {
        var result = LegacyData.Read(json, Deserialize);
        if (result.Settings is not null) SettingsMerge.Normalize(result.Settings);
        return result;
    }

    private static AppSettings? Deserialize(JsonObject root)
    {
        try
        {
            return root.Deserialize<AppSettings>(ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return ReadLenient(typeof(AppSettings), root) as AppSettings;
        }
    }

    /// <summary>Reads a settings file from anywhere (import); null when missing or unreadable.</summary>
    public static AppSettings? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(IoRetry.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>A deep copy through JSON.</summary>
    public static AppSettings Clone(AppSettings settings) => Parse(Serialize(settings)) ?? new AppSettings();

    private SettingsMerge.Report? MergeFromDisk(AppSettings settings, DateTime nowUtc)
    {
        if (!File.Exists(_path)) return null;
        if (_baselineJson is not null && _stamp == StampOf(_path)) return null;

        string text;
        try
        {
            text = IoRetry.ReadAllText(_path);
        }
        catch
        {
            return null;
        }

        if (text == _baselineJson) return null;

        var theirs = Parse(text);
        if (theirs is null)
        {
            // A half-synced file can read as garbage for a moment.
            IoRetry.Sleep(TimeSpan.FromMilliseconds(250));
            try { text = IoRetry.ReadAllText(_path); } catch { return null; }
            theirs = Parse(text);
        }

        if (theirs is null)
        {
            SetAside(_path);
            return null;
        }

        var baseline = _baselineJson is null ? null : Parse(_baselineJson);
        return SettingsMerge.Concurrent(settings, theirs, baseline, nowUtc);
    }

    private void Adopt(string json, AppSettings settings)
    {
        _baselineJson = json;
        _stamp = StampOf(_path);
        _baselineIds = (settings.Entries ?? []).Select(e => e.Id).ToHashSet();
        _baselinePrints = new Dictionary<Guid, string>();
        foreach (var e in settings.Entries ?? [])
            _baselinePrints.TryAdd(e.Id, SettingsMerge.Fingerprint(e));
    }

    private static (long, DateTime)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Keeps a copy of a file that would not parse, so the next save can't destroy it.</summary>
    private static void SetAside(string path)
    {
        try
        {
            File.Copy(path, $"{path}.unreadable-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch
        {
            // Nothing more can be done; the save goes ahead.
        }
    }

    private static object? ReadLenient(Type type, JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.Deserialize(type, ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
        }

        if (node is JsonArray array && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var itemType = type.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(type)!;
            foreach (var item in array)
            {
                var value = ReadLenient(itemType, item);
                if (value is not null) list.Add(value);
            }

            return list;
        }

        if (node is JsonObject obj && type.IsClass && type != typeof(string) && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            var instance = Activator.CreateInstance(type)!;
            foreach (var property in type.GetProperties().Where(p => p.CanRead && p.CanWrite))
            {
                var child = obj.FirstOrDefault(p => string.Equals(p.Key, property.Name, StringComparison.OrdinalIgnoreCase)).Value;
                var value = ReadLenient(property.PropertyType, child);
                if (value is not null) property.SetValue(instance, value);
            }

            return instance;
        }

        return null;
    }

    private static AppSettings CreateDefaults()
    {
        var codes = new List<JobCode>
        {
            new() { Code = "ADMIN", Description = "Administration" },
            new() { Code = "DEV", Description = "Software development" },
            new() { Code = "MTG", Description = "Meetings" },
            new() { Code = "SUP", Description = "Support / tickets" }
        };

        return new AppSettings
        {
            JobCodes = codes,
            FavoriteTasks =
            [
                new FavoriteTask { Name = "Stand-up", Category = "Meetings", JobCodeId = codes[2].Id },
                new FavoriteTask { Name = "Email / Slack", Category = "Admin", JobCodeId = codes[0].Id },
                new FavoriteTask { Name = "Feature work", Category = "Development", JobCodeId = codes[1].Id }
            ]
        };
    }
}

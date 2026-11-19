using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private string _path;

    public SettingsStore(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
    }

    public string StoragePath => _path;

    /// <summary>Data folder in use: settings.json and the day notes live here.</summary>
    public string Folder => Path.GetDirectoryName(_path)!;

    /// <summary>Later saves go to settings.json in <paramref name="folder"/>; nothing is read or written now.</summary>
    public void SwitchFolder(string folder)
    {
        _path = Path.Combine(Path.GetFullPath(folder), "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            var defaults = CreateDefaults();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefaults();
        }
        catch
        {
            return CreateDefaults();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_path, json);
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

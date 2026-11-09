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

    private readonly string _path;

    public SettingsStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TimesheetBuddy");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.json");
    }

    public string StoragePath => _path;

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

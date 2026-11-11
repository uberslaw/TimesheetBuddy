namespace TimesheetBuddy.Models;

public sealed class AppSettings
{
    public bool AlwaysOnTop { get; set; } = true;
    public int RecheckHours { get; set; }
    public int RecheckMinutes { get; set; } = 30;
    public int FifteenMinuteIntervalMinutes { get; set; } = 15;
    public bool FifteenMinutePromptEnabled { get; set; } = true;
    public bool RecheckPromptEnabled { get; set; } = true;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public List<JobCode> JobCodes { get; set; } = [];
    public List<FavoriteTask> FavoriteTasks { get; set; } = [];
    public List<string> Categories { get; set; } =
    [
        "General",
        "Development",
        "Meetings",
        "Support",
        "Admin"
    ];
    public List<TimeEntry> Entries { get; set; } = [];
    public ActiveSession? ActiveSession { get; set; }

    /// <summary>
    /// Ordered job-code ids shown in the job-code view.
    /// Null means the view has not been customized and should list active codes.
    /// </summary>
    public List<Guid>? JobCodeViewIds { get; set; }
}

public sealed class ActiveSession
{
    public Guid EntryId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public Guid? JobCodeId { get; set; }
    public string JobCodeText { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
}

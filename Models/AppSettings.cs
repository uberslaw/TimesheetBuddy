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
    public double? WindowHeight { get; set; }
    public bool ShowTimerWidget { get; set; } = true;
    public bool ShowTimerWidgetWhenCovered { get; set; } = true;
    public bool ShowTimerWidgetOnOtherMonitor { get; set; } = true;
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }

    /// <summary>App Track: log time per foreground program (name only) to app-usage-*.json. Off by default.</summary>
    public bool AppTrackEnabled { get; set; }

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

    /// <summary>Start of the running "No Task Selected" timer; null when a task runs or nothing was stopped.</summary>
    public DateTime? NoTaskStartedUtc { get; set; }

    /// <summary>Finished "No Task Selected" time (1 minute or more each) not yet assigned to a task.</summary>
    public List<UnassignedSegment> UnassignedSegments { get; set; } = [];

    /// <summary>
    /// Ordered job-code ids shown in the job-code view.
    /// Null means the view has not been customized and should list active codes.
    /// </summary>
    public List<Guid>? JobCodeViewIds { get; set; }

    /// <summary>
    /// Ids of time entries removed from <see cref="Entries"/> (purged soft deletes, trimmed segments), so a
    /// merge with another PC's copy or an older backup does not bring them back. Kept for about 400 days.
    /// </summary>
    public List<RemovedEntry> RemovedEntries { get; set; } = [];
}

public sealed class RemovedEntry
{
    public Guid Id { get; set; }
    public DateTime RemovedUtc { get; set; }
}

public sealed class ActiveSession
{
    public Guid EntryId { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public Guid? JobCodeId { get; set; }
    public string JobCodeText { get; set; } = string.Empty;
    public string Ticket { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
}

/// <summary>
/// A finished "No Task Selected" run of at least a minute, waiting to be assigned to a task.
/// Kept apart from <see cref="AppSettings.Entries"/> so it never counts as logged work.
/// </summary>
public sealed class UnassignedSegment
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
}

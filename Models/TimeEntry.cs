using System.Text.Json.Serialization;

namespace TimesheetBuddy.Models;

public sealed class TimeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public string TaskName { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public Guid? JobCodeId { get; set; }
    public string JobCodeText { get; set; } = string.Empty;
    public string Ticket { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int ManualMinutes { get; set; }
    public string Source { get; set; } = "timer"; // timer | fifteen | manual | add-time
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtLocal { get; set; }

    /// <summary>Install that created the entry (client id + machine name); empty in data from earlier releases.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CreatedByClientId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedByMachine { get; set; }

    /// <summary>Install that last changed the entry, and when (UTC). Stamped on save.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ModifiedByClientId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedByMachine { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedAtUtc { get; set; }

    public TimeSpan Duration
    {
        get
        {
            if (ManualMinutes > 0)
                return TimeSpan.FromMinutes(ManualMinutes);

            var end = EndUtc ?? DateTime.UtcNow;
            return end - StartUtc;
        }
    }

    public string DurationDisplay
    {
        get
        {
            var d = Duration;
            return d.TotalHours >= 1
                ? $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}"
                : $"{d.Minutes:00}:{d.Seconds:00}";
        }
    }
}

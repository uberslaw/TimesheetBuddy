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
    public string Notes { get; set; } = string.Empty;
    public int ManualMinutes { get; set; }
    public string Source { get; set; } = "timer"; // timer | fifteen | manual

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

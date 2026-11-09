namespace TimesheetBuddy.Models;

public sealed class JobCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public string Display => string.IsNullOrWhiteSpace(Description)
        ? Code
        : $"{Code} — {Description}";

    public override string ToString() => Display;
}

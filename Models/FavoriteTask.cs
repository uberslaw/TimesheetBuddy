namespace TimesheetBuddy.Models;

public sealed class FavoriteTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public Guid? JobCodeId { get; set; }

    public override string ToString() => Name;
}

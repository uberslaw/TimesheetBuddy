using System.IO;
using System.Text.Json;

namespace TimesheetBuddy.Services;

/// <summary>
/// This install's stable id plus the machine name for display. Kept in <c>client.json</c> in
/// %LocalAppData%\TimesheetBuddy (never the shared data folder), so two PCs syncing one folder always differ.
/// A file copied to another PC with the profile (different machine name) gets a new id. No WPF here.
/// </summary>
public sealed record ClientIdentity(Guid ClientId, string Machine)
{
    public const string FileName = "client.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class Stored
    {
        public Guid ClientId { get; set; }
        public string? Machine { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Machine name safe for a file name: letters, digits, '-' and '_' only.</summary>
    public string SafeMachine
    {
        get
        {
            var chars = (Machine ?? string.Empty).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
            var text = new string(chars).Trim('_');
            return text.Length == 0 ? "PC" : text;
        }
    }

    public static ClientIdentity LoadOrCreate(string localFolder, string? machine = null)
    {
        machine = string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine.Trim();
        var path = Path.Combine(localFolder, FileName);
        try
        {
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), JsonOptions);
                if (stored is not null && stored.ClientId != Guid.Empty &&
                    string.Equals(stored.Machine, machine, StringComparison.OrdinalIgnoreCase))
                    return new ClientIdentity(stored.ClientId, machine);
            }
        }
        catch
        {
            // Unreadable: a new id is written below.
        }

        var created = new ClientIdentity(Guid.NewGuid(), machine);
        try
        {
            Directory.CreateDirectory(localFolder);
            var json = JsonSerializer.Serialize(
                new Stored { ClientId = created.ClientId, Machine = machine, CreatedUtc = DateTime.UtcNow }, JsonOptions);
            IoRetry.WriteAllTextAtomic(path, json);
        }
        catch
        {
            // The id still works for this session; the next start tries to save it again.
        }

        return created;
    }
}

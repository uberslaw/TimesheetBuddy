using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimesheetBuddy.Services;

/// <summary>One change: when, which install (id + machine name), what kind, which entry, and a short summary.</summary>
public sealed record JournalLine(
    [property: JsonPropertyName("t")] DateTime TimeUtc,
    [property: JsonPropertyName("client")] Guid ClientId,
    [property: JsonPropertyName("machine")] string Machine,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("entry")] Guid? EntryId,
    [property: JsonPropertyName("summary")] string Summary);

/// <summary>
/// Append-only change log, one file per install: <c>journal\&lt;machine&gt;-&lt;clientid&gt;.jsonl</c> in the data folder,
/// one JSON object per line. Only its own install ever writes a file, so OneDrive never sees two writers.
/// Lines older than <see cref="Retention"/> are pruned from the own file at start-up; files of installs that have
/// not written for that long are deleted. Writing never throws. No WPF here.
/// </summary>
public sealed class ChangeJournal(Func<string> dataFolder, ClientIdentity client, Func<DateTime>? utcNow = null)
{
    public const string FolderName = "journal";
    public const string Extension = ".jsonl";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    public static class Actions
    {
        public const string Start = "start";
        public const string Stop = "stop";
        public const string Add = "add";
        public const string Modify = "modify";
        public const string Move = "move";
        public const string Delete = "delete";
        public const string Restore = "restore";
        public const string Import = "import";
        public const string Folder = "folder change";
        public const string Overlap = "overlap";
    }

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);

    public ClientIdentity Client { get; } = client;

    public static string FileName(ClientIdentity client) => $"{client.SafeMachine}-{client.ClientId:N}{Extension}";

    public static string FolderFor(string dataFolder) => Path.Combine(dataFolder, FolderName);

    public string OwnPath => Path.Combine(FolderFor(dataFolder()), FileName(Client));

    /// <summary>The last write failure, for diagnostics; null after a successful write.</summary>
    public string? LastError { get; private set; }

    public void Append(string action, string summary, Guid? entryId = null)
    {
        try
        {
            var line = new JournalLine(_utcNow(), Client.ClientId, Client.Machine, action, entryId, Shorten(summary));
            var path = OwnPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = JsonSerializer.Serialize(line, LineOptions) + "\n";
            IoRetry.Run(() => File.AppendAllText(path, text, new UTF8Encoding(false)));
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    /// <summary>Rewrites the own file without lines older than <see cref="Retention"/>. Returns the number removed.</summary>
    public int PruneOwn()
    {
        try
        {
            var path = OwnPath;
            if (!File.Exists(path)) return 0;
            var cutoff = _utcNow() - Retention;
            var lines = IoRetry.Run(() => File.ReadAllLines(path));
            var kept = lines.Where(l => Parse(l) is not { } parsed || parsed.TimeUtc >= cutoff).ToList();
            if (kept.Count == lines.Length) return 0;
            IoRetry.WriteAllTextAtomic(path, kept.Count == 0 ? string.Empty : string.Join("\n", kept) + "\n");
            return lines.Length - kept.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Deletes other installs' journal files untouched for longer than <see cref="Retention"/>.</summary>
    public int PruneAbandoned()
    {
        var removed = 0;
        try
        {
            var folder = FolderFor(dataFolder());
            if (!Directory.Exists(folder)) return 0;
            var cutoff = _utcNow() - Retention;
            var own = FileName(Client);
            foreach (var path in Directory.GetFiles(folder, "*" + Extension))
            {
                if (string.Equals(Path.GetFileName(path), own, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch
                {
                    // Another PC may be syncing it; try again next start.
                }
            }
        }
        catch
        {
            // Nothing to prune.
        }

        return removed;
    }

    /// <summary>Newest first: up to <paramref name="max"/> lines from every install's file since <paramref name="sinceUtc"/>.</summary>
    public static IReadOnlyList<JournalLine> ReadRecent(string dataFolder, DateTime sinceUtc, int max = 1000)
    {
        var all = new List<JournalLine>();
        var folder = FolderFor(dataFolder);
        if (!Directory.Exists(folder)) return all;
        foreach (var path in Directory.GetFiles(folder, "*" + Extension))
        {
            string[] lines;
            try
            {
                lines = IoRetry.Run(() => File.ReadAllLines(path));
            }
            catch
            {
                continue;
            }

            foreach (var text in lines)
            {
                if (Parse(text) is { } line && line.TimeUtc >= sinceUtc)
                    all.Add(line);
            }
        }

        return all.OrderByDescending(l => l.TimeUtc).Take(max).ToList();
    }

    /// <summary>A journal line, or null for a blank, half-synced or foreign line.</summary>
    public static JournalLine? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var line = JsonSerializer.Deserialize<JournalLine>(text, LineOptions);
            if (line is null || line.TimeUtc == default) return null;
            return line with
            {
                TimeUtc = DateTime.SpecifyKind(line.TimeUtc.Kind == DateTimeKind.Local ? line.TimeUtc.ToUniversalTime() : line.TimeUtc, DateTimeKind.Utc),
                Machine = line.Machine ?? string.Empty,
                Action = line.Action ?? string.Empty,
                Summary = line.Summary ?? string.Empty
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"Report · 04 — CS OFFICE SUPPORT" style label for an entry-like set of fields.</summary>
    public static string Describe(string? taskName, string? jobCodeText, string? ticket = null)
    {
        var task = TicketRules.Label(ticket, taskName);
        var code = (jobCodeText ?? string.Empty).Trim();
        if (task.Length == 0) task = "Untitled task";
        return code.Length == 0 ? task : $"{task} ({code})";
    }

    public static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes:00}m")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)Math.Round(span.TotalMinutes))}m");

    private static string Shorten(string? text)
    {
        var value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 300 ? value : value[..299] + "…";
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// One upgrade from a data shape an earlier release wrote. A reader can fix the raw text (before it is
/// JSON), the JSON tree (renames, odd values) and the typed settings (after reading). Each hook returns true
/// when it changed something. No WPF here.
/// </summary>
public interface ILegacyReader
{
    /// <summary>Short user-facing description, shown in the Import data… preview.</summary>
    string Description { get; }

    string FixText(string text) => text;

    bool FixJson(JsonObject root) => false;

    bool FixModel(AppSettings settings) => false;
}

/// <summary>
/// Reads settings.json files written by any release of the app and upgrades them to the current shape. Used
/// by every load and by Import data…. Shapes found in the history (see handover.md, Data):
/// <list type="bullet">
/// <item>3.0 (first release): <c>windowLeft</c>/<c>windowTop</c> were non-nullable doubles defaulting to NaN,
/// stored as <c>"NaN"</c> or bare <c>NaN</c>.</item>
/// <item>3.0: entries' <c>jobCodeText</c> held the full <c>"CODE — Description"</c> label.</item>
/// <item>3.0: a running timer was saved open (active session + entry without an end) and resumed.</item>
/// <item>Fields added later (ticket, soft delete, window height, job-code view, widget, No Task Selected,
/// App Track, tombstones, client stamps) are missing; they read as their defaults.</item>
/// </list>
/// The file name (<c>settings.json</c>), its folder (<c>%LocalAppData%\TimesheetBuddy</c> until the data
/// folder became configurable) and <c>day-notes-yyyyMMdd.txt</c> never changed.
/// </summary>
public static class LegacyData
{
    /// <summary>Written on every save; files without it come from a release before this one.</summary>
    public const int CurrentFormat = 2;

    public static IReadOnlyList<ILegacyReader> Readers { get; } =
    [
        new BareNonFiniteNumbers(),
        new NonFinitePositions(),
        new OpenManualEntries(),
        new OrphanActiveSession(),
        new FullJobCodeLabels()
    ];

    public sealed record Result(AppSettings? Settings, IReadOnlyList<string> Upgrades, string Release);

    /// <summary>Parses and upgrades settings text; <see cref="Result.Settings"/> is null when it is not a JSON object.</summary>
    public static Result Read(string text, Func<JsonObject, AppSettings?> deserialize)
    {
        var applied = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new Result(null, applied, "unknown");

        foreach (var reader in Readers)
        {
            var fixedText = reader.FixText(text);
            if (fixedText == text) continue;
            text = fixedText;
            applied.Add(reader.Description);
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) as JsonObject;
        }
        catch (JsonException)
        {
            return new Result(null, applied, "unknown");
        }

        if (root is null) return new Result(null, applied, "unknown");
        var release = Release(root);

        foreach (var reader in Readers)
        {
            if (reader.FixJson(root) && !applied.Contains(reader.Description))
                applied.Add(reader.Description);
        }

        var settings = deserialize(root);
        if (settings is null) return new Result(null, applied, release);

        foreach (var reader in Readers)
        {
            if (reader.FixModel(settings) && !applied.Contains(reader.Description))
                applied.Add(reader.Description);
        }

        settings.FormatVersion = CurrentFormat;
        return new Result(settings, applied, release);
    }

    /// <summary>Which release most likely wrote the file, from the fields it has.</summary>
    public static string Release(JsonObject root)
    {
        if (Get(root, "formatVersion") is JsonValue v && v.TryGetValue<int>(out var format) && format >= CurrentFormat)
            return "current";
        if (Get(root, "noTaskStartedUtc") is not null || Get(root, "unassignedSegments") is not null ||
            Get(root, "showTimerWidget") is not null || Get(root, "appTrackEnabled") is not null)
            return "3.x (Nov 2026 updates)";
        if (Get(root, "windowHeight") is not null || Get(root, "jobCodeViewIds") is not null)
            return "3.0 with early updates";
        return "3.0";
    }

    internal static JsonNode? Get(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Bare NaN / Infinity tokens are not JSON; they become null.</summary>
    private sealed class BareNonFiniteNumbers : ILegacyReader
    {
        private static readonly Regex Token = new(@"(?<=[:\[,]\s*)-?(NaN|Infinity)(?=\s*[,\]}])", RegexOptions.Compiled);

        public string Description => "Bare NaN/Infinity numbers read as empty";

        public string FixText(string text) => Token.Replace(text, "null");
    }

    /// <summary>3.0 stored an unset window position as NaN; it is now empty (null).</summary>
    private sealed class NonFinitePositions : ILegacyReader
    {
        private static readonly string[] Names = ["windowLeft", "windowTop", "windowHeight", "widgetLeft", "widgetTop"];

        public string Description => "3.0 NaN window position cleared (window is placed fresh)";

        public bool FixJson(JsonObject root)
        {
            var changed = false;
            foreach (var name in Names)
            {
                var key = root.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Key;
                if (key is null || root[key] is not JsonValue value) continue;
                if (IsNonFinite(value))
                {
                    root[key] = null;
                    changed = true;
                }
            }

            return changed;
        }

        private static bool IsNonFinite(JsonValue value)
        {
            if (value.TryGetValue<double>(out var d)) return !double.IsFinite(d);
            if (value.TryGetValue<string>(out var s))
                return double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? !double.IsFinite(parsed)
                    : true;
            return false;
        }
    }

    /// <summary>A manual-minutes entry without an end would count as running; it ends after its minutes.</summary>
    private sealed class OpenManualEntries : ILegacyReader
    {
        public string Description => "Manual entries without an end time closed after their minutes";

        public bool FixModel(AppSettings settings)
        {
            var changed = false;
            foreach (var e in settings.Entries ?? [])
            {
                if (e is null || e.EndUtc is not null || e.ManualMinutes <= 0 || e.StartUtc == default) continue;
                e.EndUtc = e.StartUtc.AddMinutes(e.ManualMinutes);
                changed = true;
            }

            return changed;
        }
    }

    /// <summary>An active session whose entry is missing gets its open entry back, so its time is not lost.</summary>
    private sealed class OrphanActiveSession : ILegacyReader
    {
        public string Description => "Running timer without its entry restored";

        public bool FixModel(AppSettings settings)
        {
            var session = settings.ActiveSession;
            if (session is null || session.StartedUtc == default) return false;
            settings.Entries ??= [];
            if (session.EntryId != Guid.Empty && settings.Entries.Any(e => e is not null && e.Id == session.EntryId))
                return false;

            if (session.EntryId == Guid.Empty) session.EntryId = Guid.NewGuid();
            settings.Entries.Add(new TimeEntry
            {
                Id = session.EntryId,
                StartUtc = session.StartedUtc,
                TaskName = session.TaskName ?? string.Empty,
                Category = string.IsNullOrWhiteSpace(session.Category) ? "General" : session.Category,
                JobCodeId = session.JobCodeId,
                JobCodeText = session.JobCodeText ?? string.Empty,
                Ticket = session.Ticket ?? string.Empty,
                Source = "timer"
            });
            return true;
        }
    }

    /// <summary>3.0 labels ("089082-04 — CS OFFICE SUPPORT - Admin") become today's short label for known codes.</summary>
    private sealed class FullJobCodeLabels : ILegacyReader
    {
        public string Description => "3.0 long job-code labels shortened to the current label";

        public bool FixModel(AppSettings settings)
        {
            var codes = (settings.JobCodes ?? []).Where(c => c is not null)
                .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            var changed = false;
            foreach (var e in settings.Entries ?? [])
            {
                if (e?.JobCodeId is not Guid id || !codes.TryGetValue(id, out var code)) continue;
                if (IsLongLabel(e.JobCodeText, code))
                {
                    e.JobCodeText = code.Display;
                    changed = true;
                }
            }

            if (settings.ActiveSession is { JobCodeId: Guid sid } session && codes.TryGetValue(sid, out var active) &&
                IsLongLabel(session.JobCodeText, active))
            {
                session.JobCodeText = active.Display;
                changed = true;
            }

            return changed;
        }

        private static bool IsLongLabel(string? text, JobCode code)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(code.Description)) return false;
            var trimmed = text.Trim();
            return trimmed != code.Display &&
                   string.Equals(trimmed, $"{code.Code.Trim()} — {code.Description.Trim()}", StringComparison.Ordinal);
        }
    }
}

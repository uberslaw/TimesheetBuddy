using TimesheetBuddy.Models;
using TimesheetBuddy.Services;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (condition)
    {
        Console.WriteLine("PASS  " + message);
        return;
    }

    failures.Add(message);
    Console.WriteLine("FAIL  " + message);
}

// A settings.json exactly as the 3.0 release (commit "Import Timesheet Buddy 3.0") wrote it: camelCase,
// windowLeft/windowTop as NaN, long job-code labels, a running session saved open, none of the later fields.
const string codeId = "6f1c4c55-8a39-4e55-9a49-0c1e8c2a0001";
const string runningId = "0b7f0a77-1111-4c3e-9d0e-5b8b8a000001";
var v30 = $$"""
{
  "alwaysOnTop": true,
  "recheckHours": 0,
  "recheckMinutes": 30,
  "fifteenMinuteIntervalMinutes": 15,
  "fifteenMinutePromptEnabled": true,
  "recheckPromptEnabled": true,
  "windowLeft": "NaN",
  "windowTop": NaN,
  "jobCodes": [
    { "id": "{{codeId}}", "code": "089082-04", "description": "CS OFFICE SUPPORT - Admin tasks", "isActive": true }
  ],
  "favoriteTasks": [
    { "id": "7a0e0f00-2222-4c3e-9d0e-5b8b8a000002", "name": "Stand-up", "category": "Meetings", "jobCodeId": "{{codeId}}" }
  ],
  "categories": [ "General", "Meetings" ],
  "entries": [
    {
      "id": "1d2e3f40-3333-4c3e-9d0e-5b8b8a000003",
      "startUtc": "2026-11-05T22:00:00Z",
      "endUtc": "2026-11-05T23:30:00Z",
      "taskName": "Inbox",
      "category": "General",
      "jobCodeId": "{{codeId}}",
      "jobCodeText": "089082-04 — CS OFFICE SUPPORT - Admin tasks",
      "notes": "",
      "manualMinutes": 0,
      "source": "timer"
    },
    {
      "id": "1d2e3f40-4444-4c3e-9d0e-5b8b8a000004",
      "startUtc": "2026-11-06T01:00:00Z",
      "endUtc": "2026-11-06T01:20:00Z",
      "taskName": "Call",
      "category": "General",
      "jobCodeId": null,
      "jobCodeText": "",
      "notes": "phone",
      "manualMinutes": 20,
      "source": "manual"
    },
    {
      "id": "{{runningId}}",
      "startUtc": "2026-11-06T02:00:00Z",
      "endUtc": null,
      "taskName": "Report",
      "category": "General",
      "jobCodeId": "{{codeId}}",
      "jobCodeText": "089082-04 — CS OFFICE SUPPORT - Admin tasks",
      "notes": "",
      "manualMinutes": 0,
      "source": "timer"
    }
  ],
  "activeSession": {
    "entryId": "{{runningId}}",
    "taskName": "Report",
    "category": "General",
    "jobCodeId": "{{codeId}}",
    "jobCodeText": "089082-04 — CS OFFICE SUPPORT - Admin tasks",
    "startedUtc": "2026-11-06T02:00:00Z"
  }
}
""";

var read = SettingsStore.ParseWithUpgrades(v30);
var s = read.Settings;
Check(s is not null, "3.0 file with NaN window position parses");
Check(read.Release == "3.0", "release detected as 3.0 (got " + read.Release + ")");
Check(s!.WindowLeft is null && s.WindowTop is null, "NaN window position (string and bare) reads as unset");
Check(read.Upgrades.Any(u => u.Contains("NaN")), "the NaN upgrade is reported");
Check(s.Entries.Count == 3, "all three entries load");
Check(s.Entries.All(e => e.Ticket == "" && !e.IsDeleted && e.DeletedAtLocal is null), "missing ticket / soft-delete fields read as defaults");
Check(s.ShowTimerWidget && s.ShowTimerWidgetWhenCovered && s.ShowTimerWidgetOnOtherMonitor && !s.AppTrackEnabled,
    "missing widget / App Track fields read as their defaults");
Check(s.UnassignedSegments.Count == 0 && s.NoTaskStartedUtc is null && s.RemovedEntries.Count == 0 && s.JobCodeViewIds is null,
    "missing No Task Selected, tombstone and view fields are empty");
var code = s.JobCodes.Single();
Check(s.Entries[0].JobCodeText == code.Display && code.Display == "04 — CS OFFICE SUPPORT",
    "3.0 long job-code label becomes the current label (got " + s.Entries[0].JobCodeText + ")");
Check(s.ActiveSession?.JobCodeText == code.Display, "the running session's label is shortened too");
Check(s.ActiveSession?.EntryId == Guid.Parse(runningId) && s.Entries[2].EndUtc is null,
    "3.0 running session stays open (it resumes, as 3.0 did)");
Check(s.FormatVersion == LegacyData.CurrentFormat, "upgraded settings carry the current format version");

// Variants seen only defensively.
var orphan = SettingsStore.ParseWithUpgrades("""
{ "activeSession": { "entryId": "00000000-0000-0000-0000-000000000000", "taskName": "Lost", "startedUtc": "2026-11-06T02:00:00Z" },
  "entries": [ { "startUtc": "2026-11-06T01:00:00Z", "manualMinutes": 15, "taskName": "Open manual" } ] }
""");
Check(orphan.Settings!.Entries.Any(e => e.TaskName == "Lost" && e.Id == orphan.Settings.ActiveSession!.EntryId && e.EndUtc is null),
    "a running session without its entry gets the entry back");
Check(orphan.Settings.Entries.Single(e => e.TaskName == "Open manual").EndUtc == new DateTime(2026, 11, 6, 1, 15, 0, DateTimeKind.Utc),
    "a manual entry without an end ends after its minutes");
Check(SettingsStore.ParseWithUpgrades("[1,2]").Settings is null, "a JSON array is not a settings file");
Check(SettingsStore.ParseWithUpgrades("{ \"WindowLeft\": 12.5, \"Entries\": [] }").Settings?.WindowLeft == 12.5,
    "PascalCase property names read too");

var root = Path.Combine(Path.GetTempPath(), "tsb-legacy-" + Guid.NewGuid().ToString("N"));
try
{
    // Normal load upgrades in place and keeps the original as a .bak.
    var data = Path.Combine(root, "data");
    Directory.CreateDirectory(data);
    File.WriteAllText(Path.Combine(data, "settings.json"), v30);
    File.WriteAllText(Path.Combine(data, "day-notes-20261106.txt"), "3.0 note");
    var store = new SettingsStore(data) { Clock = () => new DateTime(2026, 11, 19, 9, 0, 0) };
    var loaded = store.Load();
    Check(store.LastLoad?.Release == "3.0" && store.UpgradeBackup is not null &&
          File.Exists(Path.Combine(data, store.UpgradeBackup)), "loading a 3.0 file keeps a .bak of the original");
    Check(File.ReadAllText(Path.Combine(data, store.UpgradeBackup!)) == v30, "the .bak is the untouched 3.0 file");
    store.Save(loaded);
    var saved = File.ReadAllText(Path.Combine(data, "settings.json"));
    Check(saved.Contains("\"formatVersion\": 2") && !saved.Contains("NaN"), "the next save writes the current format, no NaN");
    var again = new SettingsStore(data);
    again.Load();
    Check(again.LastLoad?.Release == "current" && again.UpgradeBackup is null, "a current file is not backed up again");

    // Import data… from a 3.0 data folder into current data.
    var current = Path.Combine(root, "current");
    Directory.CreateDirectory(current);
    var currentStore = new SettingsStore(current);
    var mine = currentStore.Load();
    var old = Path.Combine(root, "old-release");
    Directory.CreateDirectory(old);
    File.WriteAllText(Path.Combine(old, "settings.json"), v30);
    File.WriteAllText(Path.Combine(old, "day-notes-20261106.txt"), "3.0 note");
    var source = DataImport.Open(old, current);
    Check(source.Error is null && source.Release == "3.0" && source.Upgrades.Count > 0, "Import data… opens a 3.0 folder and names the release");
    var preview = DataImport.PreviewImport(mine, source, current, new DateTime(2026, 11, 19, 9, 0, 0));
    Check(preview.Settings!.EntriesAdded == 2 && preview.Settings.SkippedRunning == 1,
        "preview: two finished 3.0 entries to add, the open running one skipped");
    Check(preview.Settings.JobCodesAdded.Count == 1 && preview.Files.NotesNew.Count == 1, "preview: the job code and the day note are new");
    Check(mine.Entries.Count == 0, "preview changes nothing");
    var report = SettingsMerge.Import(mine, source.Settings!, DateTime.UtcNow);
    Check(report.EntriesAdded == 2 && mine.Entries.All(e => e.JobCodeText != "089082-04 — CS OFFICE SUPPORT - Admin tasks"),
        "import adds the entries with upgraded labels");
    var twice = SettingsMerge.Import(mine, SettingsStore.Parse(v30)!, DateTime.UtcNow);
    Check(twice.NothingToAdd, "importing the same 3.0 file again adds nothing");

    // A single settings.json file (e.g. copied from the old PC) imports too.
    var file = Path.Combine(root, "settings from old pc.json");
    File.WriteAllText(file, v30);
    var fileSource = DataImport.Open(file, current);
    Check(fileSource.Error is null && fileSource.Settings?.Entries.Count == 3 && fileSource.FilesFolder is null,
        "a loose 3.0 settings file opens for import");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"{failures.Count} legacy data check(s) failed.");
    return 1;
}

Console.WriteLine("All legacy data checks passed.");
return 0;

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

var root = Path.Combine(Path.GetTempPath(), "tsb-datafolder-" + Guid.NewGuid().ToString("N"));
var defaultFolder = Path.Combine(root, "default");
var custom = Path.Combine(root, "custom");
Directory.CreateDirectory(defaultFolder);
Directory.CreateDirectory(custom);

try
{
    // Resolution.
    var res = DataFolder.Resolve(defaultFolder);
    Check(res.Source == DataFolder.Source.Default && res.Folder == defaultFolder && res.UnavailableFolder is null,
        "no pointer file uses the default folder");

    File.WriteAllText(DataFolder.PointerPath(defaultFolder), "{ \"dataFolder\": \"  \" }");
    Check(DataFolder.Resolve(defaultFolder).Source == DataFolder.Source.Default, "blank pointer uses the default folder");

    File.WriteAllText(DataFolder.PointerPath(defaultFolder), "not json");
    Check(DataFolder.Resolve(defaultFolder).Source == DataFolder.Source.Default, "unreadable pointer uses the default folder");

    DataFolder.WritePointer(defaultFolder, custom);
    var pointerJson = File.ReadAllText(DataFolder.PointerPath(defaultFolder));
    Check(pointerJson.Contains("\"dataFolder\""), "pointer file uses the dataFolder key");
    res = DataFolder.Resolve(defaultFolder);
    Check(res.Source == DataFolder.Source.Custom && DataFolder.SameFolder(res.Folder, custom),
        "a valid pointer uses the custom folder");

    var missing = Path.Combine(root, "unplugged-drive", "TimesheetBuddy");
    DataFolder.WritePointer(defaultFolder, missing);
    res = DataFolder.Resolve(defaultFolder);
    Check(res.Source == DataFolder.Source.Fallback && res.Folder == defaultFolder,
        "a missing custom folder falls back to the default");
    Check(res.UnavailableFolder is not null && DataFolder.SameFolder(res.UnavailableFolder, missing),
        "the fallback names the missing folder");
    Check(!Directory.Exists(missing), "the missing folder is not created");
    Check(DataFolder.ReadPointer(defaultFolder) is { } kept && DataFolder.SameFolder(kept, missing),
        "the pointer is left unchanged after a fallback");

    DataFolder.WritePointer(defaultFolder, defaultFolder);
    Check(!File.Exists(DataFolder.PointerPath(defaultFolder)), "choosing the default removes the pointer file");
    DataFolder.WritePointer(defaultFolder, null);
    Check(!File.Exists(DataFolder.PointerPath(defaultFolder)), "clearing an absent pointer is fine");

    File.WriteAllText(DataFolder.PointerPath(defaultFolder), "{ \"dataFolder\": " +
        System.Text.Json.JsonSerializer.Serialize(defaultFolder + Path.DirectorySeparatorChar) + " }");
    Check(DataFolder.ReadPointer(defaultFolder) is null, "a pointer to the default folder counts as default");

    // Writability and folder comparison.
    Check(DataFolder.IsWritable(custom), "an existing folder is writable");
    Check(!DataFolder.IsWritable(missing), "a missing folder is not writable");
    Check(!DataFolder.IsWritable("  "), "a blank path is not writable");
    Check(Directory.GetFiles(custom).Length == 0, "the write probe leaves nothing behind");
    if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
    {
        var readOnly = Path.Combine(root, "read-only");
        Directory.CreateDirectory(readOnly);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Check(!DataFolder.IsWritable(readOnly), "a read-only folder is not writable");
        DataFolder.WritePointer(defaultFolder, readOnly);
        Check(DataFolder.Resolve(defaultFolder).Source == DataFolder.Source.Fallback, "a read-only custom folder falls back");
        DataFolder.WritePointer(defaultFolder, null);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    Check(DataFolder.SameFolder(custom, custom + Path.DirectorySeparatorChar), "trailing separator is the same folder");
    Check(DataFolder.SameFolder(custom, Path.Combine(custom, "..", "custom")), "relative segments are the same folder");
    Check(!DataFolder.SameFolder(custom, defaultFolder), "different folders differ");

    // Plans.
    var current = Path.Combine(root, "current");
    var empty = Path.Combine(root, "empty");
    var used = Path.Combine(root, "used");
    Directory.CreateDirectory(current);
    Directory.CreateDirectory(empty);
    Directory.CreateDirectory(used);
    File.WriteAllText(Path.Combine(current, "settings.json"), "{ \"current\": true }");
    File.WriteAllText(Path.Combine(current, "day-notes-20261118.txt"), "notes 18");
    File.WriteAllText(Path.Combine(current, "day-notes-20261119.txt"), "notes 19");
    File.WriteAllText(Path.Combine(current, "app-usage-2026-11-18.json"), "{ \"usage\": 18 }");
    File.WriteAllText(Path.Combine(current, "app-usage-2026-11-19.json"), "{ \"usage\": 19 }");
    File.WriteAllText(Path.Combine(current, "app-usage-2026-11-19.json.tmp"), "half written");
    File.WriteAllText(Path.Combine(current, "app-usage-2026-11-17.json.unreadable-20261118090000"), "set aside");
    File.WriteAllText(Path.Combine(current, "crash.log"), "boom");
    File.WriteAllText(Path.Combine(current, "datafolder.json"), "{}");
    File.WriteAllText(Path.Combine(used, "settings.json"), "{ \"used\": true }");

    Check(DataFolder.Plan(current, current + Path.DirectorySeparatorChar) == DataFolder.ChangeKind.SameFolder,
        "same folder is a no-op");
    Check(DataFolder.Plan(current, missing) == DataFolder.ChangeKind.Invalid, "a missing target is invalid");
    Check(DataFolder.Plan(current, empty) == DataFolder.ChangeKind.CopyToEmpty, "an empty target gets a copy");
    Check(DataFolder.Plan(current, used) == DataFolder.ChangeKind.TargetHasData, "existing data in the target is detected");

    var files = DataFolder.DataFiles(current);
    Check(files.SequenceEqual(["settings.json", "day-notes-20261118.txt", "day-notes-20261119.txt",
            "app-usage-2026-11-18.json", "app-usage-2026-11-19.json"]),
        "data files are settings.json, day notes and app usage only: " + string.Join(", ", files));
    Check(DataFolder.DataFiles(missing).Count == 0, "a missing folder has no data files");

    // Backup name.
    var stamp = new DateTime(2026, 11, 19, 9, 5, 7);
    Check(DataFolder.BackupName("settings.json", stamp) == "settings.json.bak-20261119090507", "backup name format");

    // Copy to an empty folder.
    var copied = DataFolder.CopyData(current, empty, stamp);
    Check(copied.Count == 5, "five files copied");
    Check(File.ReadAllText(Path.Combine(empty, "settings.json")) == "{ \"current\": true }", "settings.json copied");
    Check(File.ReadAllText(Path.Combine(empty, "day-notes-20261119.txt")) == "notes 19", "day notes copied");
    Check(File.ReadAllText(Path.Combine(empty, "app-usage-2026-11-18.json")) == "{ \"usage\": 18 }", "app usage copied");
    Check(!File.Exists(Path.Combine(empty, "crash.log")) && !File.Exists(Path.Combine(empty, "datafolder.json")),
        "crash log and pointer are not copied");
    Check(!File.Exists(Path.Combine(empty, "app-usage-2026-11-19.json.tmp")) &&
          Directory.GetFiles(empty, "*.unreadable-*").Length == 0,
        "half-written and set-aside app usage files are not copied");
    Check(File.Exists(Path.Combine(current, "settings.json")) && File.Exists(Path.Combine(current, "day-notes-20261118.txt")),
        "originals are left in place");

    // Replace keeps a backup of the target's settings.json.
    File.WriteAllText(Path.Combine(used, "day-notes-20260101.txt"), "only in target");
    DataFolder.CopyData(current, used, stamp);
    Check(File.ReadAllText(Path.Combine(used, "settings.json")) == "{ \"current\": true }", "replace overwrites settings.json");
    Check(File.ReadAllText(Path.Combine(used, "settings.json.bak-20261119090507")) == "{ \"used\": true }",
        "replace backs up the target's settings.json");
    Check(File.ReadAllText(Path.Combine(used, "day-notes-20260101.txt")) == "only in target",
        "notes only in the target are kept");

    // Replace also backs up an app usage file it overwrites and keeps days only in the target.
    File.WriteAllText(Path.Combine(used, "app-usage-2026-11-19.json"), "{ \"target\": 19 }");
    File.WriteAllText(Path.Combine(used, "app-usage-2026-01-01.json"), "{ \"target\": 1 }");
    var later = stamp.AddMinutes(1);
    DataFolder.CopyData(current, used, later);
    Check(File.ReadAllText(Path.Combine(used, "app-usage-2026-11-19.json")) == "{ \"usage\": 19 }",
        "replace overwrites the day's app usage");
    Check(File.ReadAllText(Path.Combine(used, DataFolder.BackupName("app-usage-2026-11-19.json", later))) == "{ \"target\": 19 }",
        "replace backs up the target's app usage file");
    Check(File.ReadAllText(Path.Combine(used, "app-usage-2026-01-01.json")) == "{ \"target\": 1 }",
        "app usage days only in the target are kept");
    Check(!DataFolder.DataFiles(used).Any(n => n.Contains(".bak-")), "backups are not data files");

    // The store follows a folder switch.
    var store = new SettingsStore(current);
    var settings = store.Load();
    store.SwitchFolder(empty);
    Check(DataFolder.SameFolder(store.Folder, empty), "store reports the new folder");
    settings.FifteenMinuteIntervalMinutes = 25;
    store.Save(settings);
    Check(new SettingsStore(empty).Load().FifteenMinuteIntervalMinutes == 25, "saves go to the new folder");
    Check(File.ReadAllText(Path.Combine(current, "settings.json")) == "{ \"current\": true }", "the old folder is untouched");

    // A move never takes this PC's client id, its session file or the crash log along.
    var withMachineFiles = Path.Combine(root, "machine");
    var moveTarget = Path.Combine(root, "move-target");
    Directory.CreateDirectory(Path.Combine(withMachineFiles, "sessions"));
    Directory.CreateDirectory(moveTarget);
    File.WriteAllText(Path.Combine(withMachineFiles, "settings.json"), "{}");
    File.WriteAllText(Path.Combine(withMachineFiles, ClientIdentity.FileName), "{}");
    File.WriteAllText(Path.Combine(withMachineFiles, "crash.log"), "x");
    File.WriteAllText(Path.Combine(withMachineFiles, "sessions", "abc.json"), "{}");
    var moved = DataFolder.Transfer(withMachineFiles, moveTarget, move: true, stamp);
    Check(moved.Copied.SequenceEqual(["settings.json"]) && moved.Removed.SequenceEqual(["settings.json"]),
        "move transfers only the data files");
    Check(File.Exists(Path.Combine(withMachineFiles, ClientIdentity.FileName)) &&
          File.Exists(Path.Combine(withMachineFiles, "crash.log")) &&
          File.Exists(Path.Combine(withMachineFiles, "sessions", "abc.json")) &&
          !File.Exists(Path.Combine(moveTarget, ClientIdentity.FileName)) &&
          !Directory.Exists(Path.Combine(moveTarget, "sessions")),
        "client.json, crash.log and session files stay where they are");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All data folder checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

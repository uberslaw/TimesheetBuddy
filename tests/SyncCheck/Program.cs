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

var root = Path.Combine(Path.GetTempPath(), "tsb-sync-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var noon = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Local);

TimeEntry Finished(string task, DateTime startLocal, int minutes) => new()
{
    TaskName = task,
    StartUtc = startLocal.ToUniversalTime(),
    EndUtc = startLocal.ToUniversalTime().AddMinutes(minutes)
};

try
{
    // ---------------- Client identity ----------------
    var localA = Path.Combine(root, "localA");
    var idA = ClientIdentity.LoadOrCreate(localA, "PC-A");
    var idA2 = ClientIdentity.LoadOrCreate(localA, "PC-A");
    Check(idA.ClientId != Guid.Empty && idA.ClientId == idA2.ClientId, "client id is stable across starts");
    Check(File.Exists(Path.Combine(localA, ClientIdentity.FileName)), "client id lives in the local folder");
    var copied = ClientIdentity.LoadOrCreate(localA, "PC-B");
    Check(copied.ClientId != idA.ClientId && copied.Machine == "PC-B", "a client.json copied to another machine gets a new id");
    Check(new ClientIdentity(Guid.NewGuid(), "my pc/1").SafeMachine == "my_pc_1", "machine name is made file-name safe");
    var idB = new ClientIdentity(Guid.NewGuid(), "PC-B");
    idA = ClientIdentity.LoadOrCreate(localA, "PC-A");

    // ---------------- Entry stamps ----------------
    var shared = Path.Combine(root, "onedrive");
    Directory.CreateDirectory(shared);
    var clockA = noon;
    var storeA = new SettingsStore(shared) { Client = idA, Clock = () => clockA };
    var a = storeA.Load();
    var e1 = Finished("Inbox", noon.AddHours(-3), 30);
    a.Entries.Add(e1);
    storeA.Save(a);
    Check(e1.CreatedByClientId == idA.ClientId && e1.CreatedByMachine == "PC-A" && e1.ModifiedByClientId == idA.ClientId &&
          e1.ModifiedAtUtc == noon.ToUniversalTime(), "a new entry is stamped with its creator and ModifiedAt on save");
    clockA = noon.AddMinutes(5);
    storeA.Save(a);
    Check(e1.ModifiedAtUtc == noon.ToUniversalTime(), "an unchanged entry keeps its stamp");
    e1.Notes = "edited";
    storeA.Save(a);
    Check(e1.ModifiedAtUtc == noon.AddMinutes(5).ToUniversalTime() && e1.CreatedByClientId == idA.ClientId,
        "an edited entry gets a new ModifiedAt, creator unchanged");

    var legacyFolder = Path.Combine(root, "legacy");
    Directory.CreateDirectory(legacyFolder);
    File.WriteAllText(Path.Combine(legacyFolder, "settings.json"),
        """{ "entries": [ { "id": "11111111-1111-1111-1111-111111111111", "startUtc": "2026-11-18T01:00:00Z", "endUtc": "2026-11-18T02:00:00Z", "taskName": "Old" } ] }""");
    var legacyStore = new SettingsStore(legacyFolder) { Client = idA };
    var legacy = legacyStore.Load();
    legacyStore.Save(legacy);
    Check(legacy.Entries.Single().CreatedByClientId is null, "entries from older data load and are not restamped until changed");
    Check(!File.ReadAllText(Path.Combine(legacyFolder, "settings.json")).Contains("createdByClientId"),
        "unset stamps are not written");

    // ---------------- Journal ----------------
    var journalNow = new DateTime(2026, 11, 19, 2, 0, 0, DateTimeKind.Utc);
    var journalA = new ChangeJournal(() => shared, idA, () => journalNow);
    var journalB = new ChangeJournal(() => shared, idB, () => journalNow.AddMinutes(1));
    journalA.Append(ChangeJournal.Actions.Start, "Started Inbox", e1.Id);
    journalB.Append(ChangeJournal.Actions.Add, "Manual entry: Call 20m");
    var ownFile = Path.Combine(shared, "journal", $"PC-A-{idA.ClientId:N}.jsonl");
    Check(File.Exists(ownFile), "journal file is journal/<machine>-<clientid>.jsonl");
    Check(Directory.GetFiles(Path.Combine(shared, "journal")).Length == 2, "each client writes its own journal file");
    var recent = ChangeJournal.ReadRecent(shared, journalNow.AddDays(-1));
    Check(recent.Count == 2 && recent[0].Machine == "PC-B" && recent[1].Action == "start" && recent[1].EntryId == e1.Id,
        "change log reads every PC's lines, newest first, with machine, action and entry id");
    File.AppendAllText(ownFile, "{\"t\":\"2026-11-19T0");
    Check(ChangeJournal.ReadRecent(shared, journalNow.AddDays(-1)).Count == 2, "a half-synced line is skipped");
    File.WriteAllText(ownFile, "");
    var oldJournal = new ChangeJournal(() => shared, idA, () => journalNow.AddDays(-100));
    oldJournal.Append(ChangeJournal.Actions.Stop, "old");
    journalA.Append(ChangeJournal.Actions.Stop, "new");
    Check(journalA.PruneOwn() == 1 && File.ReadAllLines(ownFile).Length == 1 && File.ReadAllText(ownFile).Contains("new"),
        "lines older than 90 days are pruned from the own file");
    var deadFile = Path.Combine(shared, "journal", "OLD-PC-" + Guid.NewGuid().ToString("N") + ".jsonl");
    File.WriteAllText(deadFile, "{}\n");
    File.SetLastWriteTimeUtc(deadFile, journalNow.AddDays(-120));
    Check(journalA.PruneAbandoned() == 1 && !File.Exists(deadFile) &&
          File.Exists(Path.Combine(shared, "journal", ChangeJournal.FileName(idB))),
        "a journal file untouched for 90 days is deleted; live ones stay");

    // ---------------- Merge on save between two PCs ----------------
    var clockB = noon;
    var storeB = new SettingsStore(shared) { Client = idB, Clock = () => clockB };
    var b = storeB.Load();
    Check(b.Entries.Any(e => e.Id == e1.Id), "PC B starts with the latest data on disk");
    var eB = Finished("Call", noon.AddHours(-1), 20);
    b.Entries.Add(eB);
    storeB.Save(b);
    var eA2 = Finished("Report", noon.AddHours(-2), 40);
    a.Entries.Add(eA2);
    storeA.Save(a);
    Check(a.Entries.Any(e => e.Id == eB.Id) && a.Entries.Any(e => e.Id == eA2.Id),
        "A's save merges B's new entry instead of overwriting it");
    var onDisk = SettingsStore.ReadFile(Path.Combine(shared, "settings.json"))!;
    Check(onDisk.Entries.Count(e => e.Id == eB.Id) == 1 && onDisk.Entries.Any(e => e.Id == eA2.Id), "the file holds both PCs' entries once");

    // Deletion on B must not come back from A.
    b.Entries.RemoveAll(e => e.Id == e1.Id);
    storeB.Save(b);
    Check(b.RemovedEntries.Any(r => r.Id == e1.Id) && b.Entries.All(e => e.Id != e1.Id) && b.Entries.Any(e => e.Id == eA2.Id),
        "B's removal is tombstoned and B picks up A's entry");
    a.Entries.Add(Finished("Lunch", noon.AddMinutes(-30), 30));
    storeA.Save(a);
    Check(a.Entries.All(e => e.Id != e1.Id), "the tombstone removes the entry on A too");
    onDisk = SettingsStore.ReadFile(Path.Combine(shared, "settings.json"))!;
    Check(onDisk.Entries.All(e => e.Id != e1.Id), "a deleted entry is not resurrected by the other PC's save");
    var resurrect = SettingsStore.Clone(onDisk);
    resurrect.Entries.Add(SettingsMerge.Clone(e1));
    var resurrectReport = SettingsMerge.Concurrent(resurrect, SettingsStore.Clone(onDisk), null, DateTime.UtcNow);
    Check(resurrect.Entries.All(e => e.Id != e1.Id) && resurrectReport.EntriesRemoved == 1, "a stale copy holding a tombstoned id loses it");

    // Both edited the same entry: the later ModifiedAt wins.
    var target = a.Entries.Single(e => e.Id == eB.Id);
    clockA = noon.AddMinutes(10);
    target.Notes = "A's note";
    storeA.Save(a);
    var bTarget = b.Entries.Single(e => e.Id == eB.Id);
    clockB = noon.AddMinutes(20);
    bTarget.Notes = "B's later note";
    storeB.Save(b);
    clockA = noon.AddMinutes(25);
    a.Entries.Add(Finished("Tea", noon.AddMinutes(-5), 5));
    storeA.Save(a);
    Check(a.Entries.Single(e => e.Id == eB.Id).Notes == "B's later note", "both changed an entry: the later edit wins");

    // Another install's running timer is not taken over.
    clockB = noon.AddMinutes(30);
    b.Entries.Add(new TimeEntry { TaskName = "Running on B", StartUtc = noon.AddMinutes(28).ToUniversalTime() });
    b.ActiveSession = new ActiveSession { EntryId = b.Entries[^1].Id, TaskName = "Running on B", StartedUtc = noon.AddMinutes(28).ToUniversalTime() };
    storeB.Save(b);
    Check(b.ActiveSession.ClientId == idB.ClientId, "the running session is stamped with its client");
    storeA.Save(a);
    Check(a.Entries.All(e => e.TaskName != "Running on B"), "merge on save does not pull in another PC's running entry");
    var reload = new SettingsStore(shared) { Client = idA }.Load();
    Check(reload.ActiveSession is null && reload.Entries.All(e => e.TaskName != "Running on B"),
        "loading the shared file does not resume another PC's running timer");
    var reloadB = new SettingsStore(shared) { Client = idB }.Load();
    Check(reloadB.ActiveSession is null || reloadB.ActiveSession.ClientId == idB.ClientId, "a PC's own session is still its own");

    // Sequential same-day use: B closes, A starts later and sees everything.
    b.Entries.Single(e => e.TaskName == "Running on B").EndUtc = noon.AddMinutes(40).ToUniversalTime();
    b.ActiveSession = null;
    clockB = noon.AddMinutes(40);
    storeB.Save(b);
    var later = new SettingsStore(shared) { Client = idA, Clock = () => noon.AddHours(1) };
    var aLater = later.Load();
    Check(aLater.Entries.Any(e => e.TaskName == "Running on B" && e.EndUtc is not null) &&
          aLater.Entries.Any(e => e.TaskName == "Tea"), "sequential sessions: the next start loads both PCs' work");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"{failures.Count} sync check(s) failed.");
    return 1;
}

Console.WriteLine("All sync checks passed.");
return 0;

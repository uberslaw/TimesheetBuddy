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

var now = new DateTime(2026, 11, 19, 1, 0, 0, DateTimeKind.Utc);
var idA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
var idB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
var startA = now.AddHours(-2);
var startB = now.AddHours(-1);
var none = new HashSet<string>();

OverlapRules.Me MeA(DateTime? confirmed = null) => new(idA, "PC-A", startA, confirmed);
OverlapRules.Me MeB(DateTime? confirmed = null) => new(idB, "PC-B", startB, confirmed);

SessionFile Session(Guid id, string machine, DateTime processStart, DateTime heartbeat, DateTime? confirmed = null,
    CloseRequest? request = null) => new()
{
    ClientId = id,
    Machine = machine,
    ProcessStartUtc = processStart,
    LastHeartbeatUtc = heartbeat,
    ConfirmedActiveAtUtc = confirmed,
    CloseRequest = request
};

// ---------------- Liveness ----------------
var bLive = Session(idB, "PC-B", startB, now.AddSeconds(-20));
var bStale = Session(idB, "PC-B", startB, now.AddSeconds(-91));
Check(PresenceTiming.Heartbeat == TimeSpan.FromSeconds(20) && PresenceTiming.LiveWindow == TimeSpan.FromSeconds(90),
    "defaults: 20 s heartbeat, 90 s live window");
Check(OverlapRules.IsLive(bLive, idA, now), "a heartbeat 20 s old is live");
Check(!OverlapRules.IsLive(bStale, idA, now), "a heartbeat 91 s old is stale");
Check(!OverlapRules.IsLive(Session(idA, "PC-A", startA, now), idA, now), "the own client id is never 'another' session");
Check(OverlapRules.LiveOthers([Session(Guid.NewGuid(), "pc-a", startB, now)], MeA(), now).Count == 0,
    "another install on the same machine does not count as another PC");

// ---------------- Prompt ----------------
var d = OverlapRules.Decide(MeA(), [bLive], now, screenLocked: false, promptOpen: false, none);
Check(d.Kind == OverlapRules.Kind.ShowPrompt && d.Other?.Machine == "PC-B", "a live client on another PC shows the prompt");
d = OverlapRules.Decide(MeB(), [Session(idA, "PC-A", startA, now)], now, false, false, none);
Check(d.Kind == OverlapRules.Kind.ShowPrompt, "both machines show the prompt independently");
Check(OverlapRules.Decide(MeA(), [bStale], now, false, false, none).Kind == OverlapRules.Kind.None,
    "a stale session file shows nothing (sequential use, or a crashed PC)");
d = OverlapRules.Decide(MeA(), [bLive], now, screenLocked: true, promptOpen: false, none);
Check(d.Kind == OverlapRules.Kind.None && d.Other is not null, "locked: the prompt is held back, overlap still known");
Check(OverlapRules.Decide(MeA(), [bLive], now, screenLocked: false, promptOpen: false, none).Kind == OverlapRules.Kind.ShowPrompt,
    "after unlock the held-back prompt shows");
Check(OverlapRules.Decide(MeA(), [bLive], now, false, promptOpen: true, none).Kind == OverlapRules.Kind.None,
    "an open prompt is not shown twice");
Check(OverlapRules.Decide(MeA(), [bStale], now, false, promptOpen: true, none).Kind == OverlapRules.Kind.HidePrompt,
    "the prompt closes when the other session goes away");
var dismissed = new HashSet<string> { OverlapRules.SessionKey(bLive) };
Check(OverlapRules.Decide(MeA(), [bLive], now, false, false, dismissed).Kind == OverlapRules.Kind.None,
    "Not now: no prompt again for that session");
var bRestarted = Session(idB, "PC-B", startB.AddMinutes(30), now);
Check(OverlapRules.Decide(MeA(), [bRestarted], now, false, false, dismissed).Kind == OverlapRules.Kind.ShowPrompt,
    "a restart of the other app asks again");

// ---------------- Locked never confirms or denies ----------------
var aYes = now.AddSeconds(-10);
var requestFromA = OverlapRules.RequestFor(bLive, MeA(aYes), aYes);
var aConfirmed = Session(idA, "PC-A", startA, now, aYes, requestFromA);
d = OverlapRules.Decide(MeB(), [aConfirmed], now, screenLocked: true, promptOpen: false, none);
Check(d.Kind == OverlapRules.Kind.CloseNow && d.Other?.Machine == "PC-A", "a close request is honoured even while locked");
d = OverlapRules.Decide(MeA(), [bLive], now, screenLocked: true, false, none);
Check(d.Kind != OverlapRules.Kind.CloseNow && d.Kind != OverlapRules.Kind.AlreadyConfirmedThenClose,
    "being locked never makes this PC lose");
Check(OverlapRules.Decide(MeB(), [Session(idA, "PC-A", startA, now)], now, screenLocked: true, false, none).Kind == OverlapRules.Kind.None,
    "the other PC being locked (no Yes) never makes it win");

// ---------------- Close request ----------------
Check(OverlapRules.Decide(MeB(), [aConfirmed], now, false, promptOpen: true, none).Kind == OverlapRules.Kind.CloseNow,
    "a close request closes this one whether or not its prompt was answered");
var laterLaunch = new OverlapRules.Me(idB, "PC-B", startB.AddMinutes(45), null);
Check(OverlapRules.Decide(laterLaunch, [aConfirmed], now, false, false, none).Kind == OverlapRules.Kind.ShowPrompt,
    "a close request never closes a later launch of the same install");
var staleRequester = Session(idA, "PC-A", startA, now.AddMinutes(-5), aYes, requestFromA);
Check(OverlapRules.Decide(MeB(), [staleRequester], now, false, false, none).Kind == OverlapRules.Kind.None,
    "a request from a session that is no longer live is ignored");
Check(OverlapRules.Decide(MeA(aYes), [bLive], now, false, false, none).Kind == OverlapRules.Kind.None,
    "the confirmed PC is not prompted again while it waits");

// ---------------- Both said Yes ----------------
var bYesLater = now.AddSeconds(-5);
var bConfirmedLater = Session(idB, "PC-B", startB, now, bYesLater,
    OverlapRules.RequestFor(Session(idA, "PC-A", startA, now), MeB(bYesLater), bYesLater));
d = OverlapRules.Decide(MeA(aYes), [bConfirmedLater], now, false, false, none);
Check(d.Kind == OverlapRules.Kind.None, "earliest Yes wins: A (earlier) ignores B's request");
d = OverlapRules.Decide(MeB(bYesLater), [aConfirmed], now, false, false, none);
Check(d.Kind == OverlapRules.Kind.AlreadyConfirmedThenClose && d.Other?.Machine == "PC-A",
    "the later Yes shows 'Already confirmed on PC-A' and closes");
var aConfirmedNoRequestYet = Session(idA, "PC-A", startA, now, aYes);
Check(OverlapRules.Decide(MeB(bYesLater), [aConfirmedNoRequestYet], now, false, false, none).Kind ==
      OverlapRules.Kind.AlreadyConfirmedThenClose, "the later Yes loses even before the request syncs");
var tie = now.AddSeconds(-7);
var aTie = Session(idA, "PC-A", startA, now, tie, OverlapRules.RequestFor(bLive, MeA(tie), tie));
var bTie = Session(idB, "PC-B", startB, now, tie,
    OverlapRules.RequestFor(Session(idA, "PC-A", startA, now), MeB(tie), tie));
Check(OverlapRules.Decide(MeA(tie), [bTie], now, false, false, none).Kind == OverlapRules.Kind.None &&
      OverlapRules.Decide(MeB(tie), [aTie], now, false, false, none).Kind == OverlapRules.Kind.AlreadyConfirmedThenClose,
    "same Yes time: the smaller client id wins on both PCs");
Check(OverlapRules.FirstWins(aYes, idB, bYesLater, idA) && !OverlapRules.FirstWins(bYesLater, idA, aYes, idB),
    "FirstWins: time first, then client id");

// ---------------- Clearing after the overlap ----------------
Check(!OverlapRules.ShouldClearConfirmation(MeA(aYes), requestFromA, [bLive], now), "kept while the target is still live");
Check(OverlapRules.ShouldClearConfirmation(MeA(aYes), requestFromA, [], now), "cleared once the target's session file is gone");
Check(OverlapRules.ShouldClearConfirmation(MeA(aYes), requestFromA, [bStale], now), "cleared once the target is stale");
Check(OverlapRules.ShouldClearConfirmation(MeA(aYes), requestFromA, [bRestarted], now), "a restarted target is a new overlap");
Check(!OverlapRules.ShouldClearConfirmation(MeA(), null, [], now), "nothing to clear without a Yes");

// ---------------- Keep / Discard ----------------
var codeId = Guid.NewGuid();
var mine = new AppSettings { JobCodes = [new JobCode { Id = codeId, Code = "089082-04", Description = "CS OFFICE SUPPORT" }] };
var shared = new TimeEntry { TaskName = "Shared", StartUtc = now.AddHours(-3), EndUtc = now.AddHours(-2.5) };
var deletedHere = new TimeEntry { TaskName = "Deleted here", StartUtc = now.AddHours(-2.5), EndUtc = now.AddHours(-2.4) };
mine.Entries.Add(SettingsMerge.Clone(shared));
mine.RemovedEntries.Add(new RemovedEntry { Id = deletedHere.Id, RemovedUtc = now.AddMinutes(-30) });
var onlyThere = new TimeEntry { TaskName = "Only on B", StartUtc = now.AddHours(-2), EndUtc = now.AddHours(-1.5), JobCodeId = codeId, JobCodeText = "04 — CS OFFICE SUPPORT" };
var unknownCode = new TimeEntry { TaskName = "Unknown code", StartUtc = now.AddHours(-1.4), EndUtc = now.AddHours(-1.3), JobCodeId = Guid.NewGuid(), JobCodeText = "04 — CS OFFICE SUPPORT" };
var bSnapshot = Session(idB, "PC-B", startB, now.AddSeconds(-15));
bSnapshot.TodayEntries = [shared, deletedHere, onlyThere, unknownCode, SettingsMerge.Clone(onlyThere)];
bSnapshot.Running = new RunningSnapshot { EntryId = Guid.NewGuid(), TaskName = "Report", StartedUtc = now.AddMinutes(-40) };

var items = OverlapMerge.Items(mine, bSnapshot, now);
Check(items.Count(i => i.Id == onlyThere.Id) == 1, "items are deduped by id");
Check(items.Single(i => i.Id == shared.Id).AlreadyHere, "an entry already in this timesheet is marked as already here");
Check(items.All(i => i.Id != deletedHere.Id), "an entry deleted here (tombstone) is not offered again");
var runningItem = items.Single(i => i.IsRunning);
Check(runningItem.Entry.EndUtc == now && runningItem.Entry.StartUtc == now.AddMinutes(-40) && runningItem.Entry.CreatedByMachine == "PC-B",
    "the other PC's running timer is offered as a finished entry ending now");
var bGone = Session(idB, "PC-B", startB, now.AddMinutes(-3));
Check(OverlapMerge.RunningEnd(bGone, now) == now.AddMinutes(-3), "a timer whose app stopped heartbeating ends at its last heartbeat");

var keep = OverlapMerge.Keep(mine, items, new HashSet<Guid> { onlyThere.Id, runningItem.Id, shared.Id, unknownCode.Id });
Check(keep.Count == 3 && mine.Entries.Count(e => e.Id == shared.Id) == 1, "Keep selected adds the ticked items; already-here ones are not duplicated");
Check(mine.Entries.Single(e => e.Id == unknownCode.Id).JobCodeId == codeId, "an unknown job code id is matched by its label");
Check(mine.Entries.Single(e => e.Id == runningItem.Id).EndUtc == now, "the kept running timer is a finished entry");
var again = OverlapMerge.Keep(mine, OverlapMerge.Items(mine, bSnapshot, now), new HashSet<Guid>(items.Select(i => i.Id)));
Check(again.Count == 0, "keeping twice adds nothing");
var discard = new AppSettings();
Check(OverlapMerge.Keep(discard, items, new HashSet<Guid>()).Count == 0 && discard.Entries.Count == 0, "Discard all keeps nothing");

// ---------------- Snapshot published in the session file ----------------
var local = new AppSettings();
var finishedToday = new TimeEntry { TaskName = "Done", StartUtc = DateTime.Today.AddHours(9).ToUniversalTime(), EndUtc = DateTime.Today.AddHours(10).ToUniversalTime() };
var yesterday = new TimeEntry { TaskName = "Old", StartUtc = DateTime.Today.AddDays(-1).AddHours(9).ToUniversalTime(), EndUtc = DateTime.Today.AddDays(-1).AddHours(10).ToUniversalTime() };
var softDeleted = new TimeEntry { TaskName = "Gone", StartUtc = finishedToday.StartUtc, EndUtc = finishedToday.EndUtc, IsDeleted = true };
var runningEntry = new TimeEntry { TaskName = "Now", StartUtc = DateTime.UtcNow.AddMinutes(-5) };
local.Entries.AddRange([finishedToday, yesterday, softDeleted, runningEntry]);
local.ActiveSession = new ActiveSession { EntryId = runningEntry.Id, TaskName = "Now", StartedUtc = runningEntry.StartUtc };
var (today, running) = OverlapMerge.Snapshot(local, DateTime.Today);
Check(today.Count == 1 && today[0].Id == finishedToday.Id, "the snapshot has today's finished, non-deleted entries only");
Check(running?.EntryId == runningEntry.Id && running.TaskName == "Now", "the snapshot carries the unsaved running timer");

// ---------------- Session files, close without saving ----------------
var root = Path.Combine(Path.GetTempPath(), "tsb-presence-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(root);
    Check(SessionFiles.Write(root, aConfirmed) && File.Exists(SessionFiles.PathFor(root, idA)), "session file is sessions/<clientid>.json");
    SessionFiles.Write(root, bSnapshot);
    File.WriteAllText(Path.Combine(SessionFiles.FolderFor(root), "half.json"), "{ \"clientId\": ");
    var others = SessionFiles.ReadOthers(root, idB);
    Check(others.Count == 1 && others[0].ClientId == idA && others[0].CloseRequest?.TargetClientId == idB &&
          others[0].ConfirmedActiveAtUtc == aYes, "reading skips the own file and a half-synced one, and round-trips the request");
    Check(OverlapRules.Decide(MeB(), others, now, false, false, none).Kind == OverlapRules.Kind.CloseNow,
        "B decides to close from A's file on disk");
    SessionFiles.Delete(root, idB);
    Check(!File.Exists(SessionFiles.PathFor(root, idB)), "the own session file is deleted on exit");

    // Closing on request: the store is suspended, so neither the running timer nor anything else is written.
    var store = new SettingsStore(root);
    var data = store.Load();
    var before = File.ReadAllText(store.StoragePath);
    data.Entries.Add(new TimeEntry { TaskName = "Unsaved on B", StartUtc = DateTime.UtcNow.AddMinutes(-3) });
    store.Suspend();
    store.Save(data);
    Check(File.ReadAllText(store.StoragePath) == before, "after a close request settings.json is never written");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"{failures.Count} presence check(s) failed.");
    return 1;
}

Console.WriteLine("All presence checks passed.");
return 0;

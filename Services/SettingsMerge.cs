using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Merges two copies of settings.json without losing time entries. Used by Import data…, by the
/// Merge choice when the data folder changes, and before every save when another PC (OneDrive) has
/// changed the file since this process last read or wrote it. No WPF here.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Entries are the same when their ids match; otherwise when task, category, job code, ticket,
/// start, end and manual minutes match (start/end to the second), so copies without ids dedupe too.</item>
/// <item>An entry id listed in <see cref="AppSettings.RemovedEntries"/> on either side was deleted for good
/// and never comes back. Removing an entry from the list without a tombstone does nothing to the other copy,
/// so a stale or lagging file can never delete entries.</item>
/// <item>Same id on both sides: the preferred side wins, except that in a concurrent merge an entry this
/// process has not touched since the last sync takes the other side's edit (soft delete, restore, moves).</item>
/// <item>Job codes match by id, else by code text (the other side's entries are re-pointed); favorites by id,
/// else by name + category; categories by text, case-insensitive.</item>
/// <item>Preferences (window and widget positions, prompts, App Track switch), the running timer and No Task
/// Selected state are never taken from the other copy.</item>
/// </list>
/// </remarks>
public static class SettingsMerge
{
    /// <summary>How long a deleted entry id is remembered so an older copy can't bring it back.</summary>
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(400);

    public enum Mode
    {
        /// <summary>Import data… / folder merge: nothing is removed from the target, deleted and running entries are not brought in.</summary>
        Import,

        /// <summary>Save-time merge of another process's changes, optionally against the last synced copy.</summary>
        Concurrent
    }

    public sealed class Report
    {
        public int IncomingEntries { get; set; }
        public int EntriesAdded { get; set; }
        public int EntriesUpdated { get; set; }
        public int EntriesRemoved { get; set; }
        public int DuplicateEntries { get; set; }
        public int SkippedDeleted { get; set; }
        public int SkippedRunning { get; set; }
        public int SkippedRemoved { get; set; }
        public int UnassignedAdded { get; set; }
        public DateTime? FirstAddedDay { get; set; }
        public DateTime? LastAddedDay { get; set; }
        public DateTime? FirstIncomingDay { get; set; }
        public DateTime? LastIncomingDay { get; set; }
        public List<string> JobCodesAdded { get; } = [];
        public List<string> CategoriesAdded { get; } = [];
        public List<string> FavoritesAdded { get; } = [];
        public int ListItemsRemoved { get; set; }
        public int ListItemsUpdated { get; set; }

        public bool Changed =>
            EntriesAdded + EntriesUpdated + EntriesRemoved + UnassignedAdded + ListItemsRemoved + ListItemsUpdated > 0 ||
            JobCodesAdded.Count + CategoriesAdded.Count + FavoritesAdded.Count > 0;

        /// <summary>Nothing would be added or changed: the target already holds everything.</summary>
        public bool NothingToAdd =>
            EntriesAdded + EntriesUpdated == 0 &&
            JobCodesAdded.Count + CategoriesAdded.Count + FavoritesAdded.Count == 0;
    }

    /// <summary>
    /// Brings <paramref name="incoming"/> into <paramref name="target"/> (mutated). Never removes anything
    /// from the target except entries the incoming copy has tombstoned. On the same entry id the target's
    /// version is kept, or the incoming one when <paramref name="preferIncoming"/>.
    /// </summary>
    public static Report Import(AppSettings target, AppSettings incoming, DateTime nowUtc, bool preferIncoming = false) =>
        Merge(target, incoming, baseline: null, Mode.Import, preferIncoming, nowUtc);

    /// <summary>
    /// Folds another process's copy (<paramref name="theirs"/>, read from disk) into <paramref name="mine"/>
    /// (mutated). <paramref name="baseline"/> is what this process last read or wrote, or null when unknown.
    /// </summary>
    public static Report Concurrent(AppSettings mine, AppSettings theirs, AppSettings? baseline, DateTime nowUtc) =>
        Merge(mine, theirs, baseline, Mode.Concurrent, preferIncoming: false, nowUtc);

    /// <summary>Ids present in <paramref name="previousIds"/> but no longer in <paramref name="settings"/> get a tombstone.</summary>
    public static int RecordRemovals(AppSettings settings, IReadOnlyCollection<Guid> previousIds, DateTime nowUtc)
    {
        Normalize(settings);
        if (previousIds.Count == 0) return 0;
        var present = settings.Entries.Select(e => e.Id).ToHashSet();
        var known = settings.RemovedEntries.Select(r => r.Id).ToHashSet();
        var added = 0;
        foreach (var id in previousIds)
        {
            if (present.Contains(id) || !known.Add(id)) continue;
            settings.RemovedEntries.Add(new RemovedEntry { Id = id, RemovedUtc = nowUtc });
            added++;
        }

        return added;
    }

    private static Report Merge(AppSettings mine, AppSettings theirs, AppSettings? baseline, Mode mode,
        bool preferIncoming, DateTime nowUtc)
    {
        Normalize(mine);
        Normalize(theirs);
        if (baseline is not null) Normalize(baseline);

        var report = new Report();
        var codeMap = MergeJobCodes(mine, theirs, baseline, mode, report);
        MergeCategories(mine, theirs, baseline, mode, report);
        MergeFavorites(mine, theirs, baseline, mode, codeMap, report);
        MergeEntries(mine, theirs, baseline, mode, preferIncoming, codeMap, nowUtc, report);

        if (mode == Mode.Concurrent)
        {
            MergeUnassigned(mine, theirs, baseline, report);
            if (baseline is not null && SameIds(mine.JobCodeViewIds, baseline.JobCodeViewIds) &&
                !SameIds(theirs.JobCodeViewIds, baseline.JobCodeViewIds))
                mine.JobCodeViewIds = theirs.JobCodeViewIds?.Select(id => codeMap.GetValueOrDefault(id, id)).Distinct().ToList();
        }

        return report;
    }

    private static void MergeEntries(AppSettings mine, AppSettings theirs, AppSettings? baseline, Mode mode,
        bool preferIncoming, Dictionary<Guid, Guid> codeMap, DateTime nowUtc, Report report)
    {
        var activeId = mine.ActiveSession?.EntryId;
        var mineIndex = new Dictionary<Guid, int>();
        for (var i = 0; i < mine.Entries.Count; i++)
            mineIndex.TryAdd(mine.Entries[i].Id, i);
        var baseById = new Dictionary<Guid, TimeEntry>();
        foreach (var e in baseline?.Entries ?? [])
            baseById.TryAdd(e.Id, e);

        var tombstones = new Dictionary<Guid, DateTime>();
        foreach (var r in mine.RemovedEntries.Concat(theirs.RemovedEntries))
        {
            if (!tombstones.TryGetValue(r.Id, out var at) || r.RemovedUtc < at)
                tombstones[r.Id] = r.RemovedUtc;
        }

        var contentKeys = mine.Entries.Select(ContentKey).ToHashSet(StringComparer.Ordinal);
        var seenIncoming = new HashSet<Guid>();

        foreach (var original in theirs.Entries)
        {
            if (!seenIncoming.Add(original.Id)) continue;
            var t = Clone(original);
            if (t.JobCodeId is Guid code && codeMap.TryGetValue(code, out var mapped))
                t.JobCodeId = mapped;

            if (!t.IsDeleted)
            {
                report.IncomingEntries++;
                var day = DayOf(t);
                if (report.FirstIncomingDay is null || day < report.FirstIncomingDay) report.FirstIncomingDay = day;
                if (report.LastIncomingDay is null || day > report.LastIncomingDay) report.LastIncomingDay = day;
            }

            if (mineIndex.TryGetValue(t.Id, out var index))
            {
                var m = mine.Entries[index];
                var takeTheirs = mode == Mode.Import
                    ? preferIncoming && t.Id != activeId && Fingerprint(m) != Fingerprint(t)
                    : baseById.TryGetValue(t.Id, out var b) && Fingerprint(m) == Fingerprint(b) &&
                      Fingerprint(t) != Fingerprint(b) && t.Id != activeId;
                if (takeTheirs)
                {
                    contentKeys.Remove(ContentKey(m));
                    mine.Entries[index] = t;
                    contentKeys.Add(ContentKey(t));
                    report.EntriesUpdated++;
                }
                else if (mode == Mode.Import && !t.IsDeleted)
                {
                    report.DuplicateEntries++;
                }

                continue;
            }

            if (tombstones.ContainsKey(t.Id))
            {
                if (!t.IsDeleted) report.SkippedRemoved++;
                continue;
            }

            if (mode == Mode.Import && t.IsDeleted)
            {
                report.SkippedDeleted++;
                continue;
            }

            if (mode == Mode.Import && t.EndUtc is null && t.ManualMinutes <= 0)
            {
                report.SkippedRunning++;
                continue;
            }

            if (!contentKeys.Add(ContentKey(t)))
            {
                if (!t.IsDeleted) report.DuplicateEntries++;
                continue;
            }

            mine.Entries.Add(t);
            mineIndex[t.Id] = mine.Entries.Count - 1;
            if (!t.IsDeleted)
            {
                report.EntriesAdded++;
                var day = DayOf(t);
                if (report.FirstAddedDay is null || day < report.FirstAddedDay) report.FirstAddedDay = day;
                if (report.LastAddedDay is null || day > report.LastAddedDay) report.LastAddedDay = day;
            }
        }

        // The other copy's tombstones remove entries here too (ids are never reused), except the running timer's.
        var theirTombstones = theirs.RemovedEntries.Select(r => r.Id).ToHashSet();
        report.EntriesRemoved += mine.Entries.RemoveAll(e => theirTombstones.Contains(e.Id) && e.Id != activeId);

        var present = mine.Entries.Select(e => e.Id).ToHashSet();
        var cutoff = nowUtc - TombstoneRetention;
        mine.RemovedEntries = tombstones
            .Where(p => !present.Contains(p.Key) && p.Value >= cutoff)
            .Select(p => new RemovedEntry { Id = p.Key, RemovedUtc = p.Value })
            .OrderBy(r => r.RemovedUtc)
            .ToList();
    }

    private static Dictionary<Guid, Guid> MergeJobCodes(AppSettings mine, AppSettings theirs, AppSettings? baseline,
        Mode mode, Report report)
    {
        var map = new Dictionary<Guid, Guid>();
        var baseById = (baseline?.JobCodes ?? []).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        var theirIds = theirs.JobCodes.Select(c => c.Id).ToHashSet();

        if (mode == Mode.Concurrent && baseline is not null)
        {
            for (var i = mine.JobCodes.Count - 1; i >= 0; i--)
            {
                var m = mine.JobCodes[i];
                if (theirIds.Contains(m.Id) || !baseById.TryGetValue(m.Id, out var b) || CodeFingerprint(m) != CodeFingerprint(b))
                    continue;
                mine.JobCodes.RemoveAt(i);
                report.ListItemsRemoved++;
            }
        }

        foreach (var t in theirs.JobCodes)
        {
            var byId = mine.JobCodes.FindIndex(c => c.Id == t.Id);
            if (byId >= 0)
            {
                if (mode == Mode.Concurrent && baseById.TryGetValue(t.Id, out var b) &&
                    CodeFingerprint(mine.JobCodes[byId]) == CodeFingerprint(b) && CodeFingerprint(t) != CodeFingerprint(b))
                {
                    mine.JobCodes[byId] = CloneCode(t);
                    report.ListItemsUpdated++;
                }

                continue;
            }

            var byCode = mine.JobCodes.FirstOrDefault(c => SameText(c.Code, t.Code));
            if (byCode is not null)
            {
                map[t.Id] = byCode.Id;
                continue;
            }

            if (mode == Mode.Concurrent && baseById.ContainsKey(t.Id)) continue;

            mine.JobCodes.Add(CloneCode(t));
            report.JobCodesAdded.Add(t.Display);
        }

        return map;
    }

    private static void MergeCategories(AppSettings mine, AppSettings theirs, AppSettings? baseline, Mode mode, Report report)
    {
        var inBase = new HashSet<string>(baseline?.Categories ?? [], StringComparer.OrdinalIgnoreCase);
        var inTheirs = new HashSet<string>(theirs.Categories, StringComparer.OrdinalIgnoreCase);

        if (mode == Mode.Concurrent && baseline is not null)
        {
            report.ListItemsRemoved += mine.Categories.RemoveAll(c => inBase.Contains(c) && !inTheirs.Contains(c));
            if (mine.Categories.Count == 0) mine.Categories.Add("General");
        }

        var inMine = new HashSet<string>(mine.Categories, StringComparer.OrdinalIgnoreCase);
        foreach (var c in theirs.Categories)
        {
            if (inMine.Contains(c) || (mode == Mode.Concurrent && inBase.Contains(c))) continue;
            mine.Categories.Add(c);
            inMine.Add(c);
            report.CategoriesAdded.Add(c);
        }
    }

    private static void MergeFavorites(AppSettings mine, AppSettings theirs, AppSettings? baseline, Mode mode,
        Dictionary<Guid, Guid> codeMap, Report report)
    {
        var baseIds = (baseline?.FavoriteTasks ?? []).Select(f => f.Id).ToHashSet();
        var theirIds = theirs.FavoriteTasks.Select(f => f.Id).ToHashSet();

        if (mode == Mode.Concurrent && baseline is not null)
            report.ListItemsRemoved += mine.FavoriteTasks.RemoveAll(f => baseIds.Contains(f.Id) && !theirIds.Contains(f.Id));

        foreach (var t in theirs.FavoriteTasks)
        {
            if (mine.FavoriteTasks.Any(f => f.Id == t.Id ||
                                            (SameText(f.Name, t.Name) && SameText(f.Category, t.Category))))
                continue;
            if (mode == Mode.Concurrent && baseIds.Contains(t.Id)) continue;

            mine.FavoriteTasks.Add(new FavoriteTask
            {
                Id = t.Id,
                Name = t.Name,
                Category = t.Category,
                JobCodeId = t.JobCodeId is Guid id ? codeMap.GetValueOrDefault(id, id) : null
            });
            report.FavoritesAdded.Add(t.Name);
        }
    }

    private static void MergeUnassigned(AppSettings mine, AppSettings theirs, AppSettings? baseline, Report report)
    {
        static string Key(UnassignedSegment s) => $"{s.StartUtc.Ticks}-{s.EndUtc.Ticks}";
        var inBase = (baseline?.UnassignedSegments ?? []).Select(Key).ToHashSet();
        var inTheirs = theirs.UnassignedSegments.Select(Key).ToHashSet();

        if (baseline is not null)
            report.ListItemsRemoved += mine.UnassignedSegments.RemoveAll(s => inBase.Contains(Key(s)) && !inTheirs.Contains(Key(s)));

        var inMine = mine.UnassignedSegments.Select(Key).ToHashSet();
        foreach (var s in theirs.UnassignedSegments)
        {
            if (inMine.Contains(Key(s)) || inBase.Contains(Key(s))) continue;
            mine.UnassignedSegments.Add(new UnassignedSegment { StartUtc = s.StartUtc, EndUtc = s.EndUtc });
            inMine.Add(Key(s));
            report.UnassignedAdded++;
        }
    }

    /// <summary>
    /// Fills gaps left by older or hand-edited files: null lists and strings, unspecified date kinds
    /// (read as UTC), missing ids, negative minutes, entries without a start.
    /// </summary>
    public static void Normalize(AppSettings s)
    {
        s.JobCodes = (s.JobCodes ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Code)).ToList();
        foreach (var c in s.JobCodes)
        {
            if (c.Id == Guid.Empty) c.Id = Guid.NewGuid();
            c.Code = c.Code.Trim();
            c.Description = (c.Description ?? string.Empty).Trim();
        }

        s.FavoriteTasks = (s.FavoriteTasks ?? []).Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Name)).ToList();
        foreach (var f in s.FavoriteTasks)
        {
            if (f.Id == Guid.Empty) f.Id = Guid.NewGuid();
            f.Name = f.Name.Trim();
            f.Category = string.IsNullOrWhiteSpace(f.Category) ? "General" : f.Category.Trim();
        }

        s.Categories = (s.Categories ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        s.Entries = (s.Entries ?? []).Where(e => e is not null && e.StartUtc != default).ToList();
        foreach (var e in s.Entries)
        {
            if (e.Id == Guid.Empty) e.Id = Guid.NewGuid();
            e.StartUtc = AsUtc(e.StartUtc);
            if (e.EndUtc is DateTime end) e.EndUtc = AsUtc(end);
            e.TaskName ??= string.Empty;
            e.Category = string.IsNullOrWhiteSpace(e.Category) ? "General" : e.Category;
            e.JobCodeText ??= string.Empty;
            e.Ticket ??= string.Empty;
            e.Notes ??= string.Empty;
            e.Source = string.IsNullOrWhiteSpace(e.Source) ? "timer" : e.Source;
            if (e.ManualMinutes < 0) e.ManualMinutes = 0;
        }

        s.UnassignedSegments = (s.UnassignedSegments ?? [])
            .Where(u => u is not null && u.EndUtc > u.StartUtc)
            .Select(u => new UnassignedSegment { StartUtc = AsUtc(u.StartUtc), EndUtc = AsUtc(u.EndUtc) })
            .ToList();
        s.RemovedEntries = (s.RemovedEntries ?? []).Where(r => r is not null && r.Id != Guid.Empty).ToList();
    }

    /// <summary>Identity for entries without a usable id: task, category, job code, ticket, start, end, manual minutes.</summary>
    public static string ContentKey(TimeEntry e)
    {
        var code = e.JobCodeId?.ToString() ?? (e.JobCodeText ?? string.Empty).Trim().ToUpperInvariant();
        return string.Join("|",
            (e.TaskName ?? string.Empty).Trim().ToUpperInvariant(),
            (e.Category ?? string.Empty).Trim().ToUpperInvariant(),
            code,
            (e.Ticket ?? string.Empty).Trim().ToUpperInvariant(),
            Seconds(e.StartUtc),
            e.EndUtc is DateTime end ? Seconds(end) : "open",
            e.ManualMinutes);
    }

    private static string Fingerprint(TimeEntry e) => string.Join("|",
        e.Id, e.StartUtc.Ticks, e.EndUtc?.Ticks, e.TaskName, e.Category, e.JobCodeId, e.JobCodeText, e.Ticket,
        e.Notes, e.ManualMinutes, e.Source, e.IsDeleted, e.DeletedAtLocal?.Ticks);

    private static string CodeFingerprint(JobCode c) => $"{c.Id}|{c.Code}|{c.Description}|{c.IsActive}";

    private static long Seconds(DateTime utc) => AsUtc(utc).Ticks / TimeSpan.TicksPerSecond;

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static DateTime DayOf(TimeEntry e) => e.StartUtc.ToLocalTime().Date;

    private static bool SameText(string? a, string? b) =>
        string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameIds(List<Guid>? a, List<Guid>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    private static JobCode CloneCode(JobCode c) => new()
    {
        Id = c.Id,
        Code = c.Code,
        Description = c.Description,
        IsActive = c.IsActive
    };

    public static TimeEntry Clone(TimeEntry e) => new()
    {
        Id = e.Id,
        StartUtc = e.StartUtc,
        EndUtc = e.EndUtc,
        TaskName = e.TaskName,
        Category = e.Category,
        JobCodeId = e.JobCodeId,
        JobCodeText = e.JobCodeText,
        Ticket = e.Ticket,
        Notes = e.Notes,
        ManualMinutes = e.ManualMinutes,
        Source = e.Source,
        IsDeleted = e.IsDeleted,
        DeletedAtLocal = e.DeletedAtLocal
    };
}

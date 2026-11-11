using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Which job codes the alternate view lists. A null saved list means "not customized yet"
/// and resolves to the active codes. A saved list is an explicit order and may be empty.
/// </summary>
public static class JobCodeViewList
{
    public static IReadOnlyList<JobCode> Resolve(IReadOnlyList<JobCode> all, IReadOnlyList<Guid>? savedIds)
    {
        if (savedIds is null)
            return all.Where(code => code.IsActive).ToList();

        var byId = all.ToDictionary(code => code.Id);
        var result = new List<JobCode>(savedIds.Count);
        var seen = new HashSet<Guid>();
        foreach (var id in savedIds)
        {
            if (!seen.Add(id)) continue;
            if (byId.TryGetValue(id, out var code))
                result.Add(code);
        }

        return result;
    }
}

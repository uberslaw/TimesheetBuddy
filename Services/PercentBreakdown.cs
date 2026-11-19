namespace TimesheetBuddy.Services;

/// <summary>
/// Whole-percent shares of a total that always add up to exactly 100 (largest-remainder method).
/// </summary>
public static class PercentBreakdown
{
    /// <summary>
    /// One whole percent per part, in the same order, summing to 100. Each part is floored, then the
    /// leftover points go to the largest fractional parts; ties go to the larger part, then the
    /// earlier one. Negative parts count as zero. Returns an empty list when the total is zero.
    /// </summary>
    public static IReadOnlyList<int> Shares(IReadOnlyList<TimeSpan> parts)
    {
        var ticks = new long[parts.Count];
        Int128 total = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            ticks[i] = Math.Max(0, parts[i].Ticks);
            total += ticks[i];
        }

        if (total == 0)
            return [];

        var shares = new int[parts.Count];
        var remainders = new Int128[parts.Count];
        var assigned = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var scaled = (Int128)ticks[i] * 100;
            shares[i] = (int)(scaled / total);
            remainders[i] = scaled % total;
            assigned += shares[i];
        }

        var order = Enumerable.Range(0, parts.Count)
            .OrderByDescending(i => remainders[i])
            .ThenByDescending(i => ticks[i])
            .ThenBy(i => i)
            .ToList();
        for (var k = 0; k < 100 - assigned; k++)
            shares[order[k]]++;

        return shares;
    }

    public static string Format(int percent) => $"{percent}%";
}

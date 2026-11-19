using TimesheetBuddy.Services;

var failures = new List<string>();

void Check(TimeSpan[] parts, int[] expected, string message)
{
    var actual = PercentBreakdown.Shares(parts);
    var actualText = actual.Count == 0 ? "(none)" : string.Join("/", actual);
    var expectedText = expected.Length == 0 ? "(none)" : string.Join("/", expected);
    var sumOk = actual.Count == 0 || actual.Sum() == 100;
    if (actual.SequenceEqual(expected) && sumOk)
    {
        Console.WriteLine($"PASS  {message} ({actualText})");
        return;
    }

    failures.Add(message);
    Console.WriteLine($"FAIL  {message}: expected {expectedText}, got {actualText} (sum {actual.Sum()})");
}

static TimeSpan Hms(int h, int m, int s) => new(h, m, s);
static TimeSpan Min(int m) => TimeSpan.FromMinutes(m);

Check([Min(20), Min(20), Min(20)], [34, 33, 33], "thirds: the leftover point goes to the first row");
Check([Hms(2, 9, 5), Hms(1, 6, 45), Hms(0, 54, 33), Hms(0, 8, 38)], [50, 26, 21, 3],
    "screenshot rows (49.84 / 25.77 / 21.06 / 3.33)");
Check([Hms(1, 2, 3)], [100], "a single row is 100");
Check([], [], "no rows shows nothing");
Check([TimeSpan.Zero, TimeSpan.Zero], [], "zero total shows nothing");
Check([Hms(4, 0, 0), TimeSpan.FromSeconds(5)], [100, 0], "a 5-second row next to 4 hours rounds to 0");
Check([Min(99), Min(1)], [99, 1], "exact shares are untouched");
Check([Min(10), Min(30), Min(30)], [14, 43, 43], "largest fractions get the leftovers (14.29 / 42.86 / 42.86)");
Check([Min(1), Min(1), Min(1), Min(1), Min(1), Min(1), Min(1)], [15, 15, 14, 14, 14, 14, 14],
    "equal sevenths: leftover points go to the earliest rows");
Check([Min(1), Min(2), Min(1), Min(2), Min(1), Min(2)], [11, 23, 11, 22, 11, 22],
    "remainder decides before size (22.22 beats 11.11), then row order among equals");
Check([Min(1), Min(3), Min(196)], [0, 2, 98], "equal .5 remainders: the larger part wins the point (0.5 / 1.5 / 98)");
Check([Min(3), Min(3), Min(1)], [43, 43, 14], "3/7, 3/7, 1/7 -> 42.86 / 42.86 / 14.29");
Check([Min(10), -Min(5)], [100, 0], "negative parts count as zero");

var many = Enumerable.Range(1, 37).Select(i => TimeSpan.FromSeconds(i * 97 + 13)).ToArray();
var manyShares = PercentBreakdown.Shares(many);
if (manyShares.Sum() == 100 && manyShares.Count == many.Length)
    Console.WriteLine("PASS  37 uneven rows still add up to 100");
else
{
    failures.Add("37 uneven rows");
    Console.WriteLine($"FAIL  37 uneven rows sum to {manyShares.Sum()}");
}

var again = PercentBreakdown.Shares(many);
if (again.SequenceEqual(manyShares))
    Console.WriteLine("PASS  same input gives the same split");
else
{
    failures.Add("deterministic");
    Console.WriteLine("FAIL  same input gave a different split");
}

if (PercentBreakdown.Format(50) == "50%")
    Console.WriteLine("PASS  format reads 50%");
else
{
    failures.Add("format");
    Console.WriteLine($"FAIL  format: {PercentBreakdown.Format(50)}");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All percent breakdown checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

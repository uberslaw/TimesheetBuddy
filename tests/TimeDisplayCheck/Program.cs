using TimesheetBuddy.Services;

var failures = new List<string>();

void Check(TimeSpan value, string expected, string message)
{
    var actual = DayTotals.FormatCompact(value);
    if (actual == expected)
    {
        Console.WriteLine($"PASS  {message} ({expected})");
        return;
    }

    failures.Add(message);
    Console.WriteLine($"FAIL  {message}: expected {expected}, got {actual}");
}

static TimeSpan Hms(int h, int m, int s) => new(h, m, s);

Check(Hms(1, 5, 3), "1:05:03", "hours drop their leading zero");
Check(Hms(12, 0, 7), "12:00:07", "two-digit hours stay whole");
Check(Hms(0, 5, 3), "5:03", "zero hours are hidden");
Check(Hms(0, 45, 0), "45:00", "whole minutes keep :00 seconds");
Check(Hms(0, 0, 14), "14", "zero hours and minutes leave bare seconds");
Check(Hms(0, 0, 7), "7", "single seconds drop the leading zero");
Check(TimeSpan.Zero, "0", "zero reads 0");
Check(TimeSpan.FromSeconds(59), "59", "59 seconds");
Check(TimeSpan.FromSeconds(60), "1:00", "60 seconds is one minute");
Check(TimeSpan.FromSeconds(3599), "59:59", "3599 seconds");
Check(TimeSpan.FromSeconds(3600), "1:00:00", "3600 seconds is one hour");
Check(Hms(26, 10, 0), "26:10:00", "over a day keeps total hours");
Check(TimeSpan.FromMilliseconds(14_900), "14", "fractions of a second are dropped, not rounded");
Check(-Hms(0, 5, 3), "-5:03", "negative minutes keep the minus");
Check(-Hms(1, 0, 2), "-1:00:02", "negative hours keep the minus");
Check(-TimeSpan.FromSeconds(9), "-9", "negative seconds keep the minus");
Check(-TimeSpan.FromMilliseconds(400), "0", "under a second negative still reads 0");
Check(DayTotals.TimeRemaining(TimeSpan.FromHours(8)), "0", "remaining over 7.5h floors at 0");
Check(DayTotals.TimeRemaining(Hms(2, 0, 30)), "5:29:30", "remaining shows seconds");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All time display checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

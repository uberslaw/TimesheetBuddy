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

// Ticket prefix: RITM or INC followed by a digit, any case, surrounding spaces ignored.
Check(TicketRules.Prefix("INC0012345") == "INC", "INC0012345 is an INC ticket");
Check(TicketRules.Prefix("RITM0012345") == "RITM", "RITM0012345 is a RITM ticket");
Check(TicketRules.Prefix("inc0012345") == "INC", "lowercase inc0012345 matches");
Check(TicketRules.Prefix("ritm0012345") == "RITM", "lowercase ritm0012345 matches");
Check(TicketRules.Prefix("   INC0012345  ") == "INC", "leading and trailing spaces are ignored");
Check(TicketRules.Prefix("INC1") == "INC", "one digit after the prefix is enough");
Check(TicketRules.Prefix("INCOMING") is null, "INCOMING is not a ticket");
Check(TicketRules.Prefix("RITMX12") is null, "RITM followed by a letter is not a ticket");
Check(TicketRules.Prefix("INC") is null, "the bare prefix is not a ticket");
Check(TicketRules.Prefix("RITM") is null, "the bare RITM prefix is not a ticket");
Check(TicketRules.Prefix("REQ123") is null, "REQ123 is not a ticket");
Check(TicketRules.Prefix("CHG0012345") is null, "other ServiceNow prefixes are not matched");
Check(TicketRules.Prefix("") is null && TicketRules.Prefix(null) is null && TicketRules.Prefix("   ") is null,
    "empty text is not a ticket");
Check(TicketRules.Prefix("X INC0012345") is null, "the prefix must be at the start");
Check(TicketRules.IsServiceNowTicket("INC0012345") && !TicketRules.IsServiceNowTicket("INCOMING"),
    "IsServiceNowTicket follows Prefix");

// Job code: Code "03" first.
var admin = Code("ADMIN", "Administration");
var exact = Code("03", "Something else");
var suffix03 = Code("089082-03", "SERVICE-NOW - Tickets");
var otherSuffix03 = Code("077000-03", "TRAINING");
var named = Code("SN", "Service Now work");
var spaced = Code("SVC", "service   now");
var hyphen = Code("SERVICE-NOW", "");
var glued = Code("X1", "ServiceNow requests");

Check(TicketRules.FindServiceNowCode([admin, suffix03, exact, named]) == exact, "Code \"03\" wins over every fallback");
Check(TicketRules.FindServiceNowCode([admin, otherSuffix03, suffix03]) == suffix03,
    "with several codes ending in -03, the one named ServiceNow wins");
Check(TicketRules.FindServiceNowCode([admin, otherSuffix03]) == otherSuffix03, "a code ending in -03 is used when it is the only one");
Check(suffix03.Display == "03 — SERVICE-NOW", "the suffix code shows as 03 — SERVICE-NOW");
Check(TicketRules.FindServiceNowCode([admin, named]) == named, "description SERVICE NOW with a space matches");
Check(TicketRules.FindServiceNowCode([admin, spaced]) == spaced, "extra spaces and lowercase still match");
Check(TicketRules.FindServiceNowCode([admin, hyphen]) == hyphen, "Code SERVICE-NOW matches");
Check(TicketRules.FindServiceNowCode([admin, glued]) == glued, "SERVICENOW written as one word matches");
Check(TicketRules.FindServiceNowCode([admin, Code("SUP", "Support / tickets")]) is null, "no ServiceNow code means no match");
Check(TicketRules.FindServiceNowCode([]) is null, "an empty list has no match");
Check(TicketRules.FindServiceNowCode([Code("0300", "Other")]) is null, "0300 is not 03");

// Category.
Check(TicketRules.FindSupportCategory(["General", "support", "Admin"]) == "support", "Support matches its existing casing");
Check(TicketRules.FindSupportCategory(["General", "Admin"]) is null, "no Support category returns null");

// Labels.
Check(TicketRules.Label("INC0012345", "Reset password") == "INC0012345 · Reset password", "ticket · task name");
Check(TicketRules.Label("", "Reset password") == "Reset password", "no ticket shows just the name");
Check(TicketRules.Label(" INC0012345 ", "") == "INC0012345", "no name shows just the ticket");
Check(TicketRules.Label("INC0012345", "inc0012345") == "INC0012345", "a name equal to the ticket is not repeated");
Check(TimerWidgetLayout.TaskLine(true, "Reset password", ticket: "RITM0012345") == "RITM0012345 · Reset password",
    "widget task line shows ticket · task name");
Check(TimerWidgetLayout.TaskLine(true, "Reset password") == "Reset password", "widget task line without a ticket is unchanged");

// Task-type key: the ticket splits rows; without one the key is unchanged.
var dev = Guid.NewGuid();
Check(TaskTypeSummary.KeyOf("Call", "Support", dev, "DEV") == TaskTypeSummary.KeyOf("Call", "Support", dev, "DEV", ""),
    "an empty ticket keeps the old key");
Check(TaskTypeSummary.KeyOf("Call", "Support", dev, "DEV", "INC1") != TaskTypeSummary.KeyOf("Call", "Support", dev, "DEV", "INC2"),
    "different tickets make different keys");
Check(TaskTypeSummary.KeyOf("Call", "Support", dev, "DEV", "inc1 ") == TaskTypeSummary.KeyOf("Call", "Support", dev, "DEV", "INC1"),
    "ticket compares ignoring case and spaces");

var day = new DateTime(2026, 11, 11);
var clock = Local(2026, 11, 11, 15);
var rows = TaskTypeSummary.Build(
[
    Entry("Reset password", "INC0012345", Local(2026, 11, 11, 9), Local(2026, 11, 11, 9, 30)),
    Entry("Reset password", "INC0099999", Local(2026, 11, 11, 10), Local(2026, 11, 11, 10, 15)),
    Entry("Reset password", "inc0012345", Local(2026, 11, 11, 11), Local(2026, 11, 11, 11, 10)),
    Entry("Reset password", "", Local(2026, 11, 11, 12), Local(2026, 11, 11, 12, 5))
], day, clock);
Check(rows.Count == 3, "same task name with two tickets and none makes three rows");
var first = rows.Single(r => r.Ticket.Equals("INC0012345", StringComparison.OrdinalIgnoreCase));
Check(first.SegmentCount == 2 && first.Total == TimeSpan.FromMinutes(40), "same ticket merges into one row");
Check(rows.Any(r => r.Ticket == "") && rows.Any(r => r.Ticket == "INC0099999"), "rows expose their ticket");

var session = new ActiveSession
{
    TaskName = "Reset password",
    Category = "Support",
    JobCodeId = dev,
    JobCodeText = "DEV",
    Ticket = "INC0099999"
};
Check(TaskTypeSummary.KeyOf(session) == rows.Single(r => r.Ticket == "INC0099999").Key, "session key matches its ticket row");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All ticket rule checks passed.");
    return 0;
}

Console.WriteLine($"{failures.Count} check(s) failed.");
return 1;

static JobCode Code(string code, string description) => new() { Code = code, Description = description };

static DateTime Local(int year, int month, int day, int hour, int minute = 0) =>
    new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local).ToUniversalTime();

TimeEntry Entry(string task, string ticket, DateTime startUtc, DateTime endUtc) => new()
{
    TaskName = task,
    Category = "Support",
    JobCodeId = dev,
    JobCodeText = "DEV",
    Ticket = ticket,
    StartUtc = startUtc,
    EndUtc = endUtc
};

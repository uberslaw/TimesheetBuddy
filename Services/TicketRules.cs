using System.Text;
using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// ServiceNow ticket rules for the main window's Ticket box (no WPF types, so checks can run anywhere).
/// </summary>
public static class TicketRules
{
    public const string ServiceNowCode = "03";
    public const string SupportCategory = "Support";
    public const string Separator = " · ";

    private static readonly string[] Prefixes = ["RITM", "INC"];

    /// <summary>
    /// "RITM" or "INC" when the trimmed text starts with that prefix (any case) followed by a digit,
    /// e.g. <c>RITM0012345</c> or <c>inc0012345</c>; otherwise null (<c>INCOMING</c>, <c>REQ123</c>, <c>INC</c>).
    /// </summary>
    public static string? Prefix(string? ticket)
    {
        var text = (ticket ?? string.Empty).Trim();
        foreach (var prefix in Prefixes)
        {
            if (text.Length > prefix.Length &&
                text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                char.IsAsciiDigit(text[prefix.Length]))
                return prefix;
        }

        return null;
    }

    public static bool IsServiceNowTicket(string? ticket) => Prefix(ticket) is not null;

    public static string Normalize(string? ticket) => (ticket ?? string.Empty).Trim();

    /// <summary>
    /// The code a ticket goes to: Code "03" exactly; else a code whose trailing part is "03"
    /// (shown as "03 — …"), preferring one named ServiceNow; else any code whose Code or
    /// Description contains SERVICE NOW / SERVICE-NOW / SERVICENOW (case, spaces and hyphens ignored).
    /// </summary>
    public static JobCode? FindServiceNowCode(IEnumerable<JobCode> codes)
    {
        var list = codes.Where(c => c is not null).ToList();

        var exact = list.FirstOrDefault(c => string.Equals(c.Code?.Trim(), ServiceNowCode, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var bySuffix = list.Where(c => JobCode.Suffix(c.Code) == ServiceNowCode).ToList();
        if (bySuffix.Count > 0)
            return bySuffix.FirstOrDefault(IsNamedServiceNow) ?? bySuffix[0];

        return list.FirstOrDefault(IsNamedServiceNow);
    }

    public static bool IsNamedServiceNow(JobCode code) =>
        Squash(code.Code).Contains("SERVICENOW", StringComparison.Ordinal) ||
        Squash(code.Description).Contains("SERVICENOW", StringComparison.Ordinal);

    /// <summary>The existing category matching "Support" (any case), or null when there is none.</summary>
    public static string? FindSupportCategory(IEnumerable<string> categories) =>
        categories.FirstOrDefault(c => string.Equals(c?.Trim(), SupportCategory, StringComparison.OrdinalIgnoreCase));

    /// <summary>"INC0012345 · task name"; just the name without a ticket, just the ticket without a name.</summary>
    public static string Label(string? ticket, string? taskName)
    {
        var t = Normalize(ticket);
        var name = (taskName ?? string.Empty).Trim();
        if (t.Length == 0) return name;
        if (name.Length == 0 || string.Equals(name, t, StringComparison.OrdinalIgnoreCase)) return t;
        return t + Separator + name;
    }

    private static string Squash(string? text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text ?? string.Empty)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }
}

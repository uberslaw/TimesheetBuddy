using System.Text;

namespace TimesheetBuddy.Models;

public sealed class JobCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Compact label: code suffix (the unique trailing part) + ALL-CAPS name from the description.
    /// Example: 089082-04 / "CS OFFICE SUPPORT - Admin tasks" → "04 — CS OFFICE SUPPORT"
    /// </summary>
    public string Display => FormatLabel(Code, Description);

    public override string ToString() => Display;

    public static string FormatLabel(string? code, string? description)
    {
        var suffix = Suffix(code);
        var name = CapName(description);

        if (suffix.Length > 0 && name.Length > 0)
            return $"{suffix} — {name}";
        if (suffix.Length > 0)
            return suffix;
        if (name.Length > 0)
            return name;

        var fallback = (code ?? string.Empty).Trim();
        return fallback.Length > 0 ? fallback : "—";
    }

    /// <summary>
    /// Formats a stored display string such as "089082-04 — CS OFFICE SUPPORT - Admin tasks".
    /// </summary>
    public static string FormatLabelFromStored(string? jobCodeText)
    {
        var text = (jobCodeText ?? string.Empty).Trim();
        if (text.Length == 0)
            return "—";

        // Prefer splitting on an em dash used by older Display values.
        var sep = text.IndexOf('—');
        if (sep < 0)
            sep = text.IndexOf('–');

        if (sep > 0)
        {
            var left = text[..sep].Trim();
            var right = text[(sep + 1)..].Trim();
            return FormatLabel(left, right);
        }

        // Already short, or code-only.
        if (text.Contains('-') && !text.Contains(' '))
            return Suffix(text);

        return text.Length <= 40 ? text : text[..39] + "…";
    }

    public static string Suffix(string? code)
    {
        var value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        var i = value.LastIndexOf('-');
        if (i >= 0 && i < value.Length - 1)
            return value[(i + 1)..].Trim();

        return value;
    }

    /// <summary>
    /// Leading ALL-CAPS title from a description ("CS OFFICE SUPPORT - Admin…" → "CS OFFICE SUPPORT").
    /// </summary>
    public static string CapName(string? description)
    {
        var value = (description ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        // Common pattern: "NAME - detail"
        var dash = value.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            var left = value[..dash].Trim();
            if (IsMostlyUpper(left))
                return left;
        }

        // Otherwise take the leading uppercase run of words.
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var taken = new StringBuilder();
        foreach (var part in parts)
        {
            if (!IsMostlyUpper(part) && taken.Length > 0)
                break;
            if (!IsMostlyUpper(part))
                break;
            if (taken.Length > 0) taken.Append(' ');
            taken.Append(part);
        }

        return taken.ToString();
    }

    private static bool IsMostlyUpper(string token)
    {
        var letters = 0;
        var upper = 0;
        foreach (var ch in token)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (char.IsUpper(ch)) upper++;
        }

        return letters > 0 && upper == letters;
    }
}

namespace TimesheetBuddy.Services;

public readonly record struct WidgetArea(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;

    public bool Intersects(double left, double top, double width, double height) =>
        left < Right && left + width > Left && top < Bottom && top + height > Top;
}

/// <summary>Text and placement rules for the minimized timer widget (no WPF types, so checks can run anywhere).</summary>
public static class TimerWidgetLayout
{
    public const string IdleHeader = "Idle";
    public const string IdleTask = "No active task";
    public const string NoJobCode = "No job code";

    public static string HeaderLine(bool running, string? jobCode, string? jobCodeText, string? category)
    {
        if (!running)
            return IdleHeader;

        var code = ShortCode(jobCode, jobCodeText);
        var cat = (category ?? string.Empty).Trim();
        if (code.Length == 0)
            code = NoJobCode;
        return cat.Length == 0 ? code : $"{code} - {cat}";
    }

    public static string TaskLine(bool running, string? taskName)
    {
        if (!running)
            return IdleTask;
        var name = (taskName ?? string.Empty).Trim();
        return name.Length > 0 ? name : "Untitled task";
    }

    /// <summary>
    /// The catalog code when known; otherwise the code part of a stored label such as "04 — CS OFFICE SUPPORT".
    /// </summary>
    public static string ShortCode(string? jobCode, string? jobCodeText)
    {
        var code = (jobCode ?? string.Empty).Trim();
        if (code.Length > 0)
            return code;

        var text = (jobCodeText ?? string.Empty).Trim();
        var sep = text.IndexOf('—');
        if (sep < 0)
            sep = text.IndexOf('–');
        if (sep >= 0)
            text = text[..sep].Trim();
        return text;
    }

    public static (double Left, double Top) DefaultPosition(WidgetArea work, double width, double height, double margin = 12) =>
        Clamp(work.Right - width - margin, work.Bottom - height - margin, width, height, work);

    /// <summary>
    /// Saved position if it is still reachable, pulled fully onto the screen; otherwise bottom-right of the primary work area.
    /// A widget touching the primary work area (or any widget on a single monitor) is kept clear of the taskbar.
    /// </summary>
    public static (double Left, double Top) Resolve(
        double? savedLeft,
        double? savedTop,
        double width,
        double height,
        WidgetArea primaryWork,
        WidgetArea virtualScreen,
        bool singleMonitor)
    {
        if (savedLeft is not double x || savedTop is not double y || !double.IsFinite(x) || !double.IsFinite(y))
            return DefaultPosition(primaryWork, width, height);

        if (singleMonitor || primaryWork.Intersects(x, y, width, height))
            return Clamp(x, y, width, height, primaryWork);

        if (!virtualScreen.Intersects(x, y, width, height))
            return DefaultPosition(primaryWork, width, height);

        return Clamp(x, y, width, height, virtualScreen);
    }

    public static (double Left, double Top) Clamp(double left, double top, double width, double height, WidgetArea area)
    {
        var x = Math.Max(area.Left, Math.Min(left, area.Right - width));
        var y = Math.Max(area.Top, Math.Min(top, area.Bottom - height));
        return (x, y);
    }
}

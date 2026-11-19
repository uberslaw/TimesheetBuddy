namespace TimesheetBuddy.Services;

public readonly record struct WidgetArea(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;

    public bool Intersects(double left, double top, double width, double height) =>
        left < Right && left + width > Left && top < Bottom && top + height > Top;
}

public enum WidgetCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>Text and placement rules for the minimized timer widget (no WPF types, so checks can run anywhere).</summary>
public static class TimerWidgetLayout
{
    public const string IdleHeader = "Idle";
    public const string IdleTask = "No active task";
    public const string NoTaskTask = "No Task Selected";
    public const string NoJobCode = "No job code";
    public const double EdgeMargin = 12;

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

    public static string TaskLine(bool running, string? taskName, bool noTaskRunning = false)
    {
        if (!running)
            return noTaskRunning ? NoTaskTask : IdleTask;
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

    public static (double Left, double Top) DefaultPosition(WidgetArea work, double width, double height, double margin = EdgeMargin) =>
        CornerPosition(work, width, height, WidgetCorner.BottomRight, margin);

    /// <summary>
    /// Docks into a corner of a monitor's work area (so a bottom corner sits above the taskbar),
    /// <paramref name="margin"/> in from both edges, never past the area when it is smaller than the widget.
    /// </summary>
    public static (double Left, double Top) CornerPosition(
        WidgetArea work,
        double width,
        double height,
        WidgetCorner corner,
        double margin = EdgeMargin)
    {
        var left = corner is WidgetCorner.TopLeft or WidgetCorner.BottomLeft
            ? work.Left + margin
            : work.Right - width - margin;
        var top = corner is WidgetCorner.TopLeft or WidgetCorner.TopRight
            ? work.Top + margin
            : work.Bottom - height - margin;
        return Clamp(left, top, width, height, work);
    }

    /// <summary>Converts a work area in device pixels (Win32) to WPF units with the window's device-to-DIP scale.</summary>
    public static WidgetArea FromDevice(WidgetArea pixels, double scaleX, double scaleY)
    {
        if (!double.IsFinite(scaleX) || scaleX <= 0) scaleX = 1;
        if (!double.IsFinite(scaleY) || scaleY <= 0) scaleY = 1;
        return new WidgetArea(pixels.Left * scaleX, pixels.Top * scaleY, pixels.Right * scaleX, pixels.Bottom * scaleY);
    }

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

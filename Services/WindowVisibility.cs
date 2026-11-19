using System.Runtime.InteropServices;
using System.Text;

namespace TimesheetBuddy.Services;

/// <summary>
/// Win32 probes behind <see cref="WidgetVisibilityRules"/>: how much of a window other apps cover,
/// and whether it sits on the primary monitor. Every call is cheap and never touches z-order.
/// </summary>
public static class WindowVisibility
{
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExLayered = 0x00080000;
    private const long WsExTransparent = 0x00000020;
    private const uint LwaAlpha = 0x2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const uint MonitorDefaultToPrimary = 1;
    private const int MaxWindowsWalked = 1024;

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
    };

    /// <summary>
    /// Share of <paramref name="hwnd"/> covered by other apps' visible windows above it in z-order.
    /// Stops walking as soon as <paramref name="stopAt"/> is reached.
    /// </summary>
    public static double CoveredFraction(IntPtr hwnd, double stopAt = WidgetVisibilityRules.CoveredThreshold)
    {
        if (hwnd == IntPtr.Zero || !TryGetBounds(hwnd, out var target))
            return 0;

        var coverage = new CoverageAccumulator(target);
        var className = new StringBuilder(64);
        var walked = 0;
        for (var above = GetWindow(hwnd, GwHwndPrev);
             above != IntPtr.Zero && walked < MaxWindowsWalked;
             above = GetWindow(above, GwHwndPrev), walked++)
        {
            if (!CountsAsCover(above, className) || !TryGetBounds(above, out var rect))
                continue;
            if (!target.Intersects(rect.Left, rect.Top, rect.Width, rect.Height))
                continue;

            coverage.Add(rect);
            if (coverage.CoveredFraction >= stopAt)
                break;
        }

        return coverage.CoveredFraction;
    }

    /// <summary>
    /// True when the centre of <paramref name="hwnd"/> lies on the primary monitor (the one at the origin).
    /// GetWindowRect and GetMonitorInfo share the calling thread's DPI coordinate space, so no scaling is needed.
    /// Unknown answers count as on the primary, so a failed probe never shows the widget.
    /// </summary>
    public static bool IsOnPrimaryMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var w))
            return true;

        var primary = MonitorFromPoint(default, MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (primary == IntPtr.Zero || !GetMonitorInfo(primary, ref info))
            return true;

        var m = info.Monitor;
        return WidgetVisibilityRules.CentreIsOn(
            new WidgetArea(w.Left, w.Top, w.Right, w.Bottom),
            new WidgetArea(m.Left, m.Top, m.Right, m.Bottom));
    }

    private static bool CountsAsCover(IntPtr hwnd, StringBuilder className)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;

        // Our widget, dialogs and the job-code view are never "other apps".
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnProcessId)
            return false;

        // Suspended UWP apps and windows on other virtual desktops report visible but are cloaked.
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var ex = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((ex & WsExToolWindow) != 0)
            return false;
        if ((ex & WsExLayered) != 0)
        {
            // Click-through or fully transparent overlays (game bars, screen dimmers) hide nothing.
            if ((ex & WsExTransparent) != 0)
                return false;
            if (GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags) && (flags & LwaAlpha) != 0 && alpha == 0)
                return false;
        }

        className.Clear();
        return GetClassName(hwnd, className, className.Capacity) <= 0 || !ShellClasses.Contains(className.ToString());
    }

    /// <summary>Visible frame (DWM bounds skip the invisible resize border); falls back to GetWindowRect.</summary>
    private static bool TryGetBounds(IntPtr hwnd, out WidgetArea area)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out NativeRect r, Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(hwnd, out r))
        {
            area = default;
            return false;
        }

        area = new WidgetArea(r.Left, r.Top, r.Right, r.Bottom);
        return area.Width > 0 && area.Height > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);
}

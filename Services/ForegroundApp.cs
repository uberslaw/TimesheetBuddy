using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TimesheetBuddy.Services;

/// <summary>
/// Win32 probes for App Track: which program owns the foreground window, and how long since the
/// last keyboard or mouse input. Only the process's exe path is read, never the window title.
/// </summary>
public static class ForegroundApp
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxPath = 1024;

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    public static readonly AppIdentity Self = new(AppNames.SelfExe, AppNames.Self);

    /// <summary>
    /// The foreground program, or null when there is no foreground window. Timesheet Buddy's own
    /// windows count as <see cref="Self"/>. A process that cannot be opened (elevated) falls back
    /// to its process name, then to "Unknown app".
    /// </summary>
    public static AppIdentity? Current(AppNameCache names)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return null;
        if (pid == OwnProcessId) return Self;

        var path = ImagePath(pid);
        return names.Identify(path, path is null ? ProcessName(pid) : null);
    }

    /// <summary>File description and product name from the exe's version resource.</summary>
    public static (string? Description, string? Product) Describe(string exePath)
    {
        var info = FileVersionInfo.GetVersionInfo(exePath);
        return (info.FileDescription, info.ProductName);
    }

    /// <summary>Time since the last keyboard or mouse input in this session; zero if unknown.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        var elapsedMs = unchecked((uint)Environment.TickCount - info.Time);
        return TimeSpan.FromMilliseconds(elapsedMs);
    }

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(MaxPath);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0
                ? buffer.ToString(0, size)
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

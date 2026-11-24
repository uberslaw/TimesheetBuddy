using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TimesheetBuddy.Services;

namespace TimesheetBuddy;

public partial class App : Application
{
    public static AppState State { get; private set; } = null!;
    public static PromptScheduler Scheduler { get; private set; } = null!;
    public static SettingsStore Store { get; private set; } = null!;
    public static AppTracker AppTracker { get; private set; } = null!;
    public static ClientIdentity Client { get; private set; } = null!;
    public static ChangeJournal Journal { get; private set; } = null!;

    /// <summary>Always in the default folder so it works when a custom data folder is unavailable.</summary>
    public static string CrashLogPath { get; } = Path.Combine(DataFolder.DefaultFolder, "crash.log");

    /// <summary>Starts, exits and their reasons, beside crash.log.</summary>
    public static string ExitLogPath { get; } = Path.Combine(DataFolder.DefaultFolder, ExitLog.FileName);

    /// <summary>Saved custom data folder that could not be used this session, if any.</summary>
    public static string? UnavailableDataFolder { get; private set; }

    /// <summary>
    /// Set before any shutdown the app starts itself or Windows asks for (sign-out, shutdown, close request,
    /// restart, fatal error). While set, closing the main window never asks for confirmation.
    /// </summary>
    internal static string? ProgrammaticExitReason { get; private set; }

    private static readonly int Pid = Environment.ProcessId;
    private static bool _exitLogged;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            if (ex is not null)
                WriteCrashLog(ex);
            var what = ex is null ? "unknown error" : $"{ex.GetType().Name}: {ex.Message}";
            if (args.IsTerminating)
                LogExit($"unhandled exception on a background thread: {what}");
            else
                LogLine(ExitLog.Kinds.Error, $"unhandled exception on a background thread: {what}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            LogLine(ExitLog.Kinds.Error, $"unobserved task exception: {args.Exception.GetBaseException().Message}");
            args.SetObserved();
        };

        foreach (var lost in ExitLog.UnfinishedStarts(ExitLog.Read(ExitLogPath), Pid))
        {
            if (IsRunning(lost.Pid)) continue;
            ExitLog.Append(ExitLogPath, ExitLog.Format(DateTime.Now, lost.Pid, ExitLog.Kinds.Exit,
                $"no exit was recorded for the session started {lost.TimeLocal:yyyy-MM-dd HH:mm:ss}: the process was ended " +
                "without running its exit code (killed, power loss, or a crash that could not be logged)"));
        }

        LogLine(ExitLog.Kinds.Start, $"Timesheet Buddy {typeof(App).Assembly.GetName().Version}");

        base.OnStartup(e);

        try
        {
            var data = DataFolder.Resolve(DataFolder.DefaultFolder);
            UnavailableDataFolder = data.UnavailableFolder;
            Client = ClientIdentity.LoadOrCreate(DataFolder.DefaultFolder);
            Store = new SettingsStore(data.Folder) { Client = Client };
            Journal = new ChangeJournal(() => Store.Folder, Client);
            State = new AppState(Store) { Journal = Journal };
            Journal.PruneOwn();
            Journal.PruneAbandoned();
            Scheduler = new PromptScheduler(State);
            State.RestoreSessionIfAny();
            AppTracker = new AppTracker(() => Store.Folder);
            if (State.Settings.AppTrackEnabled)
                AppTracker.Start();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                () => StartPresence(() => window.IsScreenLocked));

            if (UnavailableDataFolder is not null)
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                    MessageBox.Show(window,
                        "Timesheet Buddy could not open your data folder:\n" + UnavailableDataFolder +
                        "\n\nIt may be on a drive that is not connected. This session uses the default folder instead:\n" +
                        data.Folder +
                        "\n\nYour folder choice is kept, so the next start uses it again once it is available.",
                        "Timesheet Buddy",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning));
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            BeginProgrammaticExit($"could not start: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(
                "Timesheet Buddy could not start.\n\n" + ex.Message +
                "\n\nDetails were written to:\n" + CrashLogPath,
                "Timesheet Buddy",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        var what = $"{e.Exception.GetType().Name}: {e.Exception.Message}";
        LogLine(ExitLog.Kinds.Error, $"unhandled UI exception, error box shown: {what}");
        MessageBox.Show(
            "Timesheet Buddy hit an unexpected error.\n\n" + e.Exception.Message +
            "\n\nDetails were written to:\n" + CrashLogPath,
            "Timesheet Buddy",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        if (Windows.Count == 0)
        {
            BeginProgrammaticExit($"unhandled UI exception with no window left: {what}");
            Shutdown(1);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        BeginProgrammaticExit(e.ReasonSessionEnding == ReasonSessionEnding.Logoff
            ? ExitLog.Reasons.WindowsLogoff
            : ExitLog.Reasons.WindowsShutdown);
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!ClosingWithoutSave)
            AppTracker?.Stop();
        Presence?.Stop(deleteFile: true);

        LogExit(ProgrammaticExitReason ?? ExitLog.Reasons.UnknownShutdown);
        LogLine(ExitLog.Kinds.Info, $"exit code {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    /// <summary>
    /// Marks the coming shutdown as not user-initiated (so the main window closes without asking) and logs why.
    /// The first reason wins.
    /// </summary>
    internal static void BeginProgrammaticExit(string reason)
    {
        ProgrammaticExitReason ??= reason;
        LogExit(reason);
    }

    /// <summary>Writes this process's exit line once; later calls are ignored.</summary>
    internal static void LogExit(string reason)
    {
        if (_exitLogged) return;
        _exitLogged = true;
        LogLine(ExitLog.Kinds.Exit, reason);
    }

    internal static void LogLine(string kind, string text) =>
        ExitLog.Append(ExitLogPath, ExitLog.Format(DateTime.Now, Pid, kind, text));

    /// <summary>
    /// True once WPF itself is shutting down (<see cref="Application.Shutdown()"/> from any path): windows then
    /// close with their Cancel ignored, so asking would be pointless. Read from WPF's internal flag; false if
    /// it can't be read.
    /// </summary>
    internal static bool IsShuttingDown()
    {
        if (Current?.Dispatcher is { HasShutdownStarted: true }) return true;
        try
        {
            var property = typeof(Application).GetProperty("IsShuttingDown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
            var value = property?.GetMethod?.IsStatic == true ? property.GetValue(null) : property?.GetValue(Current);
            return value is true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited &&
                   string.Equals(process.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void ClearUnavailableDataFolder() => UnavailableDataFolder = null;

    /// <summary>Turns App Track on or off now and saves the choice.</summary>
    internal static void SetAppTrack(bool enabled)
    {
        State.SetAppTrackEnabled(enabled);
        if (enabled)
            AppTracker.Start();
        else
            AppTracker.Stop();
    }

    /// <summary>Starts a fresh copy of the app, then closes this one (the main window saves on close, without asking).</summary>
    internal static void Restart()
    {
        BeginProgrammaticExit(ExitLog.Reasons.Restart);
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("The program path is unknown.");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show(
                "Timesheet Buddy could not restart itself. Please start it again.\n\n" + ex.Message,
                "Timesheet Buddy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        Current.Shutdown();
    }

    internal static void WriteCrashLog(Exception ex)
    {
        try
        {
            var folder = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.AppendAllText(
                CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // A logging failure must not replace the original crash.
        }
    }
}

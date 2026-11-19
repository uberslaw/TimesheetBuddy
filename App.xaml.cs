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

    /// <summary>Saved custom data folder that could not be used this session, if any.</summary>
    public static string? UnavailableDataFolder { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

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
        MessageBox.Show(
            "Timesheet Buddy hit an unexpected error.\n\n" + e.Exception.Message +
            "\n\nDetails were written to:\n" + CrashLogPath,
            "Timesheet Buddy",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        if (Windows.Count == 0)
            Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!ClosingWithoutSave)
            AppTracker?.Stop();
        Presence?.Stop(deleteFile: true);
        base.OnExit(e);
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

    /// <summary>Starts a fresh copy of the app, then closes this one (the main window saves on close).</summary>
    internal static void Restart()
    {
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

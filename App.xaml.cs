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

    public static string CrashLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimesheetBuddy",
        "crash.log");

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
            Store = new SettingsStore();
            State = new AppState(Store);
            Scheduler = new PromptScheduler(State);
            State.RestoreSessionIfAny();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
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

using System.Windows;
using TimesheetBuddy.Services;

namespace TimesheetBuddy;

public partial class App : Application
{
    public static AppState State { get; private set; } = null!;
    public static PromptScheduler Scheduler { get; private set; } = null!;
    public static SettingsStore Store { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Store = new SettingsStore();
        State = new AppState(Store);
        Scheduler = new PromptScheduler(State);
        State.RestoreSessionIfAny();
    }
}

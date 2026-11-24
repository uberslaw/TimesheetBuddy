using System.Windows;
using System.Windows.Threading;
using TimesheetBuddy.Services;
using TimesheetBuddy.Views;

namespace TimesheetBuddy;

/// <summary>
/// Two PCs open on one data folder: the "Are you active on this one?" popup, Keep / Discard after Yes, and the
/// close without saving when the other PC wins. Rules live in <see cref="OverlapRules"/>.
/// </summary>
public partial class App
{
    private static OverlapPromptWindow? _overlapPrompt;
    private static KeepDiscardWindow? _keepDiscard;

    public static PresenceMonitor? Presence { get; private set; }

    /// <summary>
    /// Set when another PC asked this one to close: nothing is saved from then on (no running-timer save on exit,
    /// no settings.json write, no App Track flush).
    /// </summary>
    internal static bool ClosingWithoutSave { get; private set; }

    internal static void StartPresence(Func<bool> isScreenLocked)
    {
        if (Presence is not null || ClosingWithoutSave) return;
        Presence = new PresenceMonitor(() => Store.Folder, Client, State) { IsScreenLocked = isScreenLocked };
        Presence.DecisionMade += OnOverlapDecision;
        Presence.Start();
    }

    /// <summary>After an unlock a held-back prompt shows at once instead of on the next heartbeat.</summary>
    internal static void OnScreenLockChanged(bool locked)
    {
        if (!locked) Presence?.Tick();
    }

    /// <summary>Closes the popup and the Keep / Discard window without answering (normal close of the app).</summary>
    internal static void CloseOverlapWindows()
    {
        if (_overlapPrompt is { } prompt)
        {
            _overlapPrompt = null;
            prompt.Dismiss();
        }

        if (Presence is not null) Presence.PromptOpen = false;

        if (_keepDiscard is { } keep)
        {
            _keepDiscard = null;
            keep.Dismiss();
            State.LogOverlap($"Keep or discard for {keep.Machine} closed unanswered: nothing kept");
        }
    }

    private static void OnOverlapDecision(OverlapRules.Decision decision)
    {
        if (ClosingWithoutSave) return;
        var machine = decision.Other?.Machine ?? "another PC";
        switch (decision.Kind)
        {
            case OverlapRules.Kind.ShowPrompt when decision.Other is not null:
                ShowOverlapPrompt(decision.Other);
                break;
            case OverlapRules.Kind.HidePrompt:
                CloseOverlapWindows();
                break;
            case OverlapRules.Kind.CloseNow:
                CloseWithoutSaving($"Closed without saving: {machine} confirmed it is the active PC", null);
                break;
            case OverlapRules.Kind.AlreadyConfirmedThenClose:
                CloseWithoutSaving($"Already confirmed on {machine}; closed without saving", $"Already confirmed on {machine}.");
                break;
        }
    }

    private static void ShowOverlapPrompt(SessionFile other)
    {
        if (_overlapPrompt is not null || Presence is null) return;
        var prompt = new OverlapPromptWindow(other);
        _overlapPrompt = prompt;
        Presence.PromptOpen = true;
        prompt.Answered += yes =>
        {
            if (_overlapPrompt == prompt) _overlapPrompt = null;
            if (Presence is not null) Presence.PromptOpen = false;
            if (ClosingWithoutSave) return;
            if (yes)
                ConfirmActive(other);
            else
                Presence?.Dismiss(other);
        };
        prompt.Show();
    }

    private static void ConfirmActive(SessionFile other)
    {
        if (Presence is null) return;
        var latest = Presence.Read(other.ClientId) ?? other;
        Presence.Confirm(latest);
        var items = OverlapMerge.Items(State.Settings, latest, DateTime.UtcNow);

        _keepDiscard?.Dismiss();
        var window = new KeepDiscardWindow(latest.Machine, items);
        _keepDiscard = window;
        window.Finished += keep =>
        {
            if (_keepDiscard == window) _keepDiscard = null;
            if (ClosingWithoutSave) return;
            State.KeepFromOtherPc(items, keep, latest.Machine);
        };
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Closes this app immediately without saving anything. Never waits on a dialog: open prompts are dismissed,
    /// <see cref="Application.Shutdown()"/> closes every window (ignoring their close guards), and if the process
    /// is still alive after <see cref="PresenceTiming.ForcedExitFallback"/> (a Win32 message box, say) it is ended.
    /// </summary>
    internal static void CloseWithoutSaving(string journalSummary, string? notice)
    {
        if (ClosingWithoutSave) return;
        ClosingWithoutSave = true;
        BeginProgrammaticExit("close request from another PC: " + journalSummary);
        Store.Suspend();
        State.LogOverlap(journalSummary);
        Presence?.Stop(deleteFile: true);
        CloseOverlapWindows();

        var delay = notice is null ? TimeSpan.Zero : PresenceTiming.AlreadyConfirmedNotice;
        var fallback = new Thread(() =>
        {
            Thread.Sleep(delay + PresenceTiming.ForcedExitFallback);
            LogLine(ExitLog.Kinds.Info, "close request: shutdown had not finished, process ended by the fallback timer");
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "Timesheet Buddy forced exit" };
        fallback.Start();

        if (notice is null)
        {
            Current.Shutdown();
            return;
        }

        OverlapPromptWindow.ShowNotice(notice, delay);
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Current.Shutdown();
        };
        timer.Start();
    }
}

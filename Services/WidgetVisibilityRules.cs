namespace TimesheetBuddy.Services;

/// <summary>Why the timer widget should be showing; <see cref="None"/> means hide it.</summary>
public enum WidgetShowReason
{
    None,
    Minimized,
    Hidden,
    Covered,
    OtherMonitor
}

/// <summary>What the main window looks like to the user right now (gathered by Services/WindowVisibility.cs).</summary>
public readonly record struct MainWindowSnapshot(
    bool Minimized,
    bool Hidden,
    bool Topmost,
    double CoveredFraction,
    bool OnPrimaryMonitor,
    bool ModalOpen = false);

/// <summary>Settings → Widget: the master switch plus the two "also when" options.</summary>
public readonly record struct WidgetVisibilityOptions(bool Enabled, bool WhenCovered, bool WhenOtherMonitor);

/// <summary>
/// Show/hide rules for the timer widget (no WPF or Win32 types, so checks can run anywhere).
/// The widget shows whenever the user cannot actually see the main window on the primary monitor.
/// </summary>
public static class WidgetVisibilityRules
{
    /// <summary>Share of the main window that other apps must cover before it counts as buried.</summary>
    public const double CoveredThreshold = 0.5;

    public static WidgetShowReason Decide(MainWindowSnapshot main, WidgetVisibilityOptions options, bool closing = false)
    {
        // A modal dialog (prompt, Settings) has the user's attention and disables the widget anyway.
        if (closing || !options.Enabled || main.ModalOpen)
            return WidgetShowReason.None;
        if (main.Minimized)
            return WidgetShowReason.Minimized;
        if (main.Hidden)
            return WidgetShowReason.Hidden;
        if (options.WhenOtherMonitor && !main.OnPrimaryMonitor)
            return WidgetShowReason.OtherMonitor;
        if (options.WhenCovered && !main.Topmost && main.CoveredFraction >= CoveredThreshold)
            return WidgetShowReason.Covered;
        return WidgetShowReason.None;
    }

    /// <summary>Minimize and hide are the user's own action, so they show the widget without waiting.</summary>
    public static bool ShowsImmediately(WidgetShowReason reason) =>
        reason is WidgetShowReason.Minimized or WidgetShowReason.Hidden;

    public static bool ContainsPoint(WidgetArea area, double x, double y) =>
        x >= area.Left && x < area.Right && y >= area.Top && y < area.Bottom;

    /// <summary>True when the centre of <paramref name="window"/> lies on <paramref name="monitor"/>.</summary>
    public static bool CentreIsOn(WidgetArea window, WidgetArea monitor) =>
        ContainsPoint(monitor, (window.Left + window.Right) / 2, (window.Top + window.Bottom) / 2);

    public static double CoveredFraction(WidgetArea target, IEnumerable<WidgetArea> covers)
    {
        var coverage = new CoverageAccumulator(target);
        foreach (var cover in covers)
            coverage.Add(cover);
        return coverage.CoveredFraction;
    }
}

/// <summary>
/// Exact share of a target rectangle covered by the union of rectangles added so far.
/// Keeps the uncovered part as disjoint pieces and cuts each new cover out of them, so overlapping
/// covers are never counted twice and the caller can stop as soon as the threshold is reached.
/// </summary>
public sealed class CoverageAccumulator
{
    private readonly double _targetArea;
    private List<WidgetArea> _uncovered;

    public CoverageAccumulator(WidgetArea target)
    {
        _targetArea = IsEmpty(target) ? 0 : target.Width * target.Height;
        _uncovered = _targetArea > 0 ? [target] : [];
    }

    public double CoveredFraction
    {
        get
        {
            if (_targetArea <= 0)
                return 0;
            var left = _uncovered.Sum(p => p.Width * p.Height);
            return Math.Clamp(1 - left / _targetArea, 0, 1);
        }
    }

    public void Add(WidgetArea cover)
    {
        if (IsEmpty(cover) || _uncovered.Count == 0)
            return;

        var next = new List<WidgetArea>(_uncovered.Count + 3);
        foreach (var piece in _uncovered)
        {
            var l = Math.Max(piece.Left, cover.Left);
            var t = Math.Max(piece.Top, cover.Top);
            var r = Math.Min(piece.Right, cover.Right);
            var b = Math.Min(piece.Bottom, cover.Bottom);
            if (l >= r || t >= b)
            {
                next.Add(piece);
                continue;
            }

            if (piece.Top < t) next.Add(new WidgetArea(piece.Left, piece.Top, piece.Right, t));
            if (b < piece.Bottom) next.Add(new WidgetArea(piece.Left, b, piece.Right, piece.Bottom));
            if (piece.Left < l) next.Add(new WidgetArea(piece.Left, t, l, b));
            if (r < piece.Right) next.Add(new WidgetArea(r, t, piece.Right, b));
        }

        _uncovered = next;
    }

    private static bool IsEmpty(WidgetArea a) =>
        !(a.Right > a.Left && a.Bottom > a.Top) ||
        !double.IsFinite(a.Left) || !double.IsFinite(a.Top) || !double.IsFinite(a.Right) || !double.IsFinite(a.Bottom);
}

/// <summary>
/// Hysteresis between the raw rule and the widget. Minimize and hide show at once and "nothing to show"
/// hides at once; covered or other-monitor must hold for <see cref="StablePollsToShow"/> polls in a row,
/// so a window passing briefly in front, or a drag across monitors, does not flash the widget.
/// </summary>
public sealed class WidgetVisibilityGate
{
    public const int StablePollsToShow = 2;
    private int _pendingPolls;

    public bool Shown { get; private set; }

    /// <param name="fromPoll">True on the one-second tick; window events never advance the delay.</param>
    public bool Update(WidgetShowReason reason, bool fromPoll)
    {
        if (reason == WidgetShowReason.None)
        {
            _pendingPolls = 0;
            Shown = false;
        }
        else if (WidgetVisibilityRules.ShowsImmediately(reason))
        {
            _pendingPolls = 0;
            Shown = true;
        }
        else if (!Shown && fromPoll && ++_pendingPolls >= StablePollsToShow)
        {
            _pendingPolls = 0;
            Shown = true;
        }

        return Shown;
    }

    public void Reset()
    {
        _pendingPolls = 0;
        Shown = false;
    }
}

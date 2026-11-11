using TimesheetBuddy.Models;

namespace TimesheetBuddy.Services;

/// <summary>
/// Remembers a lock that began while a timer was running and decides whether unlock should prompt.
/// No prompt when the timer was idle, the session changed, or the lock was under a minute.
/// </summary>
public sealed class ScreenLockTracker
{
    private LockIntervalSplit.Snapshot? _pending;

    public void NoteLock(ActiveSession? session, DateTime utcNow)
    {
        if (session is null)
        {
            _pending = null;
            return;
        }

        if (_pending is not null)
            return;

        _pending = LockIntervalSplit.Snapshot.FromSession(session, utcNow);
    }

    public bool TryTakeUnlock(
        ActiveSession? session,
        DateTime utcNow,
        out LockIntervalSplit.Snapshot? snapshot,
        out TimeSpan lockedFor)
    {
        snapshot = null;
        lockedFor = TimeSpan.Zero;
        if (_pending is null)
            return false;

        var pending = _pending;
        _pending = null;
        lockedFor = utcNow - pending.LockedAtUtc;
        if (!LockIntervalSplit.MeetsMinimumLock(lockedFor))
            return false;
        if (session is null || session.EntryId != pending.EntryId)
            return false;

        snapshot = pending;
        return true;
    }
}

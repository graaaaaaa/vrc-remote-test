namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// A line the Log Viewer publishes instead of a log while the Bridge itself replaces the
/// VRChat clients (Relaunch mode). Without it, the copy on the share kept the closed first
/// client's log until the first poll after the new first client started: 3.6 s after that
/// launch on real hardware (docs/validation/multi-client-spike.md, option C item 9), since
/// VrchatLogService only reads every 5 s.
///
/// The deploy flow only sets or clears the line; both wake VrchatLogService, which then
/// publishes at once instead of on its next poll. Writing the share stays with that service
/// alone, so only one SharedFileWriter replaces vrchat-latest.log, and a write that fails
/// never reaches the deploy. Thread-safe: the deploy flow and the Log Viewer run on
/// different hosted services.
/// </summary>
public sealed class VrchatLogNotice
{
    private readonly object _gate = new();

    /// <summary>At most one pending wake: VrchatLogService reads the current state when it wakes.</summary>
    private readonly SemaphoreSlim _changed = new(0, 1);

    private string? _message;

    /// <summary>The line to publish instead of the log; null publishes the log as usual.</summary>
    public string? Message
    {
        get
        {
            lock (_gate)
            {
                return _message;
            }
        }
    }

    public void Show(string message)
    {
        lock (_gate)
        {
            _message = message;
            WakeLocked();
        }
    }

    /// <summary>
    /// Wakes the Log Viewer only when a line was shown, so that clearing an absent line (as
    /// every build does when it ends) changes nothing.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_message is null)
            {
                return;
            }

            _message = null;
            WakeLocked();
        }
    }

    /// <summary>
    /// For VrchatLogService's single loop: true as soon as the line was shown or cleared since
    /// the previous call, false after <paramref name="timeout"/>.
    /// </summary>
    internal Task<bool> WaitForChangeAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _changed.WaitAsync(timeout, cancellationToken);

    private void WakeLocked()
    {
        if (_changed.CurrentCount == 0)
        {
            _changed.Release();
        }
    }
}

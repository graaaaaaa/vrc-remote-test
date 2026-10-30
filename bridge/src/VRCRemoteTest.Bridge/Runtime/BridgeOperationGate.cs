namespace VRCRemoteTest.Bridge.Runtime;

/// <summary>
/// The one operation at a time that changes what runs on Windows: a deploy (from the claim
/// of its manifest to its result), a configuration change, closing clients, or a restart.
/// It lives as long as the process, across host restarts. The VRChat coordinators assume a
/// single caller (VrchatClientCoordinator's client-mix note), which held while only
/// StagingWatcher's loop called them; the LAN API's management routes now call them too.
///
/// Nothing waits on the gate: a management request that finds it taken is answered 409
/// busy, and StagingWatcher leaves a committed build where it is until the next scan.
/// Once a restart is pending, nothing new enters until the next host has started.
/// </summary>
public sealed class BridgeOperationGate
{
    private readonly object _sync = new();
    private string? _current;
    private bool _restartPending;

    /// <summary>What holds the gate, or null; for the busy answer and /bridge.</summary>
    public string? Current
    {
        get { lock (_sync) return _current; }
    }

    public bool RestartPending
    {
        get { lock (_sync) return _restartPending; }
    }

    /// <summary>Null when another operation holds the gate or a restart is pending.</summary>
    public IDisposable? TryEnter(string operation)
    {
        lock (_sync)
        {
            if (_current is not null || _restartPending)
            {
                return null;
            }

            _current = operation;
            return new Lease(this, operation);
        }
    }

    /// <summary>
    /// Called by the holder of the gate when it schedules a restart: from then on nothing
    /// enters, including after the holder leaves, until <see cref="HostStarted"/>.
    /// </summary>
    public void MarkRestartPending()
    {
        lock (_sync)
        {
            _restartPending = true;
        }
    }

    /// <summary>A new host is serving: operations may enter again.</summary>
    public void HostStarted()
    {
        lock (_sync)
        {
            _restartPending = false;
        }
    }

    private void Leave(string operation)
    {
        lock (_sync)
        {
            if (_current == operation)
            {
                _current = null;
            }
        }
    }

    private sealed class Lease(BridgeOperationGate gate, string operation) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Leave(operation);
            }
        }
    }
}

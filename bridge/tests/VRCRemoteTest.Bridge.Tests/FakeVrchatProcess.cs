using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// One fake VRChat process for the Relaunch tests. Behaviour is set per process; time
/// comes from <see cref="Clock"/>, so a test's fake clock decides when a close request
/// takes effect. Handles are opened per enumeration (<see cref="Open"/>), like real ones,
/// so a test can tell which enumeration's handle was used.
/// </summary>
internal sealed class FakeVrchatProcess
{
    private DateTime? _exitAtUtc;
    private bool _killed;
    private bool _exitedByTest;

    public FakeVrchatProcess(int processId, DateTime creationTimeUtc, int sessionId = 1, string processName = "VRChat")
    {
        ProcessId = processId;
        CreationTimeUtc = creationTimeUtc;
        SessionId = sessionId;
        ProcessName = processName;
    }

    public int ProcessId { get; }

    public DateTime CreationTimeUtc { get; }

    public int SessionId { get; }

    public string ProcessName { get; }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>False while the window does not exist yet: CloseMainWindow then returns false.</summary>
    public bool HasWindow { get; set; } = true;

    /// <summary>The window appears after this many Refresh calls (when <see cref="HasWindow"/> is false).</summary>
    public int? RefreshesUntilWindow { get; set; }

    /// <summary>Time from a delivered close request to the exit; null ignores close requests.</summary>
    public TimeSpan? CloseTakes { get; set; } = TimeSpan.FromSeconds(2);

    public bool IgnoresKill { get; set; }

    public Exception? CloseThrows { get; set; }

    public Exception? KillThrows { get; set; }

    /// <summary>When set, HasExited throws this (the handle cannot tell).</summary>
    public Exception? HasExitedThrows { get; set; }

    /// <summary>Runs on every CloseMainWindow call, before it takes effect.</summary>
    public Action? OnCloseMainWindow { get; set; }

    public int CloseCalls { get; private set; }

    public int RefreshCalls { get; private set; }

    public int KillCalls { get; private set; }

    public List<DateTime> CloseCallTimes { get; } = [];

    public List<DateTime> KillCallTimes { get; } = [];

    /// <summary>The handles opened on this process, in order; each knows the enumeration it came from.</summary>
    public List<Handle> Handles { get; } = [];

    public bool IsRunning => !(_killed || _exitedByTest || (_exitAtUtc is { } at && Clock() >= at));

    public void Exit() => _exitedByTest = true;

    public Handle Open(int enumeration)
    {
        var handle = new Handle(this, enumeration);
        Handles.Add(handle);
        return handle;
    }

    internal sealed class Handle(FakeVrchatProcess process, int enumeration) : IVrchatProcessHandle
    {
        public FakeVrchatProcess Process { get; } = process;

        /// <summary>Which OpenClients call (1-based) opened this handle.</summary>
        public int Enumeration { get; } = enumeration;

        public bool Disposed { get; private set; }

        public int ProcessId => Process.ProcessId;

        public int SessionId => Process.SessionId;

        public string ProcessName => Process.ProcessName;

        public DateTime CreationTimeUtc => Process.CreationTimeUtc;

        public bool HasExited
        {
            get
            {
                ThrowIfDisposed();
                if (Process.HasExitedThrows is { } ex)
                {
                    throw ex;
                }

                return !Process.IsRunning;
            }
        }

        public bool CloseMainWindow()
        {
            ThrowIfDisposed();
            Process.CloseCalls++;
            Process.CloseCallTimes.Add(Process.Clock());
            Process.OnCloseMainWindow?.Invoke();
            if (Process.CloseThrows is { } ex)
            {
                throw ex;
            }

            if (!Process.IsRunning)
            {
                throw new InvalidOperationException("The process has exited.");
            }

            if (!Process.HasWindow)
            {
                return false;
            }

            if (Process.CloseTakes is { } takes)
            {
                var exitAt = Process.Clock() + takes;
                Process._exitAtUtc = Process._exitAtUtc is { } earlier && earlier < exitAt ? earlier : exitAt;
            }

            return true;
        }

        public void Refresh()
        {
            ThrowIfDisposed();
            Process.RefreshCalls++;
            if (Process.RefreshesUntilWindow is { } until && Process.RefreshCalls >= until)
            {
                Process.HasWindow = true;
            }
        }

        public void Kill()
        {
            ThrowIfDisposed();
            Process.KillCalls++;
            Process.KillCallTimes.Add(Process.Clock());
            if (Process.KillThrows is { } ex)
            {
                throw ex;
            }

            if (!Process.IgnoresKill)
            {
                Process._killed = true;
            }
        }

        public void Dispose() => Disposed = true;

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Disposed, this);
    }
}

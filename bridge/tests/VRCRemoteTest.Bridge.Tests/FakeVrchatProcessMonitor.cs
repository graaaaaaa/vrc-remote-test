using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatProcessMonitor : IVrchatProcessMonitor
{
    private readonly object _gate = new();

    /// <summary>Same-session clients reported by GetClients (and, via the real status rule, GetStatus).</summary>
    public List<VrchatClientInfo> Clients { get; set; } = [];

    public int UnverifiedCount { get; set; }

    public string? EnumerationError { get; set; }

    /// <summary>When set, GetStatus() throws this instead of returning a status.</summary>
    public Exception? ThrowOnGetStatus { get; set; }

    /// <summary>Runs before every GetClients, e.g. to change the clients between polls.</summary>
    public Action<int>? OnGetClients { get; set; }

    public int GetStatusCallCount { get; private set; }

    public int GetClientsCallCount { get; private set; }

    public void AddClient(int processId, DateTime? creationTimeUtc, bool watchWorlds = true)
    {
        lock (_gate)
        {
            Clients.Add(new VrchatClientInfo(processId, creationTimeUtc, watchWorlds));
        }
    }

    public VrchatClientSnapshot GetClients()
    {
        lock (_gate)
        {
            GetClientsCallCount++;
            OnGetClients?.Invoke(GetClientsCallCount);
            return EnumerationError is not null
                ? VrchatClientSnapshot.Failed(EnumerationError)
                : new VrchatClientSnapshot(Clients.ToList(), UnverifiedCount);
        }
    }

    /// <summary>Relaunch mode: the processes OpenClients opens handles to while they run.</summary>
    public List<FakeVrchatProcess> Processes { get; } = [];

    /// <summary>VRChat processes OpenClients could not confirm (e.g. elevated).</summary>
    public int OpenUnverifiedCount { get; set; }

    /// <summary>When set, OpenClients reports that the process list could not be read.</summary>
    public string? OpenEnumerationError { get; set; }

    /// <summary>Runs before every OpenClients with its 1-based call number, e.g. to start a VRChat in between.</summary>
    public Action<int>? OnOpenClients { get; set; }

    public int OpenClientsCallCount { get; private set; }

    public FakeVrchatProcess AddProcess(int processId, DateTime creationTimeUtc, int sessionId = 1, string processName = "VRChat")
    {
        var process = new FakeVrchatProcess(processId, creationTimeUtc, sessionId, processName);
        lock (_gate)
        {
            Processes.Add(process);
        }

        return process;
    }

    public VrchatProcessHandleSnapshot OpenClients()
    {
        lock (_gate)
        {
            var call = ++OpenClientsCallCount;
            OnOpenClients?.Invoke(call);
            if (OpenEnumerationError is not null)
            {
                return VrchatProcessHandleSnapshot.Failed(OpenEnumerationError);
            }

            return new VrchatProcessHandleSnapshot(
                Processes.Where(p => p.IsRunning).Select(p => (IVrchatProcessHandle)p.Open(call)).ToList(),
                OpenUnverifiedCount);
        }
    }

    public VrchatStatus GetStatus()
    {
        GetStatusCallCount++;

        if (ThrowOnGetStatus is not null)
        {
            throw ThrowOnGetStatus;
        }

        return VrchatProcessMonitor.BuildStatus(GetClients(), DateTimeOffset.UtcNow);
    }
}

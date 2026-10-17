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

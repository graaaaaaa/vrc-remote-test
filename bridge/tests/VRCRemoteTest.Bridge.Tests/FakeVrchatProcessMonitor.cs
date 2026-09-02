using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatProcessMonitor : IVrchatProcessMonitor
{
    public bool IsRunning { get; set; }
    public bool WatchWorldsDetected { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? StartTimeUtc { get; set; }

    /// <summary>When set, GetStatus() throws this instead of returning a status.</summary>
    public Exception? ThrowOnGetStatus { get; set; }

    public int GetStatusCallCount { get; private set; }

    public VrchatStatus GetStatus()
    {
        GetStatusCallCount++;

        if (ThrowOnGetStatus is not null)
        {
            throw ThrowOnGetStatus;
        }

        return new VrchatStatus
        {
            IsRunning = IsRunning,
            WatchWorldsDetected = WatchWorldsDetected,
            ProcessId = ProcessId,
            StartTimeUtc = StartTimeUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }
}

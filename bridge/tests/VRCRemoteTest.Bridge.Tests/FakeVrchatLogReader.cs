using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatLogReader : IVrchatLogReader
{
    public LogSnapshotResult ResultToReturn { get; set; } = LogSnapshotResult.Empty();
    public Exception? ThrowOnReadSnapshot { get; set; }
    public int ReadSnapshotCallCount { get; private set; }

    public LogSnapshotResult ReadSnapshot()
    {
        ReadSnapshotCallCount++;

        if (ThrowOnReadSnapshot is not null)
        {
            throw ThrowOnReadSnapshot;
        }

        return ResultToReturn;
    }
}

using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatStatusWriter : IVrchatStatusWriter
{
    public int WriteCallCount { get; private set; }
    public VrchatStatus? LastWrittenStatus { get; private set; }
    public string? LastStatusDirectory { get; private set; }

    public void Write(VrchatStatus status, string statusDirectory)
    {
        WriteCallCount++;
        LastWrittenStatus = status;
        LastStatusDirectory = statusDirectory;
    }
}

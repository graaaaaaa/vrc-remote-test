using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

public interface IVrchatStatusWriter
{
    void Write(VrchatStatus status, string statusDirectory);
}

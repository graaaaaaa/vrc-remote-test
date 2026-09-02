using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Isolates all OS-process-inspection code (Process enumeration + Windows-only WMI
/// command-line lookup), mirroring how VrcSdkBuildAdapter is the sole class touching
/// VRChat SDK types on the Unity side. No other class in this project should call
/// Process.GetProcessesByName or System.Management directly.
/// </summary>
public interface IVrchatProcessMonitor
{
    VrchatStatus GetStatus();
}

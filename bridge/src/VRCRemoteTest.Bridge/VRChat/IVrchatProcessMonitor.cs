using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Isolates all OS-process-inspection code (Process enumeration + Windows-only WMI
/// command-line lookup), mirroring how VrcSdkBuildAdapter is the sole class touching
/// VRChat SDK types on the Unity side. No other class in this project should call
/// Process.GetProcessesByName or System.Management directly. RestartManagerFileOwners
/// only looks up, by PID, the processes that have a log file open; it does not enumerate.
/// </summary>
public interface IVrchatProcessMonitor
{
    /// <summary>Enumerates once; never throws (failures are reported in the snapshot).</summary>
    VrchatClientSnapshot GetClients();

    /// <summary>
    /// The status-file view of one enumeration: the representative client only, in the
    /// shape Unity already reads. Throws when the process list cannot be read.
    /// </summary>
    VrchatStatus GetStatus();

    /// <summary>
    /// Relaunch mode: enumerates once and opens a handle to every same-session VRChat,
    /// without asking WMI. Never throws; the caller disposes the snapshot.
    /// </summary>
    VrchatProcessHandleSnapshot OpenClients();
}

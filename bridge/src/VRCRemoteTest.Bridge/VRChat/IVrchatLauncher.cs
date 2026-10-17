namespace VRCRemoteTest.Bridge.VRChat;

public interface IVrchatLauncher
{
    /// <summary>
    /// Starts one VRChat client. Returns a failed result instead of throwing when the
    /// process cannot be started.
    /// </summary>
    Task<LaunchResult> LaunchAsync(VrchatLaunchRequest request);
}

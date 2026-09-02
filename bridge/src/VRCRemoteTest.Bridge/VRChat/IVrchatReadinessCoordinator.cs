namespace VRCRemoteTest.Bridge.VRChat;

public interface IVrchatReadinessCoordinator
{
    /// <summary>
    /// Ensures VRChat is running with --watch-worlds before a build deploys,
    /// launching it if necessary. Only called by StagingWatcher when
    /// AutoLaunchVrchat is enabled.
    /// </summary>
    Task<ReadinessResult> EnsureReadyAsync(CancellationToken cancellationToken);
}

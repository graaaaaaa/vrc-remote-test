namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// The VRChat side of a deploy, only used by StagingWatcher when AutoLaunchVrchat is
/// enabled. StagingWatcher keeps validation, install, results, archive and cleanup; this
/// only decides what to do before installing and, for a cold start, launches the
/// clients after the build is installed and archived. VrchatClientMode picks the
/// implementation: <see cref="VrchatClientCoordinator"/> (Reload) or
/// <see cref="VrchatRelaunchCoordinator"/> (Relaunch).
/// </summary>
public interface IVrchatClientCoordinator
{
    /// <summary>
    /// Decides between a cold start, a plain deploy (Reload: after waiting until every
    /// running --watch-worlds client is ready) and a failure. In Relaunch mode this is
    /// also where the Bridge's own clients are closed.
    /// </summary>
    Task<VrchatPreparation> PrepareAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Launches the configured clients (VrchatClientCount, or one per VrchatProfiles
    /// entry), at least 3 seconds apart and each after the previous one's output_log
    /// file has appeared (within a bounded wait), all opening
    /// <paramref name="deployedWorldPath"/> in one shared instance via --url.
    /// </summary>
    Task<VrchatLaunchOutcome> LaunchClientsAsync(string deployedWorldPath, CancellationToken cancellationToken);
}

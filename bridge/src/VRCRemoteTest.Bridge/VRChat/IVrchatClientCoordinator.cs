namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// The VRChat side of a deploy, only used by StagingWatcher when AutoLaunchVrchat is
/// enabled. StagingWatcher keeps validation, install, results, archive and cleanup; this
/// only decides what to do before installing and, for a cold start, launches the
/// clients after the build is installed and archived.
/// </summary>
public interface IVrchatClientCoordinator
{
    /// <summary>
    /// Decides between a cold start, a plain deploy (after waiting until every running
    /// --watch-worlds client is ready) and a failure.
    /// </summary>
    Task<VrchatPreparation> PrepareAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Launches VrchatClientCount clients, a few seconds apart, all opening
    /// <paramref name="deployedWorldPath"/> in one shared instance via --url.
    /// </summary>
    Task<VrchatLaunchOutcome> LaunchClientsAsync(string deployedWorldPath, CancellationToken cancellationToken);
}

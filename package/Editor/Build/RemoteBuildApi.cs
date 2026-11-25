using System;
using System.Threading;
using System.Threading.Tasks;

namespace VRCRemoteTest
{
    /// <summary>
    /// The public way for Editor code (the window, the packaged runner's flow, or a project's own
    /// tools) to build or redeploy and get the outcome back. Everything goes through the shared
    /// coordinator of <see cref="RemoteBuildCommand"/>, so a build started here and one from the
    /// menu or the window never run at the same time, and every attempt saves its deploy result
    /// (<see cref="RemoteBuildOutcome.DeployResultPath"/>).
    ///
    /// Call from the main thread. The outcome says whether the build was deployed and VRChat
    /// launched; whether the clients are ready and whether a test passed are not decided here.
    /// </summary>
    public static class RemoteBuildApi
    {
        /// <summary>Builds the open scene for Windows with the VRChat SDK and deploys it to the Bridge.</summary>
        public static Task<RemoteBuildOutcome> BuildAndDeployAsync(
            RemoteBuildRequest request = null,
            IProgress<RemoteBuildProgress> progress = null,
            CancellationToken cancellationToken = default) =>
            RemoteBuildCommand.GetCoordinator().ExecuteRemoteBuildCoreAsync(request, progress, cancellationToken);

        /// <summary>Deploys the last artifact built in this Editor session again, without the SDK build.</summary>
        public static Task<RemoteBuildOutcome> DeployLastBuildAsync(
            RemoteBuildRequest request = null,
            IProgress<RemoteBuildProgress> progress = null,
            CancellationToken cancellationToken = default) =>
            RemoteBuildCommand.GetCoordinator().DeployLastBuildCoreAsync(request, progress, cancellationToken);

        /// <summary>
        /// While the SDK builds the world for <see cref="BuildAndDeployAsync"/> (or Remote Build &amp;
        /// Test): the tag a project's build preprocessing may embed in the world, so that the
        /// world can log it (vrcrt.build=&lt;tag&gt;) and the runner can tell this build from an
        /// earlier one. Null at any other time. The tag is not a secret.
        /// </summary>
        public static string ActiveWorldBuildTag => RemoteBuildCommand.PeekCoordinator()?.ActiveWorldBuildTag;
    }
}

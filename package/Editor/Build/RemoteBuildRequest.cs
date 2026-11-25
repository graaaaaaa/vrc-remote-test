using System.Text.RegularExpressions;

namespace VRCRemoteTest
{
    /// <summary>
    /// Optional inputs of one build or redeploy through <see cref="RemoteBuildApi"/>. Both are
    /// supplied by trusted local code (the packaged runner's prepare step, or the user's own
    /// Editor code), never by anything the Bridge or a world reports.
    /// </summary>
    public sealed class RemoteBuildRequest
    {
        private static readonly Regex TagPattern = new Regex(@"^[A-Za-z0-9_-]{8,64}$", RegexOptions.Compiled);

        /// <summary>
        /// The folder of the runner's current run (runs/&lt;runId&gt; under the run root), where a
        /// copy of the deploy result is written as deploy-result.json. Null outside a run.
        /// </summary>
        public string RunDirectory { get; set; }

        /// <summary>
        /// The tag to expose as <see cref="RemoteBuildApi.ActiveWorldBuildTag"/> while the SDK builds
        /// the world, so that the user's build preprocessing can embed it. Null or invalid: the
        /// attempt id is used. Ignored by a redeploy, which keeps the tag of the bytes it sends.
        /// </summary>
        public string WorldBuildTag { get; set; }

        public static bool IsValidTag(string tag) => tag != null && TagPattern.IsMatch(tag);
    }
}

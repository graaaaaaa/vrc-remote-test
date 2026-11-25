using System;

namespace VRCRemoteTest
{
    /// <summary>Terminal result of a single ExecuteRemoteBuildAsync run.</summary>
    public sealed class RemoteBuildOutcome
    {
        public bool Succeeded { get; }
        public string BuildId { get; }
        public BuildResult Result { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }

        /// <summary>Identifies this attempt, also when it stopped before a build ID was allocated.</summary>
        public string AttemptId { get; internal set; }

        /// <summary>Where the structured deploy result was saved; null when it was not written.</summary>
        public string DeployResultPath { get; internal set; }

        /// <summary>Stopped by its cancellation token (not inferred from the message).</summary>
        internal bool Canceled { get; set; }

        /// <summary>preflight, sdkBuild, upload or result: how far the attempt got.</summary>
        internal string Stage { get; set; }

        internal string Kind { get; set; }

        internal string ArtifactSha256 { get; set; }

        internal long? ArtifactSize { get; set; }

        /// <summary>The tag actually embedded in the bytes deployed, or null when that is not known.</summary>
        internal string WorldBuildTag { get; set; }

        internal DateTimeOffset StartedAtUtc { get; set; }

        internal DateTimeOffset FinishedAtUtc { get; set; }

        private RemoteBuildOutcome(
            bool succeeded, string buildId, BuildResult result, string errorCode, string errorMessage)
        {
            Succeeded = succeeded;
            BuildId = buildId;
            Result = result;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        public static RemoteBuildOutcome Success(string buildId, BuildResult result) =>
            new RemoteBuildOutcome(true, buildId, result, null, null);

        /// <summary><paramref name="result"/> is the Bridge's failed result, when there is one (for its launch and warnings).</summary>
        public static RemoteBuildOutcome Failure(string buildId, string errorCode, string errorMessage,
            BuildResult result = null) =>
            new RemoteBuildOutcome(false, buildId, result, errorCode, errorMessage);

        internal static RemoteBuildOutcome Cancellation(string buildId, string message) =>
            new RemoteBuildOutcome(false, buildId, null, VRCRemoteTest.ErrorCode.UnknownError, message) { Canceled = true };
    }
}

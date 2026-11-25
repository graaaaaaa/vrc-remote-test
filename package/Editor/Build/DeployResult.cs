using System;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// The structured result of one build or redeploy attempt (schema vrcrt.deploy-result v1),
    /// written by <see cref="DeployResultWriter"/> and read by the packaged runner. It keeps
    /// "deployed", "launched", "ready" and "passed" apart: this file only says the first two
    /// (<see cref="Classification"/>); readiness and the test verdict are the runner's and the
    /// user's.
    /// </summary>
    public sealed class DeployResult
    {
        public const string SchemaName = "vrcrt.deploy-result";
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schema")] public string Schema { get; set; } = SchemaName;
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonProperty("attemptId")] public string AttemptId { get; set; }

        /// <summary>Null when the attempt stopped before a build ID was allocated.</summary>
        [JsonProperty("buildId")] public string BuildId { get; set; }

        [JsonProperty("packageVersion")] public string PackageVersion { get; set; }

        /// <summary>build (SDK build and deploy) or redeploy (Deploy Last Build).</summary>
        [JsonProperty("kind")] public string Kind { get; set; }

        [JsonProperty("startedAtUtc")] public DateTimeOffset StartedAtUtc { get; set; }
        [JsonProperty("finishedAtUtc")] public DateTimeOffset FinishedAtUtc { get; set; }

        /// <summary>preflight, sdkBuild, upload or result: the last stage the attempt entered.</summary>
        [JsonProperty("stage")] public string Stage { get; set; }

        [JsonProperty("artifact")] public DeployResultArtifact Artifact { get; set; }
        [JsonProperty("unity")] public DeployResultUnity Unity { get; set; }

        /// <summary>The Bridge's result after Unity validated it; null when there was none.</summary>
        [JsonProperty("bridgeResult")] public DeployResultBridge BridgeResult { get; set; }

        [JsonProperty("classification")] public DeployResultClassification Classification { get; set; }
    }

    public sealed class DeployResultArtifact
    {
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("size")] public long? Size { get; set; }

        /// <summary>
        /// The tag embedded in these bytes, as far as this Editor knows; null when unknown (no fresh
        /// build in this session, or the bytes changed since). A null tag makes the runner's
        /// world-build match UNKNOWN.
        /// </summary>
        [JsonProperty("worldBuildTag")] public string WorldBuildTag { get; set; }
    }

    public sealed class DeployResultUnity
    {
        [JsonProperty("succeeded")] public bool Succeeded { get; set; }
        [JsonProperty("canceled")] public bool Canceled { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }

        /// <summary>Cut to 2000 characters. Not redacted: treat it like the Editor log.</summary>
        [JsonProperty("message")] public string Message { get; set; }
    }

    public sealed class DeployResultBridge
    {
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("errorCode")] public string ErrorCode { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("deployedFileName")] public string DeployedFileName { get; set; }
        [JsonProperty("launch")] public LaunchReport Launch { get; set; }
        [JsonProperty("warningCount")] public int WarningCount { get; set; }
    }

    public sealed class DeployResultClassification
    {
        /// <summary>PASS, FAIL, UNKNOWN or CANCELED: whether this build is installed on the Bridge.</summary>
        [JsonProperty("deploy")] public string Deploy { get; set; }

        /// <summary>PASS, FAIL, UNKNOWN, CANCELED, notApplicable or notReached: whether VRChat started as requested.</summary>
        [JsonProperty("launch")] public string Launch { get; set; }

        /// <summary>Why a procedure must stop here; null when it may continue.</summary>
        [JsonProperty("stopReason")] public string StopReason { get; set; }
    }
}

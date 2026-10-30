using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// Wire format for the per-build result returned by the Bridge HTTPS API.
    /// Must match
    /// bridge/src/VRCRemoteTest.Bridge/Protocol/BuildResult.cs exactly.
    /// Unity treats this as untrusted input and must validate it (see
    /// RemoteBuildCoordinator.IsValidResult) before acting on it.
    /// </summary>
    [Serializable]
    public sealed class BuildResult
    {
        [JsonProperty("protocolVersion")]
        public int ProtocolVersion { get; set; }

        [JsonProperty("buildId")]
        public string BuildId { get; set; } = string.Empty;

        /// <summary>"deployed" | "failed"</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        [JsonProperty("deployedFileName")]
        public string DeployedFileName { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; }

        [JsonProperty("errorCode")]
        public string ErrorCode { get; set; }

        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        [JsonProperty("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }

        /// <summary>
        /// What happened to VRChat for this build (Bridge v0.8.0 and later). Null when the
        /// build stopped before that was decided, or from an older Bridge.
        /// </summary>
        [JsonProperty("launch", NullValueHandling = NullValueHandling.Ignore)]
        public LaunchReport Launch { get; set; }

        /// <summary>Things worth knowing that did not stop the build (Bridge v0.8.0 and later); untrusted text.</summary>
        [JsonProperty("warnings", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Warnings { get; set; }
    }

    /// <summary>
    /// The VRChat side of a deploy. Must match LaunchReport in
    /// bridge/src/VRCRemoteTest.Bridge/Protocol/BuildResult.cs. Outcome is one of
    /// disabled, reusedRunning, launched, relaunched, partial, failed or notReached.
    /// </summary>
    [Serializable]
    public sealed class LaunchReport
    {
        public const string Disabled = "disabled";

        [JsonProperty("attempted")]
        public bool Attempted { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; } = string.Empty;

        [JsonProperty("requested", NullValueHandling = NullValueHandling.Ignore)]
        public int? Requested { get; set; }

        [JsonProperty("started", NullValueHandling = NullValueHandling.Ignore)]
        public int? Started { get; set; }

        [JsonProperty("closed", NullValueHandling = NullValueHandling.Ignore)]
        public int? Closed { get; set; }
    }
}

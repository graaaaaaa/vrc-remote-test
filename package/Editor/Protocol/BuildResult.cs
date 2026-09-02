using System;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// Wire format for the single per-build result file the Bridge writes under
    /// `{stagingDirectory}/results/{buildId}.json`. Must match
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
    }
}

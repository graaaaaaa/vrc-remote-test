using System;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// Wire format for the `{buildId}.ready.json` manifest written by Unity.
    /// Must match bridge/src/VRCRemoteTest.Bridge/Protocol/BuildManifest.cs exactly
    /// (flat structure, camelCase property names pinned via JsonProperty).
    /// </summary>
    [Serializable]
    public sealed class BuildManifest
    {
        [JsonProperty("protocolVersion")]
        public int ProtocolVersion { get; set; }

        [JsonProperty("buildId")]
        public string BuildId { get; set; } = string.Empty;

        /// <summary>Basename only. Never a filesystem path.</summary>
        [JsonProperty("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonProperty("size")]
        public long Size { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonProperty("createdAtUtc")]
        public DateTimeOffset CreatedAtUtc { get; set; }
    }
}

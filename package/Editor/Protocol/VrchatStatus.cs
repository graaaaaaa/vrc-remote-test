using System;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// Wire format for the continuously-updated `status/vrchat-status.json` file the
    /// Bridge writes (Phase 4a). Deliberately minimal, mirrors the Bridge-side model
    /// exactly. Advisory/informational only: it never gates build success or failure,
    /// only preflight display (spec section 34's "deploy success = file placement"
    /// philosophy).
    /// </summary>
    [Serializable]
    public sealed class VrchatStatus
    {
        [JsonProperty("isRunning")]
        public bool IsRunning { get; set; }

        [JsonProperty("watchWorldsDetected")]
        public bool WatchWorldsDetected { get; set; }

        [JsonProperty("processId")]
        public int? ProcessId { get; set; }

        [JsonProperty("startTimeUtc")]
        public DateTimeOffset? StartTimeUtc { get; set; }

        [JsonProperty("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// Wire format for GET /api/v1/bridge (Bridge v0.8.0 and later): the Bridge's version,
    /// host restarts and configuration state. Mirrors the anonymous object in
    /// bridge/src/VRCRemoteTest.Bridge/Lan/BridgeAdminApi.cs (Info); fields this package does
    /// not use are left out. Display only: it never gates a build. An older Bridge answers
    /// 404 and the transport reports null.
    /// </summary>
    [Serializable]
    public sealed class BridgeInfo
    {
        /// <summary>The Bridge's informational version, possibly with "+build metadata".</summary>
        [JsonProperty("version")]
        public string Version { get; set; }

        /// <summary>Goes up by one each time the host restarts in-process.</summary>
        [JsonProperty("hostGeneration")]
        public long HostGeneration { get; set; }

        [JsonProperty("hostStartedAtUtc")]
        public DateTimeOffset? HostStartedAtUtc { get; set; }

        [JsonProperty("activeConfigEtag")]
        public string ActiveConfigEtag { get; set; }

        [JsonProperty("savedConfigEtag")]
        public string SavedConfigEtag { get; set; }

        /// <summary>The last configuration change made through the LAN API, or null.</summary>
        [JsonProperty("lastConfigApply")]
        public BridgeConfigApply LastConfigApply { get; set; }

        /// <summary>The operation holding the Bridge's gate (a deploy, config, restart, close), or null.</summary>
        [JsonProperty("busy")]
        public string Busy { get; set; }

        [JsonProperty("restartPending")]
        public bool RestartPending { get; set; }

        /// <summary>Plain-language notes about the active configuration; untrusted text.</summary>
        [JsonProperty("configNotes")]
        public List<string> ConfigNotes { get; set; }
    }

    [Serializable]
    public sealed class BridgeConfigApply
    {
        [JsonProperty("operationId")]
        public string OperationId { get; set; }

        [JsonProperty("requestedEtag")]
        public string RequestedEtag { get; set; }

        /// <summary>pending, applied, rolledBack or failed.</summary>
        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("problems")]
        public List<string> Problems { get; set; }

        [JsonProperty("atUtc")]
        public DateTimeOffset? AtUtc { get; set; }
    }
}

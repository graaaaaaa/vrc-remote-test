using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

        /// <summary>SHA-256 of the running Bridge executable; null when it could not be read.</summary>
        [JsonProperty("exeSha256")]
        public string ExeSha256 { get; set; }

        /// <summary>
        /// When the Bridge process started. hostGeneration starts again at 1 in a new process,
        /// so the two together tell one host from another.
        /// </summary>
        [JsonProperty("processStartedAtUtc")]
        public DateTimeOffset? ProcessStartedAtUtc { get; set; }

        /// <summary>Goes up by one each time the host restarts in-process.</summary>
        [JsonProperty("hostGeneration")]
        public long HostGeneration { get; set; }

        [JsonProperty("hostStartedAtUtc")]
        public DateTimeOffset? HostStartedAtUtc { get; set; }

        [JsonProperty("activeConfigEtag")]
        public string ActiveConfigEtag { get; set; }

        [JsonProperty("savedConfigEtag")]
        public string SavedConfigEtag { get; set; }

        /// <summary>
        /// The settings the running host was started with, keyed by their config.json names
        /// (VrchatClientMode, VrchatProfiles, ...). Read through VrchatClientSettings.FromActiveConfig.
        /// </summary>
        [JsonProperty("activeConfig")]
        public JObject ActiveConfig { get; set; }

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

        /// <summary>
        /// A random value per Bridge process, 32 lowercase hex digits (Bridge v0.12.0 and later);
        /// null from an older Bridge.
        /// </summary>
        [JsonProperty("bridgeInstanceId")]
        public string BridgeInstanceId { get; set; }

        /// <summary>
        /// Goes up whenever something on Windows changed or may have changed (a deploy, a close or
        /// launch attempt, a config write, a host start); never goes down within a process. Bridge
        /// v0.12.0 and later; null from an older Bridge.
        /// </summary>
        [JsonProperty("operationRevision")]
        public long? OperationRevision { get; set; }

        /// <summary>
        /// The current Relaunch launch plan (Bridge v0.12.0 and later); null when there is none
        /// (after a Reload deploy, a failed deploy or a host restart) or from an older Bridge.
        /// </summary>
        [JsonProperty("currentBatch")]
        public BridgeBatch CurrentBatch { get; set; }

        /// <summary>
        /// When this package asked for this answer (UTC); null when unknown. Set by the transport
        /// and the coordinator, not part of the wire format.
        /// </summary>
        [JsonIgnore]
        public DateTimeOffset? ReadAtUtc { get; set; }
    }

    /// <summary>The Bridge's current Relaunch launch plan (GET /bridge currentBatch, Bridge v0.12.0 and later).</summary>
    [Serializable]
    public sealed class BridgeBatch
    {
        /// <summary>The build ID of the deploy that made this plan; untrusted text.</summary>
        [JsonProperty("buildId")]
        public string BuildId { get; set; }

        /// <summary>The slots that can be started again one at a time.</summary>
        [JsonProperty("launchedSlots")]
        public List<int> LaunchedSlots { get; set; }
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

    /// <summary>
    /// Wire format for GET /api/v1/profiles (Bridge v0.8.0 and later): the account each VRChat
    /// profile's client last logged in to, as the Bridge saw it in the client's log. A profile
    /// whose client has not run under the Bridge is not listed.
    /// </summary>
    [Serializable]
    public sealed class BridgeProfiles
    {
        /// <summary>VrchatProfiles of the running host; null in Reload mode.</summary>
        [JsonProperty("configuredProfiles")]
        public List<int> ConfiguredProfiles { get; set; }

        [JsonProperty("profiles")]
        public List<BridgeProfileAccount> Profiles { get; set; }

        /// <summary>For example two configured profiles logged in to one account; untrusted text.</summary>
        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; }
    }

    [Serializable]
    public sealed class BridgeProfileAccount
    {
        [JsonProperty("profile")]
        public int Profile { get; set; }

        /// <summary>Untrusted: the name in the log, masked by the Bridge.</summary>
        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        /// <summary>Equal values mean the same account on this Bridge.</summary>
        [JsonProperty("idDigest")]
        public string IdDigest { get; set; }

        [JsonProperty("authenticatedAtUtc")]
        public DateTimeOffset? AuthenticatedAtUtc { get; set; }
    }
}

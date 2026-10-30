using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VRCRemoteTest
{
    /// <summary>
    /// Wire format for `status/vrchat-clients.json`, which the Bridge (v0.6.0 and later)
    /// rewrites every 5 seconds: one entry per slot (0-3) that has had a VRChat client,
    /// exited ones included. Mirrors the Bridge-side model; fields the Log Viewer does not
    /// use are left out. Everything here is observed from the clients' logs and is
    /// advisory: it never gates a build. Missing with a Bridge 0.5.x, and the Log Viewer
    /// then shows vrchat-latest.log alone, as before.
    /// </summary>
    [Serializable]
    public sealed class VrchatClients
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonProperty("logMarkerPrefixes")]
        public List<string> LogMarkerPrefixes { get; set; }

        [JsonProperty("processListReadable")]
        public bool ProcessListReadable { get; set; }

        [JsonProperty("unassignedClients")]
        public int UnassignedClients { get; set; }

        [JsonProperty("clients")]
        public List<VrchatClientEntry> Clients { get; set; }

        [JsonProperty("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    [Serializable]
    public sealed class VrchatClientEntry
    {
        [JsonProperty("slot")]
        public int Slot { get; set; }

        [JsonProperty("clientGeneration")]
        public long ClientGeneration { get; set; }

        [JsonProperty("observationEpoch")]
        public long? ObservationEpoch { get; set; }

        [JsonProperty("processId")]
        public int ProcessId { get; set; }

        /// <summary>True while running, false once its exit is confirmed, null when the Bridge cannot tell now.</summary>
        [JsonProperty("running")]
        public bool? Running { get; set; }

        [JsonProperty("profile")]
        public int? Profile { get; set; }

        [JsonProperty("logStatus")]
        public string LogStatus { get; set; }

        /// <summary>starting, joining, joined, left or unknown; null when VRChat's Logging is not Full.</summary>
        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("instance")]
        public string Instance { get; set; }

        [JsonProperty("inErrorWorld")]
        public bool? InErrorWorld { get; set; }

        [JsonProperty("playersInInstance")]
        public List<string> PlayersInInstance { get; set; }

        [JsonProperty("isMaster")]
        public bool? IsMaster { get; set; }

        [JsonProperty("loggingLevel")]
        public string LoggingLevel { get; set; }

        [JsonProperty("authenticated")]
        public bool? Authenticated { get; set; }

        /// <summary>authenticated, notSeen, likelyLoggedOut or unknown (Bridge v0.8.0 and later); null from an older Bridge.</summary>
        [JsonProperty("loginState")]
        public string LoginState { get; set; }

        /// <summary>The account this client logged in to, or null (not seen yet, or an older Bridge).</summary>
        [JsonProperty("account")]
        public VrchatClientAccount Account { get; set; }

        [JsonProperty("observedThroughSeq")]
        public long ObservedThroughSeq { get; set; }

        [JsonProperty("catchingUp")]
        public bool CatchingUp { get; set; }

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; }
    }

    [Serializable]
    public sealed class VrchatClientAccount
    {
        /// <summary>Untrusted: whatever name the client's log carried, masked by the Bridge.</summary>
        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        /// <summary>Equal values mean the same account on this Bridge.</summary>
        [JsonProperty("idDigest")]
        public string IdDigest { get; set; }

        [JsonProperty("authenticatedAtUtc")]
        public DateTimeOffset? AuthenticatedAtUtc { get; set; }
    }
}

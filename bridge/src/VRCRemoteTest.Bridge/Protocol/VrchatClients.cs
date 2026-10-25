using System.Text.Json.Serialization;

namespace VRCRemoteTest.Bridge.Protocol;

/// <summary>
/// Wire format for the client status kept in Bridge-private state, rewritten by
/// VrchatLogService every 5 seconds (client-observation plan F1). One entry per slot that
/// has had a client, its exited one included, so a client keeps its slot number across
/// polls and Bridge restarts. Everything here is observed from the clients' processes and
/// logs: advisory, never a reason for the Mac to trust a world or for the Bridge to act.
/// The VRChat status response keeps its own shape.
///
/// The control fields of later phases (bridgeSessionId, supportedActions, sessionGeneration,
/// controlEligible) are not written yet.
/// </summary>
public sealed class VrchatClients
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The marker prefixes in use and where they come from ("config": config.json's LogMarkerPrefixes or its default).</summary>
    [JsonPropertyName("logMarkerPrefixes")]
    public IReadOnlyList<string> LogMarkerPrefixes { get; init; } = [];

    [JsonPropertyName("logMarkerPrefixesSource")]
    public string LogMarkerPrefixesSource { get; init; } = "config";

    /// <summary>False when the process list could not be read this time; every slot is then unknown.</summary>
    [JsonPropertyName("processListReadable")]
    public bool ProcessListReadable { get; init; }

    /// <summary>VRChat processes without a slot: unconfirmed ones, those whose creation time is unknown, and any beyond four.</summary>
    [JsonPropertyName("unassignedClients")]
    public int UnassignedClients { get; init; }

    [JsonPropertyName("clients")]
    public IReadOnlyList<VrchatClientEntry> Clients { get; init; } = [];

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed class VrchatClientEntry
{
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>Derived from the PID and creation time until the Bridge keeps a launch counter: another client in the slot has another value.</summary>
    [JsonPropertyName("clientGeneration")]
    public long ClientGeneration { get; init; }

    /// <summary>
    /// Increases whenever this client's log is read again from its start (Bridge restart,
    /// another file, a shorter file, other marker prefixes): seq values from another epoch
    /// cannot be compared. Null when the client's log was not read since the Bridge started.
    /// </summary>
    [JsonPropertyName("observationEpoch")]
    public long? ObservationEpoch { get; init; }

    [JsonPropertyName("processId")]
    public int ProcessId { get; init; }

    [JsonPropertyName("creationTimeUtc")]
    public DateTimeOffset CreationTimeUtc { get; init; }

    /// <summary>True while running, false once its exit is confirmed, null when it cannot be told now.</summary>
    [JsonPropertyName("running")]
    public bool? Running { get; init; }

    [JsonPropertyName("watchWorlds")]
    public bool? WatchWorlds { get; init; }

    /// <summary>The VRChat profile from the Bridge's Relaunch record; null when the client is not in it.</summary>
    [JsonPropertyName("profile")]
    public int? Profile { get; init; }

    /// <summary>shown, notYet, unmatched, cannotCheck, notRunning or unavailable: whether this client's log could be told.</summary>
    [JsonPropertyName("logStatus")]
    public string LogStatus { get; init; } = string.Empty;

    /// <summary>
    /// starting, joining, joined, left or unknown; null when VRChat's Logging is not Full,
    /// since the lines that tell it are not written then.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>The instance of the latest Joining, "local:&lt;id&gt;" without the trailing ":&lt;n&gt;".</summary>
    [JsonPropertyName("requestedInstance")]
    public string? RequestedInstance { get; init; }

    /// <summary>Set once the join finished; same form as requestedInstance. The same instance is not necessarily the same build.</summary>
    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("inErrorWorld")]
    public bool? InErrorWorld { get; init; }

    [JsonPropertyName("playersInInstance")]
    public IReadOnlyList<string>? PlayersInInstance { get; init; }

    [JsonPropertyName("isMaster")]
    public bool? IsMaster { get; init; }

    /// <summary>full, errorsOnly, off or unknown (VRChat's Settings &gt; Debug &gt; Logging).</summary>
    [JsonPropertyName("loggingLevel")]
    public string LoggingLevel { get; init; } = "unknown";

    /// <summary>True once "User Authenticated" was logged; null otherwise, never false.</summary>
    [JsonPropertyName("authenticated")]
    public bool? Authenticated { get; init; }

    [JsonPropertyName("joinAttemptSeq")]
    public long? JoinAttemptSeq { get; init; }

    [JsonPropertyName("joinedSeq")]
    public long? JoinedSeq { get; init; }

    /// <summary>The seq of the last complete line read in this epoch.</summary>
    [JsonPropertyName("observedThroughSeq")]
    public long ObservedThroughSeq { get; init; }

    /// <summary>The log has not been read to its end yet: what is reported may be behind.</summary>
    [JsonPropertyName("catchingUp")]
    public bool CatchingUp { get; init; }

    /// <summary>Lines longer than 64 KiB, of which only the start was kept.</summary>
    [JsonPropertyName("droppedLines")]
    public long DroppedLines { get; init; }

    [JsonPropertyName("lastEvents")]
    public IReadOnlyList<VrchatClientEvent> LastEvents { get; init; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class VrchatClientEvent
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    /// <summary>The line's local time on the Windows machine, in UTC; null when daylight saving time makes it ambiguous.</summary>
    [JsonPropertyName("atUtc")]
    public DateTimeOffset? AtUtc { get; init; }

    [JsonPropertyName("player")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Player { get; init; }

    [JsonPropertyName("instance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; init; }
}

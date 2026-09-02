using System.Text.Json.Serialization;

namespace VRCRemoteTest.Bridge.Protocol;

/// <summary>
/// Wire format for the continuously-updated `{stagingDirectory}/status/vrchat-status.json`
/// file. Deliberately minimal (Codex plan review Phase 4a, confidence 0.91) -- this is NOT
/// a revival of the original heartbeat design (bridgeVersion, hostName, etc. were removed
/// as over-engineered for v1, see IResultWriter's doc comment). Unity treats this as
/// advisory/informational only: it never gates build success or failure, only preflight
/// display.
/// </summary>
public sealed class VrchatStatus
{
    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; init; }

    [JsonPropertyName("watchWorldsDetected")]
    public bool WatchWorldsDetected { get; init; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    [JsonPropertyName("startTimeUtc")]
    public DateTimeOffset? StartTimeUtc { get; init; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

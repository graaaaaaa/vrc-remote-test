using System.Text.Json.Serialization;

namespace VRCRemoteTest.Bridge.Protocol;

/// <summary>
/// Wire format for VRChat status kept in Bridge-private state and returned through
/// the HTTPS API. Unity uses it for preflight display; it does not gate build results.
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

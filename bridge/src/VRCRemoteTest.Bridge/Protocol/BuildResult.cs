using System.Text.Json.Serialization;

namespace VRCRemoteTest.Bridge.Protocol;

/// <summary>
/// Wire format for the single per-build result file the Bridge writes under
/// `{stagingDirectory}/results/{buildId}.json`. Replaces the original heartbeat +
/// rich status-polling design: Unity waits for exactly one of these files to appear
/// after upload, with a bounded timeout, rather than polling a continuously-updated
/// bridge.json heartbeat.
/// </summary>
public sealed class BuildResult
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("buildId")]
    public string BuildId { get; init; } = string.Empty;

    /// <summary>"deployed" | "failed"</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("deployedFileName")]
    public string? DeployedFileName { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

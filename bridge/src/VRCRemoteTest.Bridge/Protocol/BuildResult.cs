using System.Text.Json.Serialization;

namespace VRCRemoteTest.Bridge.Protocol;

/// <summary>
/// Wire format for the per-build result kept in Bridge-private state and returned
/// through the HTTPS API. Unity polls by build ID after upload with a bounded timeout.
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

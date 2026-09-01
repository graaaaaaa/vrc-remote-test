using System.Text.Json.Serialization;

namespace VRCRemoteTest.Bridge.Protocol;

/// <summary>
/// Wire format for the `{buildId}.ready.json` manifest written by the Unity side.
/// Property names are pinned with <see cref="JsonPropertyNameAttribute"/> (camelCase)
/// rather than relying on a naming policy, so the shape is locked down and testable
/// against the same golden JSON fixture used by the Unity-side (Newtonsoft.Json) model.
/// See tests/fixtures/sample-manifest.json.
/// </summary>
public sealed class BuildManifest
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("buildId")]
    public string BuildId { get; init; } = string.Empty;

    /// <summary>Basename only. Never trust this as a filesystem path without validation.</summary>
    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }
}

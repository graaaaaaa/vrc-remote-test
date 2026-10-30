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

    /// <summary>What happened to VRChat for this build; absent when the build stopped before that was decided, or from a Bridge before v0.8.0.</summary>
    [JsonPropertyName("launch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaunchReport? Launch { get; init; }

    /// <summary>Things worth knowing that did not stop the build (two profiles on one account).</summary>
    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <summary>
/// The VRChat side of a deploy. <see cref="Attempted"/> means VRChat.exe was started at
/// least once for this build. <see cref="Outcome"/>:
/// disabled (AutoLaunchVrchat is off: nothing is launched or reloaded by the Bridge),
/// reusedRunning (Reload: the running --watch-worlds clients load the build themselves),
/// launched, relaunched (the Bridge's own clients were closed first), partial (some
/// clients started, then the batch stopped), failed (none started), notReached (the build
/// stopped before launching, e.g. VRChat the Bridge did not launch was running).
/// </summary>
public sealed class LaunchReport
{
    public const string Disabled = "disabled";
    public const string ReusedRunning = "reusedRunning";
    public const string Launched = "launched";
    public const string Relaunched = "relaunched";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string NotReached = "notReached";

    [JsonPropertyName("attempted")]
    public bool Attempted { get; init; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    [JsonPropertyName("requested")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Requested { get; init; }

    [JsonPropertyName("started")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Started { get; init; }

    /// <summary>The Bridge's own clients closed before launching (Relaunch mode).</summary>
    [JsonPropertyName("closed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Closed { get; init; }
}

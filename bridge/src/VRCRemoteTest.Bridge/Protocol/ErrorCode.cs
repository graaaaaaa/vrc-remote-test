namespace VRCRemoteTest.Bridge.Protocol;

/// <summary>
/// Error code constants shared with the Unity-side protocol model. Kept as plain
/// strings (not an enum) so the wire format never depends on ordinal values.
/// </summary>
public static class ErrorCode
{
    public const string ArtifactNotFound = "ARTIFACT_NOT_FOUND";
    public const string ManifestInvalid = "MANIFEST_INVALID";
    public const string ProtocolVersionMismatch = "PROTOCOL_VERSION_MISMATCH";
    public const string HashMismatch = "HASH_MISMATCH";
    public const string SizeMismatch = "SIZE_MISMATCH";
    public const string SizeExceeded = "SIZE_EXCEEDED";
    public const string BuildAlreadyProcessed = "BUILD_ALREADY_PROCESSED";
    public const string DeployFailed = "DEPLOY_FAILED";
    public const string UnknownError = "UNKNOWN_ERROR";
}

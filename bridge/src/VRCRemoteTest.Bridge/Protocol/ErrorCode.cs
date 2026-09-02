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

    // === Phase 4.1: VRChat autoLaunch ===
    // VRCHAT_NOT_FOUND is deliberately NOT defined here: BridgeOptionsValidator
    // failures happen at Bridge startup, before any BuildResult can ever be
    // written (see Program.cs's OptionsValidationException handling), so that
    // code can never actually appear in the wire protocol.
    public const string VrchatStartFailed = "VRCHAT_START_FAILED";
    public const string VrchatStartTimeout = "VRCHAT_START_TIMEOUT";
    public const string VrchatWatchWorldsMissing = "VRCHAT_WATCH_WORLDS_MISSING";
}

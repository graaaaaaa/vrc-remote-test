namespace VRCRemoteTest
{
    public static class ErrorCode
    {
        // === Bridge wire codes (must match bridge/src/VRCRemoteTest.Bridge/Protocol/ErrorCode.cs) ===
        public const string ArtifactNotFound = "ARTIFACT_NOT_FOUND";
        public const string ManifestInvalid = "MANIFEST_INVALID";
        public const string ProtocolVersionMismatch = "PROTOCOL_VERSION_MISMATCH";
        public const string HashMismatch = "HASH_MISMATCH";
        public const string SizeMismatch = "SIZE_MISMATCH";
        public const string SizeExceeded = "SIZE_EXCEEDED";
        public const string BuildAlreadyProcessed = "BUILD_ALREADY_PROCESSED";
        public const string DeployFailed = "DEPLOY_FAILED";
        public const string UnknownError = "UNKNOWN_ERROR";

        // === Unity-side-only codes (never sent over the wire) ===
        public const string SdkNotAvailable = "SDK_NOT_AVAILABLE";
        public const string SdkBuildFailed = "SDK_BUILD_FAILED";
        public const string InvalidBuildTarget = "INVALID_BUILD_TARGET";
        public const string RemoteShareUnavailable = "REMOTE_SHARE_UNAVAILABLE";
        public const string RemoteShareNotWritable = "REMOTE_SHARE_NOT_WRITABLE";
        public const string UploadFailed = "UPLOAD_FAILED";
        public const string BuildAlreadyRunning = "BUILD_ALREADY_RUNNING";
        public const string PlayModeActive = "PLAY_MODE_ACTIVE";
        public const string ResultTimeout = "RESULT_TIMEOUT";
        public const string ResultInvalid = "RESULT_INVALID";
        public const string InvalidConfiguration = "INVALID_CONFIGURATION";
    }
}

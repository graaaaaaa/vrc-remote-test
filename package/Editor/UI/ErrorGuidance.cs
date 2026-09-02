using System.Collections.Generic;

namespace VRCRemoteTest
{
    /// <summary>
    /// Unity-side-only lookup from ErrorCode string to short user-facing
    /// guidance text (spec section 47's "better error messages"). Deliberately
    /// does NOT change the wire protocol — the Bridge's BuildResult.ErrorMessage
    /// is unchanged; this table only adds a second, more actionable line shown
    /// alongside the raw code+message in the UI (Codex plan review Phase 5,
    /// Round 1 Alternatives, confidence 0.86).
    /// </summary>
    internal static class ErrorGuidance
    {
        /// <summary>
        /// Codes deliberately excluded from the coverage test (ErrorGuidanceTests):
        /// too generic to benefit from canned guidance beyond the raw message
        /// (Codex plan review Phase 5, Round 3).
        /// </summary>
        internal static readonly string[] ExcludedFromCoverage = { ErrorCode.UnknownError };

        private static readonly Dictionary<string, string> Guidance = new()
        {
            // Bridge wire codes
            [ErrorCode.ArtifactNotFound] =
                "The build artifact file could not be found. Try Remote Build & Test again.",
            [ErrorCode.ManifestInvalid] =
                "The build manifest was malformed or unreadable, usually from an interrupted upload. Try again.",
            [ErrorCode.ProtocolVersionMismatch] =
                "The Bridge and Unity package versions are out of sync. Update both to matching versions.",
            [ErrorCode.HashMismatch] =
                "The uploaded file's checksum did not match, usually from an interrupted upload. Try again.",
            [ErrorCode.SizeMismatch] =
                "The uploaded file's size did not match the manifest. Try again.",
            [ErrorCode.SizeExceeded] =
                "The build artifact exceeds the Bridge's configured size limit. " +
                "Check MaxArtifactSizeBytes in config.json.",
            [ErrorCode.BuildAlreadyProcessed] =
                "This build was already processed by the Bridge. No action needed.",
            [ErrorCode.DeployFailed] =
                "The Bridge failed to move the file into the VRChat Worlds directory. " +
                "Check that VrchatWorldsDirectory in config.json exists and is writable.",
            [ErrorCode.VrchatStartFailed] =
                "The Bridge could not launch VRChat. Verify VrchatExecutable in config.json " +
                "points to a valid VRChat.exe.",
            [ErrorCode.VrchatStartTimeout] =
                "VRChat did not become ready within the configured timeout. Check that VRChat starts " +
                "normally, or increase VrchatStartupTimeoutSeconds in config.json.",
            [ErrorCode.VrchatWatchWorldsMissing] =
                "VRChat is already running without --watch-worlds and will not be auto-restarted. " +
                "Close it and relaunch with --watch-worlds, or use start-vrchat-dev.ps1.",

            // Unity-only codes
            [ErrorCode.SdkNotAvailable] =
                "The VRChat SDK's build API is not available. Open the VRChat SDK Control Panel first.",
            [ErrorCode.SdkBuildFailed] =
                "The VRChat SDK build failed. Check the Console for SDK validation errors.",
            [ErrorCode.InvalidBuildTarget] =
                "The active Build Target is not Windows. Switch to StandaloneWindows64 via " +
                "File > Build Settings.",
            [ErrorCode.RemoteShareUnavailable] =
                "The configured SMB share is not reachable. Check that it's mounted and the path is correct.",
            [ErrorCode.RemoteShareNotWritable] =
                "The configured SMB share is mounted but not writable. Check share and NTFS permissions.",
            [ErrorCode.UploadFailed] =
                "The build failed to upload to the remote share. Check your network connection and " +
                "share permissions.",
            [ErrorCode.BuildAlreadyRunning] =
                "A remote build is already in progress. Wait for it to finish before starting another.",
            [ErrorCode.PlayModeActive] =
                "Cannot start a remote build while the Editor is in Play Mode. Exit Play Mode first.",
            [ErrorCode.ResultTimeout] =
                "Timed out waiting for the Bridge to respond. Check that the Bridge is running on the " +
                "Windows machine.",
            [ErrorCode.ResultInvalid] =
                "The Bridge returned an unexpected or invalid result. Check the Bridge logs on the " +
                "Windows machine.",
            [ErrorCode.InvalidConfiguration] =
                "The Remote Test configuration is invalid or incomplete. Check the Settings foldout.",
        };

        public static string GetGuidance(string errorCode)
        {
            if (errorCode != null && Guidance.TryGetValue(errorCode, out var text))
            {
                return text;
            }

            return null;
        }
    }
}

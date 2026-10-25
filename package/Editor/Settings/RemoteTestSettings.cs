using UnityEditor;

namespace VRCRemoteTest
{
    /// <summary>
    /// Machine-local settings, never committed into Assets/ProjectSettings
    /// (spec section 43). Resolution precedence for headless/CI compatibility
    /// (Codex plan review Round 2, confidence 0.93):
    /// CLI flag &gt; environment variable &gt; EditorPrefs (interactive) &gt; default/fail.
    /// </summary>
    public static class RemoteTestSettings
    {
        private const string Prefix = "VRCRemoteTest_";

        private const string BridgeUrlKey = Prefix + "BridgeUrl";
        private const string BridgeFingerprintKey = Prefix + "BridgeFingerprint";

        private const string ResultTimeoutKey = Prefix + "ResultTimeoutSeconds";
        private const int DefaultResultTimeoutSeconds = 60;
        private const int MinResultTimeoutSeconds = 1;
        private const int MaxResultTimeoutSeconds = 3600;

        private const string PollIntervalKey = Prefix + "PollIntervalSeconds";
        private const int DefaultPollIntervalSeconds = 2;
        private const int MinPollIntervalSeconds = 1;
        private const int MaxPollIntervalSeconds = 60;

        /// <summary>
        /// Connection settings are machine-local. CI uses environment variables;
        /// the interactive editor uses EditorPrefs and the macOS Keychain.
        /// </summary>
        public static string BridgeUrl
        {
            get
            {
                var envValue = System.Environment.GetEnvironmentVariable("VRC_REMOTE_TEST_BRIDGE_URL");
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue;
                }

                var prefsValue = EditorPrefs.GetString(BridgeUrlKey, string.Empty);
                if (!string.IsNullOrEmpty(prefsValue))
                {
                    return prefsValue;
                }

                throw new RemoteBuildException(
                    ErrorCode.InvalidConfiguration,
                    "Bridge URL is not configured. Pair the Bridge in the VRC Remote Test window.");
            }
            set => EditorPrefs.SetString(BridgeUrlKey, value);
        }

        public static string BridgeFingerprint
        {
            get
            {
                var envValue = System.Environment.GetEnvironmentVariable("VRC_REMOTE_TEST_CERT_SHA256");
                return !string.IsNullOrEmpty(envValue)
                    ? envValue : EditorPrefs.GetString(BridgeFingerprintKey, string.Empty);
            }
            set => EditorPrefs.SetString(BridgeFingerprintKey, value);
        }

        public static string BridgeToken =>
            System.Environment.GetEnvironmentVariable("VRC_REMOTE_TEST_TOKEN")
            ?? BridgeTokenStore.Read(BridgeUrl);

        public static bool TryGetBridgeUrl(out string url)
        {
            try
            {
                url = BridgeUrl;
                return true;
            }
            catch (RemoteBuildException) { url = null; return false; }
        }

        /// <summary>
        /// Clamped at both write and read time (Codex plan review Phase 3,
        /// Round 3 confidence 0.95): a zero or negative PollIntervalSeconds
        /// turns PollForResultAsync into a tight spin loop or throws from
        /// Task.Delay, and these values can be set programmatically via
        /// EditorPrefs outside of any UI-side clamping.
        /// </summary>
        public static int ResultTimeoutSeconds
        {
            get => System.Math.Clamp(
                EditorPrefs.GetInt(ResultTimeoutKey, DefaultResultTimeoutSeconds),
                MinResultTimeoutSeconds, MaxResultTimeoutSeconds);
            set => EditorPrefs.SetInt(
                ResultTimeoutKey,
                System.Math.Clamp(value, MinResultTimeoutSeconds, MaxResultTimeoutSeconds));
        }

        public static int PollIntervalSeconds
        {
            get => System.Math.Clamp(
                EditorPrefs.GetInt(PollIntervalKey, DefaultPollIntervalSeconds),
                MinPollIntervalSeconds, MaxPollIntervalSeconds);
            set => EditorPrefs.SetInt(
                PollIntervalKey,
                System.Math.Clamp(value, MinPollIntervalSeconds, MaxPollIntervalSeconds));
        }

        // === Phase 5: Log Viewer ===

        private const string LogAutoRefreshKey = Prefix + "LogAutoRefresh";

        public static bool LogAutoRefresh
        {
            get => EditorPrefs.GetBool(LogAutoRefreshKey, true);
            set => EditorPrefs.SetBool(LogAutoRefreshKey, value);
        }

        // === Phase 5: Moonlight ===

        private const string MoonlightApplicationNameKey = Prefix + "MoonlightApplicationName";
        private const string DefaultMoonlightApplicationName = "Moonlight";
        private const int MaxMoonlightApplicationNameLength = 255;

        /// <summary>
        /// Clamped at write time to a safe app-name shape (Codex plan review
        /// Phase 5, Round 2, confidence 0.91): non-empty, no control
        /// characters, no "/" (this is an application name for `open -a`, not
        /// a path), bounded length. Invalid input falls back to the previous
        /// value rather than storing something MoonlightLauncher would reject
        /// at launch time anyway. MoonlightLauncher.Launch() re-validates
        /// defensively since EditorPrefs can be edited outside this setter.
        /// </summary>
        public static string MoonlightApplicationName
        {
            get
            {
                var value = EditorPrefs.GetString(MoonlightApplicationNameKey, DefaultMoonlightApplicationName);
                return string.IsNullOrWhiteSpace(value) ? DefaultMoonlightApplicationName : value;
            }
            set
            {
                if (IsValidMoonlightApplicationName(value))
                {
                    EditorPrefs.SetString(MoonlightApplicationNameKey, value);
                }
            }
        }

        internal static bool IsValidMoonlightApplicationName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (value.Length > MaxMoonlightApplicationNameLength)
            {
                return false;
            }

            if (value.Contains('/'))
            {
                return false;
            }

            foreach (var c in value)
            {
                if (char.IsControl(c))
                {
                    return false;
                }
            }

            return true;
        }

        private const string FocusMoonlightAfterDeployKey = Prefix + "FocusMoonlightAfterDeploy";

        public static bool FocusMoonlightAfterDeploy
        {
            get => EditorPrefs.GetBool(FocusMoonlightAfterDeployKey, false);
            set => EditorPrefs.SetBool(FocusMoonlightAfterDeployKey, value);
        }
    }
}

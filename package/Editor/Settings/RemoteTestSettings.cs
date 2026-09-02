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

        private const string SharePathKey = Prefix + "SharePath";
        private const string SharePathCliFlag = "-vrcRemoteTestSharePath";
        private const string SharePathEnvVar = "VRC_REMOTE_TEST_SHARE_PATH";

        private const string ResultTimeoutKey = Prefix + "ResultTimeoutSeconds";
        private const int DefaultResultTimeoutSeconds = 60;
        private const int MinResultTimeoutSeconds = 1;
        private const int MaxResultTimeoutSeconds = 3600;

        private const string PollIntervalKey = Prefix + "PollIntervalSeconds";
        private const int DefaultPollIntervalSeconds = 2;
        private const int MinPollIntervalSeconds = 1;
        private const int MaxPollIntervalSeconds = 60;

        /// <summary>
        /// No default value — headless invocation must fail fast if unset
        /// rather than silently falling back to a path that may not exist.
        /// </summary>
        public static string SharePath
        {
            get
            {
                if (VrcRemoteCommandLine.TryGetFlagValue(SharePathCliFlag, out var cliValue, out var cliError))
                {
                    return cliValue;
                }

                if (cliError != null)
                {
                    throw new RemoteBuildException(ErrorCode.InvalidConfiguration, cliError);
                }

                var envValue = System.Environment.GetEnvironmentVariable(SharePathEnvVar);
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue;
                }

                var prefsValue = EditorPrefs.GetString(SharePathKey, string.Empty);
                if (!string.IsNullOrEmpty(prefsValue))
                {
                    return prefsValue;
                }

                throw new RemoteBuildException(
                    ErrorCode.InvalidConfiguration,
                    $"SharePath is not configured. Set it via {SharePathCliFlag} <path>, " +
                    $"the {SharePathEnvVar} environment variable, or interactively via EditorPrefs.");
            }
            set => EditorPrefs.SetString(SharePathKey, value);
        }

        /// <summary>
        /// Non-throwing variant of <see cref="SharePath"/> for callers (e.g. the
        /// interactive UI) that need to handle "not configured" as a normal
        /// state rather than an exception.
        /// </summary>
        public static bool TryGetSharePath(out string path)
        {
            try
            {
                path = SharePath;
                return true;
            }
            catch (RemoteBuildException)
            {
                path = null;
                return false;
            }
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

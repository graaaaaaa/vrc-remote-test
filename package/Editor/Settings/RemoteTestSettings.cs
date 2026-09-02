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

        private const string PollIntervalKey = Prefix + "PollIntervalSeconds";
        private const int DefaultPollIntervalSeconds = 2;

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

        public static int ResultTimeoutSeconds
        {
            get => EditorPrefs.GetInt(ResultTimeoutKey, DefaultResultTimeoutSeconds);
            set => EditorPrefs.SetInt(ResultTimeoutKey, value);
        }

        public static int PollIntervalSeconds
        {
            get => EditorPrefs.GetInt(PollIntervalKey, DefaultPollIntervalSeconds);
            set => EditorPrefs.SetInt(PollIntervalKey, value);
        }
    }
}

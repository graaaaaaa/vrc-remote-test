using System.Diagnostics;
using Debug = UnityEngine.Debug;

namespace VRCRemoteTest
{
    /// <summary>
    /// Plain static class, not behind an interface (Codex plan review Phase 5,
    /// Round 2, confidence 0.94): exactly one caller (RemoteTestWindow), no
    /// BackgroundService/coordinator ever needs a dependency-injected seam, and
    /// Process.Start itself is not unit-testable anyway (matching Phase 4.1's
    /// VrchatLauncher precedent, which is only exercised indirectly via a Fake
    /// in coordinator tests, never tested directly). Same reasoning this
    /// project used to remove IHashCalculator/IBuildArtifactResolver in an
    /// earlier phase review.
    /// </summary>
    public static class MoonlightLauncher
    {
        /// <summary>
        /// Launches (or focuses, if already running) the named macOS
        /// application via `open -a`. Uses the absolute path to `open` rather
        /// than relying on PATH resolution, and ArgumentList (not a
        /// concatenated command string) so applicationName can never be
        /// interpreted as shell syntax (Codex plan review Phase 5, Round 2,
        /// confidence 0.91). Never throws — logs and returns on failure so a
        /// missing/misconfigured Moonlight install can't crash the window.
        /// </summary>
        public static void Launch(string applicationName)
        {
            if (!RemoteTestSettings.IsValidMoonlightApplicationName(applicationName))
            {
                Debug.LogError(
                    "[VRC Remote Test] Moonlight application name is invalid " +
                    "(empty, contains control characters, contains '/', or too long). " +
                    "Check Settings > Moonlight Application Name.");
                return;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add("-a");
                startInfo.ArgumentList.Add(applicationName);

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    Debug.LogError($"[VRC Remote Test] Failed to launch Moonlight ('{applicationName}').");
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError(
                    $"[VRC Remote Test] Failed to launch Moonlight ('{applicationName}'): {ex.Message}. " +
                    "Is Moonlight installed? Check Settings > Moonlight Application Name.");
            }
        }
    }
}

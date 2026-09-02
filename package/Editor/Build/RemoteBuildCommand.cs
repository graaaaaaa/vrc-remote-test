using System;
using UnityEditor;
using UnityEngine;

namespace VRCRemoteTest
{
    /// <summary>
    /// Two entry points sharing one coordinator instance (so the coordinator's
    /// in-process concurrency guard actually applies across both):
    ///
    /// - <see cref="ExecuteRemoteBuildHeadless"/>: for `-batchmode -executeMethod`
    ///   / CI. Blocks synchronously and sets the process exit code.
    /// - <see cref="ExecuteRemoteBuildFromMenu"/>: for the interactive Unity menu.
    ///   Fire-and-forget `async void` is fine here since there is no batch-mode
    ///   process lifecycle to race against.
    /// </summary>
    public static class RemoteBuildCommand
    {
        private static RemoteBuildCoordinator _coordinator;

        private static RemoteBuildCoordinator GetCoordinator()
        {
            if (_coordinator == null)
            {
                var sdkAdapter = new VrcSdkBuildAdapter();
                var transport = new SmbRemoteTransport(RemoteTestSettings.SharePath);
                _coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);
            }

            return _coordinator;
        }

        /// <summary>
        /// Headless/CI entry point:
        /// `Unity.exe -batchmode -quit -projectPath &lt;path&gt; -executeMethod VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless`.
        ///
        /// Deliberately synchronous (`.GetAwaiter().GetResult()`), not
        /// `async void`: Unity's -executeMethod machinery can consider the
        /// method "done" at the first incomplete await and let -quit proceed
        /// before an async continuation runs, so an async-void entry point is
        /// not reliable here (Codex plan review Round 3, confidence 0.84).
        /// </summary>
        public static void ExecuteRemoteBuildHeadless()
        {
            var exitCode = 1;
            try
            {
                var progress = new Progress<RemoteBuildProgress>(
                    p => Debug.Log($"[VRC Remote Test] [{p.Status}] {p.Message}"));
                var outcome = GetCoordinator().ExecuteRemoteBuildAsync(progress).GetAwaiter().GetResult();
                exitCode = outcome.Succeeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VRC Remote Test] Headless build failed: {ex}");
            }
            finally
            {
                EditorApplication.Exit(exitCode);
            }
        }

        [MenuItem("VRChat SDK/Remote Build")]
        public static async void ExecuteRemoteBuildFromMenu()
        {
            try
            {
                var progress = new Progress<RemoteBuildProgress>(
                    p => Debug.Log($"[VRC Remote Test] [{p.Status}] {p.Message}"));
                await GetCoordinator().ExecuteRemoteBuildAsync(progress);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VRC Remote Test] {ex}");
            }
        }
    }
}

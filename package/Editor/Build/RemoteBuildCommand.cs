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

        /// <summary>
        /// internal (not private) so RemoteTestWindow shares the same
        /// coordinator instance instead of constructing its own — two separate
        /// instances would each hold their own SemaphoreSlim, silently
        /// defeating the single-flight concurrency guard between the menu item
        /// and the window.
        /// </summary>
        internal static RemoteBuildCoordinator GetCoordinator()
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
        /// Call after SharePath changes so the next build picks up the new
        /// value — the coordinator caches its IRemoteTransport at construction
        /// time. Returns false (and leaves the existing coordinator in place)
        /// if a build is currently running: discarding it mid-flight would
        /// hand a new caller a fresh SemaphoreSlim, re-enabling the exact
        /// concurrency bug this internal-sharing design exists to prevent
        /// (Codex plan review Phase 3, confidence 0.95).
        /// </summary>
        internal static bool InvalidateCoordinator()
        {
            if (_coordinator != null && _coordinator.IsRunning)
            {
                return false;
            }

            _coordinator = null;
            return true;
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

using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VRCRemoteTest
{
    /// <summary>
    /// Two entry points sharing one coordinator instance (so the coordinator's
    /// in-process concurrency guard actually applies across both):
    ///
    /// - <see cref="ExecuteRemoteBuildHeadless"/>: for `-batchmode -executeMethod`
    ///   / CI. Keeps the Editor running until the async SDK build finishes.
    /// - <see cref="ExecuteRemoteBuildFromMenu"/>: for the interactive Unity menu.
    ///   Fire-and-forget `async void` is fine here since there is no batch-mode
    ///   process lifecycle to race against.
    /// </summary>
    public static class RemoteBuildCommand
    {
        private static RemoteBuildCoordinator _coordinator;
        private static bool _headlessStarted;

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
                var transport = new LanRemoteTransport(
                    RemoteTestSettings.BridgeUrl,
                    RemoteTestSettings.BridgeFingerprint,
                    RemoteTestSettings.BridgeToken);
                _coordinator = new RemoteBuildCoordinator(sdkAdapter, transport)
                {
                    ResultWriter = DeployResultWriter.CreateDefault(),
                    PackageVersion = PackageVersionInfo.Current,
                };
            }

            return _coordinator;
        }

        /// <summary>
        /// The shared coordinator if one exists, without creating it (creating one reads the
        /// pairing, which throws when the Bridge is not set up).
        /// </summary>
        internal static RemoteBuildCoordinator PeekCoordinator() => _coordinator;

        /// <summary>
        /// Call after pairing changes so the next build picks up the new
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
        /// `Unity.exe -batchmode -projectPath &lt;path&gt; -executeMethod VRCRemoteTest.RemoteBuildCommand.ExecuteRemoteBuildHeadless`.
        ///
        /// Do not pass -quit: the SDK awaits on Unity's synchronization context
        /// while exporting a scene, so blocking this thread deadlocks the build.
        /// RunHeadlessAsync calls EditorApplication.Exit when it has a result.
        ///
        /// Optional, from the packaged runner: VRC_REMOTE_TEST_RUN_DIR (the run folder that gets a
        /// copy of the deploy result) and VRC_REMOTE_TEST_WORLD_BUILD_TAG (see
        /// RemoteBuildApi.ActiveWorldBuildTag).
        /// </summary>
        public static void ExecuteRemoteBuildHeadless()
        {
            if (_headlessStarted) return;
            _headlessStarted = true;
            RunHeadlessAsync();
        }

        private static async void RunHeadlessAsync()
        {
            var exitCode = 1;
            try
            {
                var scenePath = Environment.GetEnvironmentVariable("VRC_REMOTE_TEST_SCENE_PATH");
                if (!string.IsNullOrWhiteSpace(scenePath))
                {
                    if (!scenePath.StartsWith("Assets/", StringComparison.Ordinal)
                        || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                        || scenePath.Contains("..")
                        || AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                        throw new ArgumentException("VRC_REMOTE_TEST_SCENE_PATH must name a saved scene under Assets/.");
                    EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                }

                var progress = new Progress<RemoteBuildProgress>(
                    p => Debug.Log($"[VRC Remote Test] [{p.Status}] {p.Message}"));
                var coordinator = GetCoordinator();
                if (Application.isBatchMode && coordinator.IsBridgeReachable == false)
                    coordinator.ProbeBridgeForHeadless();
                var request = new RemoteBuildRequest
                {
                    RunDirectory = EnvironmentOrNull("VRC_REMOTE_TEST_RUN_DIR"),
                    WorldBuildTag = EnvironmentOrNull("VRC_REMOTE_TEST_WORLD_BUILD_TAG"),
                };
                var outcome = await coordinator.ExecuteRemoteBuildCoreAsync(request, progress, default);
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

        private static string EnvironmentOrNull(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
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

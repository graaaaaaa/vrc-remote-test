using System.Threading.Tasks;
using UnityEditor;
using VRC.SDK3.Editor;
using VRC.SDKBase.Editor;

namespace VRCRemoteTest
{
    /// <summary>
    /// The sole class that touches VRChat SDK types (spec section 8). If the
    /// SDK's Public API changes, only this file should need updating.
    /// </summary>
    public sealed class VrcSdkBuildAdapter : IVrcSdkBuildAdapter
    {
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 500;

        public bool IsAvailable => VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out _);

        public async Task<string> BuildWindowsWorldAsync()
        {
            EnsureBuildTarget();

            // ConfigureAwait(false) throughout: the headless entry point
            // (RemoteBuildCommand.ExecuteRemoteBuildHeadless) blocks Unity's
            // main thread synchronously via .GetAwaiter().GetResult(). Any
            // await here that tried to resume on the captured
            // SynchronizationContext (the main thread) would deadlock against
            // that same blocked thread. The trade-off: if TryGetBuilder needs
            // a retry (SDK Control Panel not yet open), the subsequent
            // TryGetBuilder/Build() calls may then run on a thread-pool thread
            // rather than the main thread. On the happy path (panel already
            // open, no retry) everything still runs synchronously on the
            // caller's thread up to the first await, so this only matters for
            // the retry path. Needs real-machine verification against the
            // actual SDK — see docs/sdk-api-notes.md.
            var builder = await ObtainBuilderAsync().ConfigureAwait(false);

            try
            {
                return await builder.Build().ConfigureAwait(false);
            }
            catch (BuildBlockedException ex)
            {
                throw new RemoteBuildException(
                    ErrorCode.SdkBuildFailed, $"Build blocked by SDK callback: {ex.Message}", ex);
            }
            catch (ValidationException ex)
            {
                var errors = string.Join("; ", ex.Errors);
                throw new RemoteBuildException(
                    ErrorCode.SdkBuildFailed, $"SDK validation failed: {errors}", ex);
            }
            catch (BuilderException ex)
            {
                throw new RemoteBuildException(
                    ErrorCode.SdkBuildFailed, $"SDK builder error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Does not switch the target automatically — SwitchActiveBuildTarget
        /// can trigger a domain reload that would corrupt an in-flight async
        /// Build() call. The developer must switch manually; this only detects
        /// and reports the mismatch (spec section 11's preflight philosophy).
        /// </summary>
        private static void EnsureBuildTarget()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new RemoteBuildException(
                    ErrorCode.InvalidBuildTarget,
                    "Active build target is not StandaloneWindows64. Switch it manually " +
                    "(File > Build Settings > PC, Mac & Linux Standalone > Windows) before running a remote build.");
            }
        }

        private static async Task<IVRCSdkWorldBuilderApi> ObtainBuilderAsync()
        {
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkWorldBuilderApi>(out var builder))
                {
                    return builder;
                }

                if (attempt == 0)
                {
                    EditorWindow.GetWindow(typeof(VRCSdkControlPanel));
                }

                await Task.Delay(RetryDelayMs).ConfigureAwait(false);
            }

            throw new RemoteBuildException(
                ErrorCode.SdkNotAvailable,
                "Could not obtain VRChat SDK world builder. " +
                "Ensure the VRChat SDK is installed and the SDK Control Panel can open.");
        }
    }
}

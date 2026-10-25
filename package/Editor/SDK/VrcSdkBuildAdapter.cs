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

            // The SDK builder and scene lookup are Unity APIs. Keep the
            // synchronization context across retries so they run on the
            // Editor main thread in both interactive and batch mode.
            var builder = await ObtainBuilderAsync();

            // The SDK public API can return a builder before its Builder tab has
            // populated the scene list. IsValidBuilder refreshes that list; without
            // it Build() reports "Open the SDK panel" even when the panel is open.
            if (!builder.IsValidBuilder(out var validationMessage))
            {
                throw new RemoteBuildException(
                    ErrorCode.SdkBuildFailed,
                    $"SDK world builder is not ready: {validationMessage}");
            }

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

                await Task.Delay(RetryDelayMs);
            }

            throw new RemoteBuildException(
                ErrorCode.SdkNotAvailable,
                "Could not obtain VRChat SDK world builder. " +
                "Ensure the VRChat SDK is installed and the SDK Control Panel can open.");
        }
    }
}

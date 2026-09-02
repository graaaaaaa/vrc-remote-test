using System.Threading.Tasks;

namespace VRCRemoteTest
{
    /// <summary>
    /// Isolates all VRChat SDK-touching code (spec section 8). No other class
    /// may reference IVRCSdkWorldBuilderApi, VRCSdkControlPanel, or any other
    /// SDK type directly. Real testability boundary kept as an interface
    /// (Codex plan review Round 3, confidence 0.92) — coordinator tests fake
    /// this rather than driving the actual SDK.
    /// </summary>
    public interface IVrcSdkBuildAdapter
    {
        /// <summary>
        /// Whether the VRChat SDK world builder is currently obtainable
        /// (checks TryGetBuilder availability without side effects).
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Obtains the SDK builder (opening the SDK Control Panel and retrying
        /// if needed) and invokes Build(). Returns the full filesystem path to
        /// the produced .vrcw file.
        /// </summary>
        /// <remarks>
        /// No CancellationToken parameter: VRChat SDK's underlying
        /// IVRCSdkWorldBuilderApi.Build() has no cancellation support and
        /// cannot be aborted once called (confirmed against
        /// docs/sdk-api-notes.md; Codex plan review Round 2, confidence 0.95).
        /// Callers may only check cancellation before and after calling this.
        /// </remarks>
        Task<string> BuildWindowsWorldAsync();
    }
}

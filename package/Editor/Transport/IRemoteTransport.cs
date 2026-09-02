using System.Threading;
using System.Threading.Tasks;

namespace VRCRemoteTest
{
    /// <summary>
    /// Real testability boundary (Codex plan review Round 3, confidence 0.92):
    /// the coordinator's tests substitute a fake here rather than touching a
    /// real SMB mount.
    /// </summary>
    public interface IRemoteTransport
    {
        /// <summary>Whether the remote share is mounted and writable.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Uploads the artifact and manifest to the remote staging area using
        /// the atomic .part-then-rename protocol (spec section 17).
        /// </summary>
        Task UploadBuildAsync(
            BuildArtifact artifact,
            BuildManifest manifest,
            string buildId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Polls for a result file at results/{buildId}.json. Returns null if
        /// the file does not exist yet or could not be read (transient I/O
        /// race with the Bridge's own atomic write) — callers should keep
        /// polling in that case, not treat it as failure.
        /// </summary>
        BuildResult PollResult(string buildId);
    }
}

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

        /// <summary>
        /// Reads the Bridge's continuously-updated status/vrchat-status.json
        /// (Phase 4a). Returns null if the file does not exist, could not be
        /// read, or exceeds the size guard — callers treat this the same as
        /// "unknown", never as an error.
        /// </summary>
        VrchatStatus PollVrchatStatus();

        /// <summary>
        /// Reads the Bridge's continuously-republished logs/vrchat-latest.log
        /// (Phase 5). Returns null if the file does not exist, could not be
        /// read, or exceeds the size guard — callers treat this the same as
        /// "no log available", never as an error.
        /// </summary>
        string PollVrchatLog();
    }
}

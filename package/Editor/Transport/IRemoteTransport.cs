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

        /// <summary>
        /// Reads status/vrchat-clients.json (Bridge v0.6.0 and later): one entry per
        /// slot. Returns null if the file does not exist (an older Bridge), could not be
        /// read or parsed, or exceeds the size guard — callers then show
        /// vrchat-latest.log alone, as before.
        /// </summary>
        VrchatClients PollVrchatClients();

        /// <summary>
        /// Reads logs/clients/slot-{slot}.log: that client's last 200 lines, or why it is
        /// not shown. Null under the same conditions as <see cref="PollVrchatLog"/>.
        /// </summary>
        string PollClientLog(int slot);

        /// <summary>
        /// Reads logs/clients/slot-{slot}.markers.log: the marker lines of that client's
        /// session, up to 1 MiB, with a size guard of its own. Null when missing,
        /// unreadable or over the guard.
        /// </summary>
        string PollClientMarkers(int slot);
    }
}

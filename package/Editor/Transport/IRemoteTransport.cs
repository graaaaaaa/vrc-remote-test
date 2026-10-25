using System.Threading;
using System.Threading.Tasks;

namespace VRCRemoteTest
{
    /// <summary>
    /// Real testability boundary (Codex plan review Round 3, confidence 0.92):
    /// the coordinator's tests substitute a fake here rather than touching a
    /// real network endpoint.
    /// </summary>
    public interface IRemoteTransport
    {
        /// <summary>Last observed connection status, for display only.</summary>
        bool IsAvailable { get; }

        /// <summary>Authenticate and check API/protocol versions before building.</summary>
        Task ProbeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads and commits the artifact over authenticated HTTPS.
        /// </summary>
        Task UploadBuildAsync(
            BuildArtifact artifact,
            BuildManifest manifest,
            string buildId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns null until the Bridge has published a result.
        /// </summary>
        Task<BuildResult> PollResultAsync(string buildId, CancellationToken cancellationToken = default);

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

        /// <summary>Save a redacted, complete snapshot of the selected client log.</summary>
        Task DownloadFullLogAsync(int slot, long clientGeneration, long observationEpoch,
            string destinationPath, CancellationToken cancellationToken = default);
    }
}

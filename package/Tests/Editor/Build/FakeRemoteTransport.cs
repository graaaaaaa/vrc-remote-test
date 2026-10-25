using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VRCRemoteTest.Tests
{
    internal sealed class FakeRemoteTransport : IRemoteTransport
    {
        public bool IsAvailable { get; set; } = true;
        public Task ProbeAsync(CancellationToken cancellationToken = default) =>
            IsAvailable ? Task.CompletedTask : Task.FromException(
                new RemoteBuildException(ErrorCode.BridgeUnavailable, "Bridge unavailable."));
        public bool UploadCalled { get; private set; }
        public string LastUploadedBuildId { get; private set; }

        /// <summary>
        /// Preferred over ResultQueue when set: builds the result dynamically
        /// from the actual (coordinator-generated) buildId, since tests can't
        /// predict that value ahead of time.
        /// </summary>
        public Func<string, BuildResult> ResultFactory { get; set; }

        /// <summary>
        /// Results returned by successive PollResult calls when ResultFactory
        /// is not set. Used for the "never becomes ready" timeout case.
        /// </summary>
        public Queue<BuildResult> ResultQueue { get; } = new Queue<BuildResult>();

        public Task UploadBuildAsync(
            BuildArtifact artifact, BuildManifest manifest, string buildId,
            CancellationToken cancellationToken = default)
        {
            UploadCalled = true;
            LastUploadedBuildId = buildId;
            return Task.CompletedTask;
        }

        public Task<BuildResult> PollResultAsync(string buildId, CancellationToken cancellationToken = default)
        {
            if (ResultFactory != null)
            {
                return Task.FromResult(ResultFactory(buildId));
            }

            return Task.FromResult(ResultQueue.Count > 0 ? ResultQueue.Dequeue() : null);
        }

        public VrchatStatus VrchatStatusToReturn { get; set; }

        public VrchatStatus PollVrchatStatus() => VrchatStatusToReturn;

        public string VrchatLogToReturn { get; set; }

        public string PollVrchatLog() => VrchatLogToReturn;

        public VrchatClients VrchatClientsToReturn { get; set; }

        public VrchatClients PollVrchatClients() => VrchatClientsToReturn;

        public Dictionary<int, string> ClientLogs { get; } = new Dictionary<int, string>();

        public string PollClientLog(int slot) => ClientLogs.TryGetValue(slot, out var log) ? log : null;

        public Dictionary<int, string> ClientMarkers { get; } = new Dictionary<int, string>();

        public string PollClientMarkers(int slot) => ClientMarkers.TryGetValue(slot, out var markers) ? markers : null;

        public Task DownloadFullLogAsync(int slot, long clientGeneration, long observationEpoch,
            string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

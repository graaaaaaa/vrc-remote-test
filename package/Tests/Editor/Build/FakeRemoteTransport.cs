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

        /// <summary>What GetMaxArtifactSizeBytesAsync answers; null (the default) skips the size check.</summary>
        public long? MaxArtifactSizeBytes { get; set; }

        public Task<long?> GetMaxArtifactSizeBytesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(MaxArtifactSizeBytes);

        public BridgeInfo BridgeInfoToReturn { get; set; }

        public BridgeInfo PollBridgeInfo() => BridgeInfoToReturn;

        // Management API answers. Left unset, a call gets the answer every transport without
        // the API gives.
        private static Task<BridgeAdminResponse> NotSupported() =>
            Task.FromResult(BridgeAdminResponse.NotSupported(nameof(FakeRemoteTransport)));

        private static Task<BridgeAdminResponse> Answer(BridgeAdminResponse response) =>
            response == null ? NotSupported() : Task.FromResult(response);

        public static BridgeAdminResponse Json(int status, string body) => new BridgeAdminResponse(true, status, body, null);

        /// <summary>Answers to successive GET /bridge calls; the last one repeats.</summary>
        public Queue<BridgeAdminResponse> BridgeInfoResponses { get; } = new Queue<BridgeAdminResponse>();

        public int BridgeInfoCalls { get; private set; }

        public BridgeAdminResponse ProfilesResponse { get; set; }

        public BridgeAdminResponse ConfigResponse { get; set; }

        public BridgeAdminResponse PutConfigResponse { get; set; }

        public BridgeAdminResponse CloseResponse { get; set; }

        public int PutConfigCalls { get; private set; }

        public string LastPutEtag { get; private set; }

        public string LastPutPatch { get; private set; }

        /// <summary>When set, answers GET /bridge instead of <see cref="BridgeInfoResponses"/>, e.g. never until cancelled.</summary>
        public Func<CancellationToken, Task<BridgeAdminResponse>> BridgeInfoHandler { get; set; }

        /// <summary>When set, answers PUT /config instead of <see cref="PutConfigResponse"/>.</summary>
        public Func<CancellationToken, Task<BridgeAdminResponse>> PutConfigHandler { get; set; }

        /// <summary>An answer that comes only when the call is cancelled, like a Bridge that stopped answering.</summary>
        public static async Task<BridgeAdminResponse> NeverAnswers(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }

        private BridgeAdminResponse _lastBridgeInfo;

        public Task<BridgeAdminResponse> GetBridgeInfoAsync(CancellationToken cancellationToken = default)
        {
            BridgeInfoCalls++;
            if (BridgeInfoHandler != null)
            {
                return BridgeInfoHandler(cancellationToken);
            }

            if (BridgeInfoResponses.Count > 0)
            {
                _lastBridgeInfo = BridgeInfoResponses.Dequeue();
            }

            return Answer(_lastBridgeInfo);
        }

        public Task<BridgeAdminResponse> GetProfilesAsync(CancellationToken cancellationToken = default) => Answer(ProfilesResponse);

        public Task<BridgeAdminResponse> GetConfigAsync(CancellationToken cancellationToken = default) => Answer(ConfigResponse);

        public Task<BridgeAdminResponse> PutConfigAsync(string etag, string mergePatchJson,
            CancellationToken cancellationToken = default)
        {
            PutConfigCalls++;
            LastPutEtag = etag;
            LastPutPatch = mergePatchJson;
            return PutConfigHandler != null ? PutConfigHandler(cancellationToken) : Answer(PutConfigResponse);
        }

        public Task<BridgeAdminResponse> RestartBridgeAsync(CancellationToken cancellationToken = default) => NotSupported();

        public Task<BridgeAdminResponse> CloseClientsAsync(CancellationToken cancellationToken = default) => Answer(CloseResponse);
    }
}

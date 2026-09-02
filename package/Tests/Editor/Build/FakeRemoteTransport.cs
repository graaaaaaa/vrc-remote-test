using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VRCRemoteTest.Tests
{
    internal sealed class FakeRemoteTransport : IRemoteTransport
    {
        public bool IsAvailable { get; set; } = true;
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

        public BuildResult PollResult(string buildId)
        {
            if (ResultFactory != null)
            {
                return ResultFactory(buildId);
            }

            return ResultQueue.Count > 0 ? ResultQueue.Dequeue() : null;
        }

        public VrchatStatus VrchatStatusToReturn { get; set; }

        public VrchatStatus PollVrchatStatus() => VrchatStatusToReturn;
    }
}

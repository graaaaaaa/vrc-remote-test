using System.Threading.Tasks;

namespace VRCRemoteTest.Tests
{
    internal sealed class FakeVrcSdkBuildAdapter : IVrcSdkBuildAdapter
    {
        public bool IsAvailable { get; set; } = true;
        public string ReturnedPath { get; set; }
        public RemoteBuildException ThrowOnBuild { get; set; }
        public int BuildCallCount { get; private set; }

        /// <summary>
        /// When set, BuildWindowsWorldAsync awaits this instead of returning
        /// immediately — lets a test hold a build "in flight" to exercise the
        /// concurrency guard.
        /// </summary>
        public Task<string> BuildTaskOverride { get; set; }

        /// <summary>Runs inside BuildWindowsWorldAsync, as a project's build preprocessing would.</summary>
        public System.Action DuringBuild { get; set; }

        public Task<string> BuildWindowsWorldAsync()
        {
            BuildCallCount++;
            DuringBuild?.Invoke();

            if (ThrowOnBuild != null)
            {
                throw ThrowOnBuild;
            }

            return BuildTaskOverride ?? Task.FromResult(ReturnedPath);
        }
    }
}

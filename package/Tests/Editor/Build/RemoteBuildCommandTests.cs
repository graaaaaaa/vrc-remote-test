using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>
    /// Exercises RemoteBuildCommand's static coordinator lifecycle, which is
    /// what shares a single SemaphoreSlim between the menu item and
    /// RemoteTestWindow. Relies on InternalsVisibleTo("VRCRemoteTest.Tests.Editor")
    /// (package/Editor/AssemblyInfo.cs) to reach the internal API surface.
    ///
    /// Deliberately does NOT call RemoteBuildCommand.GetCoordinator() in these
    /// tests: it constructs a real SmbRemoteTransport against
    /// RemoteTestSettings.SharePath, which depends on this machine's
    /// EditorPrefs/CLI/env configuration rather than test-controlled state.
    /// The IsRunning-guard branch itself is exercised directly against
    /// RemoteBuildCoordinator with fakes in RemoteBuildCoordinatorTests
    /// (IsRunning_reflects_in_flight_build); what's specific to
    /// RemoteBuildCommand and safe to test here is the no-coordinator-yet
    /// short-circuit path, which never touches SharePath.
    /// </summary>
    public class RemoteBuildCommandTests
    {
        [TearDown]
        public void TearDown()
        {
            RemoteBuildCommand.InvalidateCoordinator();
        }

        [Test]
        public void InvalidateCoordinator_returns_true_when_no_coordinator_exists_yet()
        {
            RemoteBuildCommand.InvalidateCoordinator();

            var invalidated = RemoteBuildCommand.InvalidateCoordinator();

            Assert.IsTrue(invalidated);
        }
    }
}

using FluentAssertions;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class BridgeInstanceLockTests : IDisposable
{
    private readonly string _stateDir;

    public BridgeInstanceLockTests()
    {
        _stateDir = Path.Combine(Directory.CreateTempSubdirectory("vrc-remote-test-lock-").FullName, "state");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_stateDir)!, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_second_Bridge_cannot_acquire_the_lock_while_the_first_holds_it()
    {
        using var first = BridgeInstanceLock.TryAcquire(_stateDir);
        using var second = BridgeInstanceLock.TryAcquire(_stateDir);

        first.Should().NotBeNull();
        second.Should().BeNull();
        File.Exists(BridgeInstanceLock.PathFor(_stateDir)).Should().BeTrue();
    }

    [Fact]
    public void The_lock_can_be_acquired_again_after_it_is_released()
    {
        BridgeInstanceLock.TryAcquire(_stateDir)!.Dispose();

        using var again = BridgeInstanceLock.TryAcquire(_stateDir);

        again.Should().NotBeNull("a lock file left behind by a previous run is reused, not a reason to refuse");
    }
}

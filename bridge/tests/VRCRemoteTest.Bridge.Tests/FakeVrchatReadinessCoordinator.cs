using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatReadinessCoordinator : IVrchatReadinessCoordinator
{
    public ReadinessResult ResultToReturn { get; set; } = ReadinessResult.Ready();
    public Exception? ThrowOnEnsureReady { get; set; }
    public bool EnsureReadyCalled { get; private set; }

    /// <summary>Runs while the watcher waits for readiness, i.e. between validation and install.</summary>
    public Action? OnEnsureReady { get; set; }

    public Task<ReadinessResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        EnsureReadyCalled = true;
        OnEnsureReady?.Invoke();

        if (ThrowOnEnsureReady is not null)
        {
            throw ThrowOnEnsureReady;
        }

        return Task.FromResult(ResultToReturn);
    }
}

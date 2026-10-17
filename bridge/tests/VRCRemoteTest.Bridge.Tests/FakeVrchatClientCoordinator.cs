using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatClientCoordinator : IVrchatClientCoordinator
{
    public VrchatPreparation PreparationToReturn { get; set; } = VrchatPreparation.DeployOnly();
    public Exception? ThrowOnPrepare { get; set; }
    public bool PrepareCalled { get; private set; }

    /// <summary>Runs while the watcher waits for readiness, i.e. between validation and install.</summary>
    public Action? OnPrepare { get; set; }

    public VrchatLaunchOutcome LaunchOutcomeToReturn { get; set; } = VrchatLaunchOutcome.Success();
    public Exception? ThrowOnLaunch { get; set; }
    public List<string> LaunchedWorldPaths { get; } = [];

    /// <summary>Runs when launching, i.e. after install and archive.</summary>
    public Action? OnLaunch { get; set; }

    public Task<VrchatPreparation> PrepareAsync(CancellationToken cancellationToken)
    {
        PrepareCalled = true;
        OnPrepare?.Invoke();

        if (ThrowOnPrepare is not null)
        {
            throw ThrowOnPrepare;
        }

        return Task.FromResult(PreparationToReturn);
    }

    public Task<VrchatLaunchOutcome> LaunchClientsAsync(string deployedWorldPath, CancellationToken cancellationToken)
    {
        LaunchedWorldPaths.Add(deployedWorldPath);
        OnLaunch?.Invoke();

        if (ThrowOnLaunch is not null)
        {
            throw ThrowOnLaunch;
        }

        return Task.FromResult(LaunchOutcomeToReturn);
    }
}

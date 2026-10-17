using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatLauncher : IVrchatLauncher
{
    private int _nextProcessId = 5000;

    public bool ShouldSucceed { get; set; } = true;

    /// <summary>When set, only launches with these zero-based indexes fail.</summary>
    public HashSet<int>? FailingLaunchIndexes { get; set; }

    public Exception? ThrowOnLaunch { get; set; }

    public List<VrchatLaunchRequest> Requests { get; } = [];

    public bool LaunchCalled => Requests.Count > 0;

    /// <summary>
    /// Lets a test simulate side effects of a successful launch (e.g. the process
    /// monitor starting to see the process). Receives the launched PID.
    /// </summary>
    public Action<int>? OnLaunch { get; set; }

    public Task<LaunchResult> LaunchAsync(VrchatLaunchRequest request)
    {
        var index = Requests.Count;
        Requests.Add(request);

        if (ThrowOnLaunch is not null)
        {
            throw ThrowOnLaunch;
        }

        var succeeds = FailingLaunchIndexes is null ? ShouldSucceed : !FailingLaunchIndexes.Contains(index);
        if (!succeeds)
        {
            return Task.FromResult(LaunchResult.Failure("simulated launch failure"));
        }

        var processId = _nextProcessId++;
        OnLaunch?.Invoke(processId);
        return Task.FromResult(LaunchResult.Success(processId, DateTime.UtcNow));
    }
}

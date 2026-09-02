using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

internal sealed class FakeVrchatLauncher : IVrchatLauncher
{
    public bool ShouldSucceed { get; set; } = true;
    public bool LaunchCalled { get; private set; }
    public string? LastExecutablePath { get; private set; }
    public string? LastMode { get; private set; }

    /// <summary>Lets a test simulate side effects of a successful launch (e.g. the process monitor starting to see the process).</summary>
    public Action? OnLaunch { get; set; }

    public Task<bool> LaunchAsync(string executablePath, string mode)
    {
        LaunchCalled = true;
        LastExecutablePath = executablePath;
        LastMode = mode;

        if (ShouldSucceed)
        {
            OnLaunch?.Invoke();
        }

        return Task.FromResult(ShouldSucceed);
    }
}

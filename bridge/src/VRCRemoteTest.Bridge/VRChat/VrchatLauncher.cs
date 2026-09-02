using System.Diagnostics;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Launch arguments mirror scripts/start-vrchat-dev.ps1 exactly: --watch-worlds is
/// always present, --no-vr is conditional on mode, the remaining three debug flags
/// are always passed. ProcessStartInfo.ArgumentList (not a concatenated command
/// string) with UseShellExecute=false avoids any shell/argument injection -- every
/// argument here is a hardcoded literal, none are user- or config-supplied (Codex
/// plan review Phase 4.1, Round 1 Security, confidence 0.86).
/// </summary>
public sealed class VrchatLauncher : IVrchatLauncher
{
    public Task<bool> LaunchAsync(string executablePath, string mode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("--watch-worlds");

        if (!string.Equals(mode, "VR", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--no-vr");
        }

        startInfo.ArgumentList.Add("--enable-debug-gui");
        startInfo.ArgumentList.Add("--enable-sdk-log-levels");
        startInfo.ArgumentList.Add("--enable-udon-debug-logging");

        // Process.Start(ProcessStartInfo) is documented as able to return null in
        // rare process-reuse scenarios, which cannot happen with UseShellExecute
        // = false and a concrete path, but the null check is cheap insurance
        // (Codex plan review Phase 4.1, Round 2, confidence 0.90). The handle is
        // disposed immediately: VrchatLauncher doesn't need to track or wait on
        // it -- IVrchatProcessMonitor rediscovers the running process
        // independently on its next poll.
        using var process = Process.Start(startInfo);
        return Task.FromResult(process is not null);
    }
}

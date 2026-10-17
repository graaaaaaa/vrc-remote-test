using System.ComponentModel;
using System.Diagnostics;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Launch arguments mirror the SDK's Build &amp; Test (RunWorldTestDesktop in SDK 3.10.4)
/// and scripts/start-vrchat-dev.ps1: --url opens the deployed world directly, so a cold
/// start does not have to wait for --watch-worlds, which is always added for the
/// reloads of later builds. ProcessStartInfo.ArgumentList (not a concatenated command
/// string) with UseShellExecute=false avoids any shell/argument injection; the only
/// variable parts are the Bridge-built world path (percent-encoded) and the Bridge's own
/// room id (Codex plan review Phase 4.1, Round 1 Security, confidence 0.86).
/// </summary>
public sealed class VrchatLauncher : IVrchatLauncher
{
    public Task<LaunchResult> LaunchAsync(VrchatLaunchRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,

            // Same as the SDK: VRChat is started from its own directory.
            WorkingDirectory = Path.GetDirectoryName(request.ExecutablePath) ?? string.Empty,
        };

        foreach (var argument in BuildArguments(request))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            // Process.Start(ProcessStartInfo) is documented as able to return null in
            // rare process-reuse scenarios, which cannot happen with UseShellExecute
            // = false and a concrete path, but the null check is cheap insurance
            // (Codex plan review Phase 4.1, Round 2, confidence 0.90).
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Task.FromResult(LaunchResult.Failure("Process.Start returned no process."));
            }

            return Task.FromResult(LaunchResult.Success(process.Id, TryGetCreationTimeUtc(process)));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return Task.FromResult(LaunchResult.Failure(ex.Message));
        }
    }

    /// <summary>
    /// Pure, for tests. The world path is percent-encoded as a whole (":" "\" " " "&amp;"
    /// included), so nothing in it can end the --url value or add a query parameter.
    /// --profile is never passed: every client uses profile 0, i.e. the same account.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(VrchatLaunchRequest request)
    {
        if (request.RoomId.Length == 0 || !request.RoomId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("RoomId must consist of ASCII digits.", nameof(request));
        }

        var arguments = new List<string>
        {
            $"--url=create?roomId={request.RoomId}&hidden=true&name=BuildAndRun"
                + $"&url=file:///{Uri.EscapeDataString(request.WorldPath)}",
            "--watch-worlds",
        };

        if (request.NoVr)
        {
            arguments.Add("--no-vr");
        }

        arguments.Add("--enable-debug-gui");
        arguments.Add("--enable-sdk-log-levels");
        arguments.Add("--enable-udon-debug-logging");
        return arguments;
    }

    private static DateTime? TryGetCreationTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}

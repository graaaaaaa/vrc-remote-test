using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Logging;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// The sole class in this project touching Process enumeration and System.Management
/// (WMI). Process.GetProcessesByName is safe to call cross-platform (returns an empty
/// array on non-Windows rather than throwing), but the --watch-worlds command-line
/// detection is Windows-only and guarded accordingly -- verified via a macOS build/test
/// spike plus a win-x64 self-contained single-file publish spike (Codex plan review
/// Phase 4a). Real WMI runtime behavior on Windows is confirmed during the real-hardware
/// verification step, not by this class's own design.
/// </summary>
public sealed class VrchatProcessMonitor : IVrchatProcessMonitor
{
    private const string ProcessName = "VRChat";
    private const string WatchWorldsArgument = "--watch-worlds";

    private readonly ILogger<VrchatProcessMonitor> _logger;

    public VrchatProcessMonitor(ILogger<VrchatProcessMonitor> logger)
    {
        _logger = logger;
    }

    public VrchatStatus GetStatus()
    {
        var processes = Process.GetProcessesByName(ProcessName);

        try
        {
            if (processes.Length == 0)
            {
                return new VrchatStatus
                {
                    IsRunning = false,
                    WatchWorldsDetected = false,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
            }

            if (processes.Length > 1)
            {
                _logger.LogWarning(
                    "Multiple VRChat processes detected (PIDs: {Pids}). Selecting the most relevant one.",
                    string.Join(", ", Array.ConvertAll(processes, p => p.Id)));
            }

            var selected = SelectProcess(processes);

            return new VrchatStatus
            {
                IsRunning = true,
                WatchWorldsDetected = selected.WatchWorldsDetected,
                ProcessId = selected.Process.Id,
                StartTimeUtc = TryGetStartTime(selected.Process),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Multiple VRChat.exe instances are possible (zombie process, manual double
    /// launch). Array order from GetProcessesByName is undefined, so this picks
    /// deterministically: prefer a process confirmed to have --watch-worlds, then
    /// the most recently started (Codex plan review Phase 4a, Round 3, confidence 0.90).
    /// </summary>
    private (Process Process, bool WatchWorldsDetected) SelectProcess(Process[] processes)
    {
        (Process Process, bool WatchWorldsDetected, DateTimeOffset? StartTime)? best = null;

        foreach (var process in processes)
        {
            var watchWorlds = TryDetectWatchWorlds(process.Id);
            var startTime = TryGetStartTime(process);

            if (best is null
                || (watchWorlds && !best.Value.WatchWorldsDetected)
                || (watchWorlds == best.Value.WatchWorldsDetected && startTime > best.Value.StartTime))
            {
                best = (process, watchWorlds, startTime);
            }
        }

        return (best!.Value.Process, best.Value.WatchWorldsDetected);
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// WMI query failure (permissions, WMI service unavailable, transient COM error)
    /// collapses to "not detected" rather than a distinct third state -- Phase 4a's
    /// VrchatStatus model is deliberately two-valued (Codex plan review, Round 3,
    /// confidence 0.88). A richer diagnostic state can be added later if actually needed.
    /// </summary>
    private bool TryDetectWatchWorlds(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={processId}");
            using var results = searcher.Get();

            foreach (var resultObj in results)
            {
                using var managementObject = (ManagementBaseObject)resultObj;
                var commandLine = managementObject["CommandLine"] as string;
                return commandLine is not null
                    && commandLine.Contains(WatchWorldsArgument, StringComparison.Ordinal);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to query command line for VRChat process {ProcessId} via WMI.", processId);
        }

        return false;
    }
}

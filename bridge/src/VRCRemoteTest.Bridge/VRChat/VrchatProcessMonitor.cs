using System.ComponentModel;
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
///
/// Only VRChat in the Bridge's own interactive session counts, and the session is
/// checked before anything else is read about a process: another session's VRChat is
/// neither a client to deploy to nor a reason to skip a cold start, and its command
/// line is never queried (multi-client plan sections 11.7 and 13.1).
/// </summary>
public sealed class VrchatProcessMonitor : IVrchatProcessMonitor
{
    private const string ProcessName = "VRChat";
    private const string WatchWorldsArgument = "--watch-worlds";

    private readonly ILogger<VrchatProcessMonitor> _logger;
    private readonly int? _currentSessionId;

    public VrchatProcessMonitor(ILogger<VrchatProcessMonitor> logger)
    {
        _logger = logger;
        _currentSessionId = TryGetCurrentSessionId();
    }

    public VrchatClientSnapshot GetClients()
    {
        var first = Enumerate(out var vanished);
        if (first.EnumerationError is not null || vanished == 0)
        {
            return first;
        }

        // A process that exited between enumeration and reading its details is simply
        // gone after a fresh enumeration; one that vanishes again is reported as
        // unverified rather than as "not running".
        var second = Enumerate(out vanished);
        return vanished == 0
            ? second
            : new VrchatClientSnapshot(second.Clients, second.UnverifiedCount + vanished, second.EnumerationError);
    }

    public VrchatStatus GetStatus()
    {
        var snapshot = GetClients();
        if (snapshot.EnumerationError is not null)
        {
            throw new InvalidOperationException($"Could not enumerate VRChat processes: {snapshot.EnumerationError}");
        }

        return BuildStatus(snapshot, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The first client: the oldest one with --watch-worlds, since it is the one a cold
    /// start launched first (multi-client plan section 11.6). Without any --watch-worlds
    /// client, the oldest client. Unknown creation times sort last; the PID breaks ties so
    /// the choice never depends on enumeration order.
    /// </summary>
    internal static VrchatClientInfo? SelectRepresentative(IReadOnlyList<VrchatClientInfo> clients) =>
        clients
            .OrderByDescending(c => c.WatchWorldsDetected)
            .ThenBy(c => c.CreationTimeUtc is null)
            .ThenBy(c => c.CreationTimeUtc)
            .ThenBy(c => c.ProcessId)
            .FirstOrDefault();

    /// <summary>
    /// Same fields and meaning as before multi-client support. A VRChat that could not
    /// be checked still shows as running (without --watch-worlds), never as stopped.
    /// </summary>
    internal static VrchatStatus BuildStatus(VrchatClientSnapshot snapshot, DateTimeOffset now)
    {
        var representative = SelectRepresentative(snapshot.Clients);
        if (representative is null)
        {
            return new VrchatStatus
            {
                IsRunning = snapshot.UnverifiedCount > 0,
                WatchWorldsDetected = false,
                UpdatedAtUtc = now,
            };
        }

        return new VrchatStatus
        {
            IsRunning = true,
            WatchWorldsDetected = representative.WatchWorldsDetected,
            ProcessId = representative.ProcessId,
            StartTimeUtc = representative.CreationTimeUtc is { } created
                ? new DateTimeOffset(created, TimeSpan.Zero)
                : null,
            UpdatedAtUtc = now,
        };
    }

    private VrchatClientSnapshot Enumerate(out int vanished)
    {
        vanished = 0;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(ex, "Failed to enumerate VRChat processes.");
            return VrchatClientSnapshot.Failed(ex.Message);
        }

        var clients = new List<VrchatClientInfo>();
        var unverified = 0;
        try
        {
            foreach (var process in processes)
            {
                switch (TryReadClient(process, out var client))
                {
                    case ReadOutcome.Client:
                        clients.Add(client!);
                        break;
                    case ReadOutcome.Vanished:
                        vanished++;
                        break;
                    case ReadOutcome.Unverified:
                        unverified++;
                        break;
                    case ReadOutcome.OtherSession:
                        break;
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return new VrchatClientSnapshot(clients, unverified);
    }

    private enum ReadOutcome
    {
        Client,
        OtherSession,
        Unverified,
        Vanished,
    }

    private ReadOutcome TryReadClient(Process process, out VrchatClientInfo? client)
    {
        client = null;

        int sessionId;
        try
        {
            sessionId = process.SessionId;
        }
        catch (InvalidOperationException)
        {
            return ReadOutcome.Vanished; // exited after enumeration
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not read the session of VRChat process {ProcessId}.", process.Id);
            return ReadOutcome.Unverified;
        }

        if (_currentSessionId is null)
        {
            return ReadOutcome.Unverified;
        }

        if (sessionId != _currentSessionId)
        {
            return ReadOutcome.OtherSession;
        }

        DateTime? creationTimeUtc;
        try
        {
            // Process.StartTime is local time; converting keeps the full tick precision.
            creationTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            return ReadOutcome.Vanished;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // Readiness then counts from when it first observed this client.
            creationTimeUtc = null;
        }

        var watchWorlds = WindowsCommandLine.ContainsArgument(TryReadCommandLine(process.Id), WatchWorldsArgument);
        client = new VrchatClientInfo(process.Id, creationTimeUtc, watchWorlds);
        return ReadOutcome.Client;
    }

    private int? TryGetCurrentSessionId()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.SessionId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _logger.LogWarning(ex, "Could not read the Bridge's own session; every VRChat process will count as unverified.");
            return null;
        }
    }

    /// <summary>
    /// WMI query failure (permissions, WMI service unavailable, transient COM error)
    /// collapses to "no command line", i.e. "--watch-worlds not detected" -- the status
    /// model is deliberately two-valued (Codex plan review Phase 4a, Round 3, confidence
    /// 0.88).
    /// </summary>
    private string? TryReadCommandLine(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId={processId}");
            using var results = searcher.Get();

            foreach (var resultObj in results)
            {
                using var managementObject = (ManagementBaseObject)resultObj;
                return managementObject["CommandLine"] as string;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to query command line for VRChat process {ProcessId} via WMI.", processId);
        }

        return null;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
/// neither a client to deploy to nor a reason to skip a cold start (multi-client plan
/// sections 11.7 and 13.1).
///
/// The command lines of one observation come from a single WMI query that only matches
/// VRChat.exe in the Bridge's session, and each row is bound to its process by the
/// creation time: Process.SessionId comes from the enumeration snapshot while
/// StartTime and WMI reopen the process by PID later, so without that check a PID
/// reused in between could lend another process's command line to the client. A row
/// that disagrees counts as the client having vanished. Anything WMI cannot answer
/// (the query fails or times out, or a command line comes back empty) makes the
/// client unverified rather than "without --watch-worlds".
/// </summary>
public sealed class VrchatProcessMonitor : IVrchatProcessMonitor
{
    private const string ProcessName = "VRChat";
    private const string WmiProcessName = "VRChat.exe";
    private const string WatchWorldsArgument = "--watch-worlds";

    /// <summary>
    /// Applies to each wait for the next result row, not to connecting or issuing the
    /// query: a WMI service that hangs there can still block an observation (Codex code
    /// review round 3, D3).
    /// </summary>
    private static readonly TimeSpan WmiEnumerationTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<VrchatProcessMonitor> _logger;
    private readonly int? _currentSessionId;
    private readonly Func<int, IReadOnlyCollection<int>, IReadOnlyList<WmiProcessRow>> _queryWmi;

    public VrchatProcessMonitor(ILogger<VrchatProcessMonitor> logger)
        : this(logger, currentSessionId: null, queryWmi: QueryWmi)
    {
    }

    /// <summary>Test-only seam: a fixed session and fake WMI rows.</summary>
    internal VrchatProcessMonitor(
        ILogger<VrchatProcessMonitor> logger,
        int? currentSessionId,
        Func<int, IReadOnlyCollection<int>, IReadOnlyList<WmiProcessRow>> queryWmi)
    {
        _logger = logger;
        _currentSessionId = currentSessionId ?? TryGetCurrentSessionId();
        _queryWmi = queryWmi;
    }

    /// <summary>One Win32_Process row; CreationDate is the raw CIM_DATETIME string.</summary>
    internal sealed record WmiProcessRow(int ProcessId, int? SessionId, string? CreationDate, string? CommandLine);

    /// <summary>A same-session VRChat process found by enumeration, before WMI is asked about it.</summary>
    internal readonly record struct Candidate(int ProcessId, DateTime? CreationTimeUtc);

    /// <summary>What one observation established about the candidates.</summary>
    internal readonly record struct Observation(List<VrchatClientInfo> Clients, int Unverified, int Vanished);

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

        var candidates = new List<Candidate>();
        var unverified = 0;
        try
        {
            foreach (var process in processes)
            {
                switch (TryReadCandidate(process, out var candidate))
                {
                    case ReadOutcome.Candidate:
                        candidates.Add(candidate);
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

        var observation = ObserveCandidates(candidates);
        vanished += observation.Vanished;
        return new VrchatClientSnapshot(observation.Clients, unverified + observation.Unverified);
    }

    /// <summary>Asks WMI once about every candidate; no query at all when there is none.</summary>
    internal Observation ObserveCandidates(IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return new Observation([], 0, 0);
        }

        IReadOnlyList<WmiProcessRow>? rows;
        try
        {
            rows = _queryWmi(_currentSessionId!.Value, candidates.Select(c => c.ProcessId).ToArray());
        }
        catch (Exception ex)
        {
            // Includes ManagementException with ManagementStatus.Timedout. Rows read
            // before the failure are discarded with the rest.
            _logger.LogWarning(ex, "Failed to query VRChat command lines via WMI.");
            rows = null;
        }

        return Classify(candidates, rows, _currentSessionId!.Value);
    }

    /// <summary>
    /// Pure, for tests. <paramref name="rows"/> is null when the query failed: every
    /// candidate is then unverified. A missing row, another session or a creation time
    /// that disagrees with StartTime means the PID no longer names the enumerated
    /// process (vanished: GetClients enumerates once more). When StartTime could not be
    /// read, the row is accepted on its name and session alone and the creation time
    /// stays unknown, as the plan requires (section 13.2): WMI's microsecond time is not
    /// substituted, because a client must keep one identity across polls.
    /// </summary>
    internal static Observation Classify(
        IReadOnlyList<Candidate> candidates, IReadOnlyList<WmiProcessRow>? rows, int currentSessionId)
    {
        if (rows is null)
        {
            return new Observation([], candidates.Count, 0);
        }

        var clients = new List<VrchatClientInfo>();
        var unverified = 0;
        var vanished = 0;
        foreach (var candidate in candidates)
        {
            var row = rows.FirstOrDefault(r => r.ProcessId == candidate.ProcessId);
            if (row is null || row.SessionId != currentSessionId)
            {
                vanished++;
                continue;
            }

            if (candidate.CreationTimeUtc is { } nativeUtc)
            {
                if (ParseCimDateTimeUtc(row.CreationDate) is not { } wmiUtc)
                {
                    unverified++;
                    continue;
                }

                if (!IsSameCreationTime(nativeUtc, wmiUtc))
                {
                    vanished++;
                    continue;
                }
            }

            if (row.CommandLine is null)
            {
                unverified++;
                continue;
            }

            clients.Add(new VrchatClientInfo(
                candidate.ProcessId,
                candidate.CreationTimeUtc,
                WindowsCommandLine.ContainsArgument(row.CommandLine, WatchWorldsArgument)));
        }

        return new Observation(clients, unverified, vanished);
    }

    /// <summary>
    /// StartTime has 100 ns resolution and CIM_DATETIME microseconds, so the same
    /// process differs by less than one microsecond (10 ticks). Widening this would
    /// only weaken the PID check.
    /// </summary>
    internal static bool IsSameCreationTime(DateTime nativeUtc, DateTime wmiUtc) =>
        Math.Abs(nativeUtc.Ticks - wmiUtc.Ticks) < TimeSpan.TicksPerMicrosecond;

    /// <summary>
    /// "yyyymmddHHMMSS.mmmmmmsUUU" with an explicit offset in minutes, converted with
    /// that offset rather than the machine's time zone (no DST ambiguity). Null for
    /// anything else, including wildcards.
    /// </summary>
    internal static DateTime? ParseCimDateTimeUtc(string? value)
    {
        if (value is null || value.Length != 25 || value.Contains('*')
            || value[14] != '.' || value[21] is not ('+' or '-'))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                value[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock)
            || !int.TryParse(value.AsSpan(22, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var offsetMinutes))
        {
            return null;
        }

        var offset = TimeSpan.FromMinutes(value[21] == '-' ? -offsetMinutes : offsetMinutes);
        try
        {
            return new DateTimeOffset(clock, offset).UtcDateTime;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private enum ReadOutcome
    {
        Candidate,
        OtherSession,
        Unverified,
        Vanished,
    }

    private ReadOutcome TryReadCandidate(Process process, out Candidate candidate)
    {
        candidate = default;

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

        candidate = new Candidate(process.Id, creationTimeUtc);
        return ReadOutcome.Candidate;
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
    /// The filter only contains integers the Bridge read itself. ReturnImmediately with
    /// a per-row timeout keeps a slow provider from blocking forever once the query is
    /// issued; a timeout surfaces as a ManagementException (ManagementStatus.Timedout)
    /// and fails the whole observation. Not on Windows, there are no rows.
    /// </summary>
    private static IReadOnlyList<WmiProcessRow> QueryWmi(int sessionId, IReadOnlyCollection<int> processIds)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var idFilter = string.Join(" OR ", processIds.Select(id => $"ProcessId={id}"));
        var query =
            "SELECT ProcessId, SessionId, CreationDate, CommandLine FROM Win32_Process " +
            $"WHERE Name='{WmiProcessName}' AND SessionId={sessionId} AND ({idFilter})";

        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"root\cimv2"),
            new ObjectQuery(query),
            new System.Management.EnumerationOptions
            {
                ReturnImmediately = true,
                Rewindable = false,
                Timeout = WmiEnumerationTimeout,
            });
        using var results = searcher.Get();

        var rows = new List<WmiProcessRow>();
        foreach (var resultObj in results)
        {
            using var row = (ManagementBaseObject)resultObj;
            rows.Add(new WmiProcessRow(
                Convert.ToInt32(row["ProcessId"], CultureInfo.InvariantCulture),
                row["SessionId"] is { } session ? Convert.ToInt32(session, CultureInfo.InvariantCulture) : null,
                row["CreationDate"] as string,
                row["CommandLine"] as string));
        }

        return rows;
    }
}

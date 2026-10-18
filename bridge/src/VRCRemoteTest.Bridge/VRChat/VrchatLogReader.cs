using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Snapshot reader: every call re-resolves the log files and reads a bounded tail
/// window of each, rather than tracking a persistent (file, offset, incomplete-line-buffer)
/// state machine. This design replaced an original stateful-tailer proposal that
/// Codex plan review found underspecified (FileShare flags, encoding,
/// rotation/truncation handling) and over-built for a "glance at recent logs"
/// developer convenience feature (Codex plan review Phase 5, Rounds 1-3, confidence
/// converged to 0.91). The incremental reading that collects marker lines from the start
/// of a session is VrchatLogScanner's, fed with the files resolved here. Real VRChat
/// output_log files were inspected on real Windows hardware before writing this class:
/// filename pattern output_log_YYYY-MM-DD_HH-MM-SS.txt, no BOM, UTF-8-compatible encoding,
/// lines shaped like "YYYY.MM.DD HH:MM:SS Level      -  Message" and ended by CRLF.
///
/// Each VRChat client has a slot (<see cref="VrchatClientRoster"/>, client-observation plan
/// C2), and each slot's log is resolved by the rules that used to apply to the first client
/// alone. Every poll asks Windows Restart Manager which processes have the recent files
/// open (<see cref="VrchatLogOwners"/>), once per file however many slots ask about it: the
/// file that only this client has open, among VRChat processes, is its log. When that
/// settles nothing for a slot (Restart Manager is unavailable or fails for one of the
/// slot's files, the client has no file open, an owner cannot be identified), the file
/// shown on the previous poll stays, or the file is matched by the times in the names
/// (<see cref="VrchatLogMatcher"/>) as before. Whichever rule picks it, a file that another
/// VRChat has open, or that was once seen open by several, is never shown; every slot's
/// "mixed" findings are collected before any slot's file is chosen, so the outcome does not
/// depend on the order of the slots. A slot keeps its file (pinned to its client's PID and
/// creation time) until the client exits, the file disappears, or Restart Manager finds
/// another VRChat on it or the client on another file; when no file can be matched, a
/// diagnostic is published instead.
///
/// logs/vrchat-latest.log (Unity 0.5.x) keeps showing what it showed before slots: the
/// first client's log (the representative client of
/// <see cref="VrchatProcessMonitor.SelectRepresentative"/>, or in Relaunch mode the running
/// client launched first according to the Bridge's record), which is slot 0's unless slot 0
/// exited, and it stays on that client while it runs. Only with no VRChat running is the
/// newest file shown there, so the last session's log stays readable (multi-client plan
/// section 13.2).
///
/// The copies are split at CRLF and continuation lines are indented (client-observation
/// plan C6 and F2, <see cref="VrchatLogLine.ToDisplayLines"/>), and masked
/// (<see cref="LogRedactor"/>) before being cut to their limits.
/// </summary>
public sealed class VrchatLogReader : IVrchatLogReader
{
    private const string OutputLogPattern = VrchatLogDirectory.OutputLogPattern;
    private const int MaxDirectoryScan = VrchatLogDirectory.MaxDirectoryScan;
    private const int SlotCount = VrchatClientRoster.SlotCount;

    private const int SnapshotBytes = 256 * 1024;
    private const int PublishedLineLimit = 200;
    private const int PerLineCharLimit = 512;

    /// <summary>
    /// Kept safely under Unity's PollVrchatLog 512 KiB guard so a full-to-cap
    /// published file can never fail that guard (Codex plan review Phase 5,
    /// Round 2, confidence 0.90).
    /// </summary>
    private const int BridgePublishedLogByteLimit = 384 * 1024;

    private const string TruncationMarker = "... [truncated]";

    private readonly BridgeOptions _options;
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly ILogger<VrchatLogReader> _logger;
    private readonly Func<string, DateTime> _creationTimeUtc;
    private readonly IManagedClientStore? _managedClients;
    private readonly Func<string, IReadOnlyList<VrchatLogOwners.Owner>>? _queryOwners;
    private readonly VrchatClientRoster _roster;

    /// <summary>Only VrchatLogService's sequential loop calls this reader, so no synchronization is needed.</summary>
    private readonly PinnedLog?[] _pinned = new PinnedLog?[SlotCount];

    /// <summary>
    /// Files Restart Manager showed open by several VRChat processes, refused for every slot
    /// even after the other processes exit: their lines stay in the file. Dropped once the
    /// file is gone.
    /// </summary>
    private readonly HashSet<string> _mixedLogs = new(StringComparer.OrdinalIgnoreCase);

    private readonly string?[] _lastReportedProblem = new string?[SlotCount];
    private string? _lastLatestProblem;
    private string? _ownerQueryProblem;

    /// <summary>The client vrchat-latest.log follows while it runs and its slot shows its log.</summary>
    private ClientKey? _latest;

    public VrchatLogReader(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        ILogger<VrchatLogReader> logger,
        IManagedClientStore managedClients,
        VrchatClientRoster roster)
        : this(options, processMonitor, logger, File.GetCreationTimeUtc, DefaultOwnerQuery(), managedClients, roster)
    {
    }

    /// <summary>
    /// Test-only seam: not every filesystem the tests run on can set a file's creation
    /// time, and matching depends on it. Without Restart Manager, as on a machine that is
    /// not Windows: only the times in the names match.
    /// </summary>
    internal VrchatLogReader(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        ILogger<VrchatLogReader> logger,
        Func<string, DateTime> creationTimeUtc,
        IManagedClientStore? managedClients = null)
        : this(options, processMonitor, logger, creationTimeUtc, queryOwners: null, managedClients)
    {
    }

    /// <summary>
    /// Test-only seam for the owners too: <paramref name="queryOwners"/> stands in for
    /// <see cref="RestartManagerFileOwners.Query"/> (null: not available) and may throw
    /// like it. <paramref name="managedClients"/> is the Relaunch record (null: none), and
    /// <paramref name="roster"/> defaults to one kept in memory only.
    /// </summary>
    internal VrchatLogReader(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        ILogger<VrchatLogReader> logger,
        Func<string, DateTime> creationTimeUtc,
        Func<string, IReadOnlyList<VrchatLogOwners.Owner>>? queryOwners,
        IManagedClientStore? managedClients = null,
        VrchatClientRoster? roster = null)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _logger = logger;
        _creationTimeUtc = creationTimeUtc;
        _managedClients = managedClients;
        _queryOwners = queryOwners;
        _roster = roster ?? new VrchatClientRoster(filePath: null);
    }

    private string? LogDirectory => VrchatLogDirectory.Resolve(_options);

    private static Func<string, IReadOnlyList<VrchatLogOwners.Owner>>? DefaultOwnerQuery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return RestartManagerFileOwners.Query;
    }

    /// <summary>vrchat-latest.log's content alone: what this reader published before slots.</summary>
    internal LogSnapshotResult ReadSnapshot() => Read().Latest;

    public VrchatLogReadResult Read()
    {
        var snapshot = _processMonitor.GetClients();
        var records = _options.IsRelaunchMode && _managedClients is not null ? _managedClients.Known : [];
        var roster = _roster.Update(snapshot, records);

        var logDirectory = LogDirectory;
        if (logDirectory is null || !Directory.Exists(logDirectory))
        {
            // VRChat has never run on this machine, or the derived path is
            // wrong -- never throw, just report nothing available (Codex plan
            // review Phase 5, Round 3, Scenario A).
            var unavailable = Enumerable.Range(0, SlotCount)
                .Select(slot => new SlotLogResult(slot, SlotLogStatus.Unavailable, null, LogSnapshotResult.Empty()))
                .ToList();
            return new VrchatLogReadResult(snapshot, roster, unavailable, LogSnapshotResult.Empty());
        }

        var slots = ResolveSlots(logDirectory, snapshot, roster);
        var latest = ResolveLatest(logDirectory, snapshot, roster, slots);
        return new VrchatLogReadResult(snapshot, roster, slots, latest);
    }

    private SlotLogResult[] ResolveSlots(string logDirectory, VrchatClientSnapshot snapshot, RosterView roster)
    {
        var results = new SlotLogResult[SlotCount];
        var pending = new List<PendingSlot>();

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var rosterSlot = roster.Slots[slot];
            var entry = rosterSlot.Entry;

            // The pin stays for as long as its client is seen, whichever client would be
            // chosen now (e.g. while WMI cannot read its command line for one poll). Only a
            // confirmed exit, a reused PID, a deleted file or Restart Manager (see Resolve)
            // releases it; an observation that cannot tell keeps it.
            if (_pinned[slot] is { } pin
                && (entry is null || pin.Client != KeyOf(entry) || rosterSlot.Liveness == RosterLiveness.Exited
                    || !File.Exists(pin.Path)))
            {
                _pinned[slot] = null;
            }

            var pinnedPath = _pinned[slot]?.Path;
            if (snapshot.EnumerationError is not null)
            {
                results[slot] = pinnedPath is not null
                    ? Problem(slot, SlotLogStatus.CannotCheck, "VRChat processes could not be checked; the log is shown again once they can be.")
                    : Problem(slot, SlotLogStatus.CannotCheck, "The VRChat process list could not be read.");
                continue;
            }

            switch (rosterSlot.Liveness)
            {
                case RosterLiveness.Empty:
                    results[slot] = NotRunning(slot, snapshot, entry);
                    break;
                case RosterLiveness.Exited:
                    results[slot] = NotRunning(slot, snapshot, entry);
                    break;
                case RosterLiveness.Unknown:
                    results[slot] = Problem(
                        slot,
                        SlotLogStatus.CannotCheck,
                        pinnedPath is not null
                            ? $"VRChat client {entry!.ProcessId} could not be checked; its log is shown again once it can be."
                            : $"VRChat client {entry!.ProcessId} in slot {slot} could not be checked.");
                    break;
                default:
                    if (pinnedPath is not null && _queryOwners is null)
                    {
                        // Without Restart Manager nothing here can release the pin, as before it was asked.
                        results[slot] = Show(slot, pinnedPath);
                    }
                    else
                    {
                        pending.Add(new PendingSlot(slot, rosterSlot.Client!, pinnedPath));
                    }

                    break;
            }
        }

        if (pending.Count == 0)
        {
            return results;
        }

        var (candidates, scanCapped) = ListCandidates(logDirectory);
        if (scanCapped)
        {
            // Nothing to check a pinned file against; it stays.
            foreach (var request in pending)
            {
                results[request.Slot] = request.PinnedPath is null ? ScanCapped(request.Slot) : Show(request.Slot, request.PinnedPath);
            }

            return results;
        }

        _mixedLogs.RemoveWhere(path => !File.Exists(path));
        var owners = QueryOwners(pending, candidates);
        var decisions = pending.Select(request => Decide(request, snapshot, candidates, owners)).ToList();
        foreach (var decision in decisions)
        {
            if (decision is not null)
            {
                RecordMixed(decision.Mixed);
            }
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var request = pending[i];
            results[request.Slot] = Resolve(request, snapshot, candidates, decisions[i]);
        }

        return results;
    }

    /// <summary>
    /// The first client's log, as before slots: kept on one client while it may still run
    /// and its slot shows its log (or cannot tell for now), else the first client chosen now.
    /// </summary>
    private LogSnapshotResult ResolveLatest(
        string logDirectory, VrchatClientSnapshot snapshot, RosterView roster, IReadOnlyList<SlotLogResult> slots)
    {
        if (_latest is { } followed)
        {
            var slot = roster.Slots.FirstOrDefault(s =>
                s.Entry is { } e && KeyOf(e) == followed && s.Liveness is RosterLiveness.Running or RosterLiveness.Unknown);
            if (slot is not null && slots[slot.Slot].Status is SlotLogStatus.Shown or SlotLogStatus.CannotCheck)
            {
                return slots[slot.Slot].Content;
            }

            _latest = null;
        }

        if (snapshot.EnumerationError is not null)
        {
            return LatestProblem("The VRChat process list could not be read.");
        }

        if (snapshot.Clients.Count == 0 && snapshot.UnverifiedCount == 0)
        {
            _lastLatestProblem = null;
            return ReadNewest(logDirectory);
        }

        var client = SelectFirstClient(snapshot.Clients);
        if (client is null)
        {
            return LatestProblem("VRChat is running, but its processes could not be checked.");
        }

        if (roster.SlotOf(client) is not { } clientSlot)
        {
            // Its creation time is unknown, or it is a fifth client.
            return LatestProblem(CouldNotMatch(client, slot: null));
        }

        var result = slots[clientSlot.Slot];
        if (result.Status == SlotLogStatus.Shown)
        {
            _latest = KeyOf(client);
        }

        _lastLatestProblem = null;
        return result.Content;
    }

    /// <summary>
    /// The log of the slot's client, which is also its pinned client when
    /// <see cref="PendingSlot.PinnedPath"/> is given: the file Restart Manager shows only it
    /// has open, else the pinned file, else the name match. A file another VRChat has open,
    /// or that was seen mixed, is refused whichever rule picks it.
    /// </summary>
    private SlotLogResult Resolve(
        PendingSlot request,
        VrchatClientSnapshot snapshot,
        IReadOnlyList<VrchatLogMatcher.Candidate> candidates,
        VrchatLogOwners.Decision? ownership)
    {
        var (slot, client, pinnedPath) = request;
        switch (ownership)
        {
            case { Verdict: VrchatLogOwners.Verdict.Owned, Path: { } owned } when !_mixedLogs.Contains(owned):
                return Pin(slot, client, owned, "the only VRChat process that has it open");

            case { Verdict: VrchatLogOwners.Verdict.Owned or VrchatLogOwners.Verdict.Shared, Path: { } mixed }:
                _pinned[slot] = null;
                return Problem(
                    slot,
                    SlotLogStatus.Unmatched,
                    $"{Path.GetFileName(mixed)}, the {OutputLogPattern} file of {Describe(client, slot)}, " +
                    "is or was also open by other VRChat clients, " +
                    "which happens when their logging starts within the same second; " +
                    "not showing the mixed log as this client's.");

            case { Verdict: VrchatLogOwners.Verdict.Several }:
                _pinned[slot] = null;
                return Problem(slot, SlotLogStatus.Unmatched, CouldNotMatch(client, slot));
        }

        // Restart Manager settled nothing, so the file names decide as before, except for
        // files it showed belong to another VRChat.
        bool Refused(string path) => _mixedLogs.Contains(path) || ownership?.Refused.Contains(path) == true;

        if (pinnedPath is not null)
        {
            if (!Refused(pinnedPath))
            {
                return Show(slot, pinnedPath);
            }

            _pinned[slot] = null;
            _logger.LogInformation(
                "No longer showing {Path} as the log of VRChat client {ProcessId}: another VRChat process has or had it open.",
                pinnedPath, client.ProcessId);
        }

        // A VRChat that could not be checked might own any file in the range.
        var result = snapshot.UnverifiedCount == 0
            ? VrchatLogMatcher.Evaluate(client, snapshot.Clients, candidates, TimeZoneInfo.Local)
            : new VrchatLogMatcher.MatchResult(null, VrchatLogMatcher.MatchFailure.Ambiguous);
        if (result.Path is { } match)
        {
            if (!Refused(match))
            {
                return Pin(slot, client, match, "matched by the time in its name");
            }

            result = new VrchatLogMatcher.MatchResult(null, VrchatLogMatcher.MatchFailure.Ambiguous);
        }

        if (HasNoLogYet(client, result, ownership))
        {
            return Problem(
                slot,
                SlotLogStatus.NotYet,
                $"The {Describe(client, slot)} has not created its {OutputLogPattern} file yet. " +
                "This usually takes about a second, and a minute or more under heavy CPU load; " +
                "its log is shown once it appears.");
        }

        return Problem(slot, SlotLogStatus.Unmatched, CouldNotMatch(client, slot));
    }

    /// <summary>
    /// VRChat created its output_log 0.70-0.84 s after the process start on real hardware
    /// without load, and with the CPU at about 100% 11-22 s after it in one session and 60 s
    /// to about 2 min after it in another (docs/validation/multi-client-spike.md, PR #2
    /// verification item 12 and PR #3 verification B), so the first poll after a cold start
    /// usually finds no file yet. Saying so replaces the previous session's log at once:
    /// publishing nothing would leave that closed client's log on the share, shown as if it
    /// were this client's (a9292a1 re-verification).
    ///
    /// With Restart Manager answering that the client has no file open, "not yet" holds for
    /// as long as no file was named after the client's start or other VRChat processes are
    /// seen holding theirs (so VRChat does keep its log open): the file can still be
    /// matched by its owner when it appears. Without that, past MaxRangeLength after the
    /// start a new file can no longer be named in the client's range, so "not yet" would
    /// never end and the generic message applies.
    /// </summary>
    private static bool HasNoLogYet(
        VrchatClientInfo client, VrchatLogMatcher.MatchResult result, VrchatLogOwners.Decision? ownership)
    {
        var noFileNamed = result.Failure == VrchatLogMatcher.MatchFailure.NoFileYet;
        if (ownership is { Verdict: VrchatLogOwners.Verdict.NotOwned } && (noFileNamed || ownership.AnyVrchatOwner))
        {
            return true;
        }

        return noFileNamed && DateTime.UtcNow - client.CreationTimeUtc < VrchatLogMatcher.MaxRangeLength;
    }

    private static string Started(VrchatClientInfo client) =>
        client.CreationTimeUtc is { } created ? $"started {created:yyyy-MM-dd HH:mm:ss}Z" : "start time unknown";

    private static string Describe(VrchatClientInfo client, int? slot) =>
        slot is { } s
            ? $"VRChat client in slot {s} (PID {client.ProcessId}, {Started(client)})"
            : $"VRChat client without a slot (PID {client.ProcessId}, {Started(client)})";

    private static string CouldNotMatch(VrchatClientInfo client, int? slot) =>
        $"Could not match an {OutputLogPattern} file to the {Describe(client, slot)}; " +
        "not showing another client's log instead. This happens when clients start within the same second " +
        "or write to one shared file, when VRChat created the log a minute or more after the client started, " +
        "or when the clock or time zone changed after the log was created.";

    private SlotLogResult NotRunning(int slot, VrchatClientSnapshot snapshot, RosterEntry? entry)
    {
        var message = entry is null
            ? $"No VRChat client is running in slot {slot}."
            : $"The VRChat client in slot {slot} (PID {entry.ProcessId}) is not running.";
        if (snapshot.UnverifiedCount > 0)
        {
            message += $" {snapshot.UnverifiedCount} VRChat process(es) could not be checked.";
        }

        _lastReportedProblem[slot] = null;
        return new SlotLogResult(slot, SlotLogStatus.NotRunning, null, LogSnapshotResult.Diagnostic(message));
    }

    /// <summary>
    /// The owners of every file a pending slot asks about: those created since shortly before
    /// its client started (<see cref="VrchatLogOwners.QueryLead"/>) and its pinned file. Each
    /// file is asked once per poll; a file whose query failed maps to null. A failure is
    /// logged once, and so is the recovery. Without Restart Manager (not Windows) nothing is
    /// asked.
    /// </summary>
    private Dictionary<string, IReadOnlyList<VrchatLogOwners.Owner>?> QueryOwners(
        IReadOnlyList<PendingSlot> pending, IReadOnlyList<VrchatLogMatcher.Candidate> candidates)
    {
        var owners = new Dictionary<string, IReadOnlyList<VrchatLogOwners.Owner>?>(StringComparer.OrdinalIgnoreCase);
        if (_queryOwners is null)
        {
            return owners;
        }

        Exception? failure = null;
        var answered = false;
        foreach (var request in pending)
        {
            foreach (var path in PathsToAsk(request, candidates))
            {
                if (owners.ContainsKey(path))
                {
                    continue;
                }

                try
                {
                    owners[path] = _queryOwners(path);
                    answered = true;
                }
                catch (Exception ex)
                {
                    // Display only, so any failure (a Restart Manager error, a missing
                    // rstrtmgr.dll) just makes the slots that asked fall back to the names.
                    owners[path] = null;
                    failure ??= ex;
                }
            }
        }

        if (failure is not null)
        {
            if (failure.Message != _ownerQueryProblem)
            {
                _logger.LogWarning(
                    failure,
                    "Could not ask Windows Restart Manager which processes have the VRChat logs open; " +
                    "matching them by the times in their names instead.");
                _ownerQueryProblem = failure.Message;
            }
        }
        else if (_ownerQueryProblem is not null && answered)
        {
            _logger.LogInformation("Windows Restart Manager answers again; matching VRChat logs by their owners.");
            _ownerQueryProblem = null;
        }

        return owners;
    }

    private static IEnumerable<string> PathsToAsk(PendingSlot request, IReadOnlyList<VrchatLogMatcher.Candidate> candidates)
    {
        if (request.Client.CreationTimeUtc is not { } created)
        {
            yield break;
        }

        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (candidate.CreationTimeUtc >= created - VrchatLogOwners.QueryLead && asked.Add(candidate.Path))
            {
                yield return candidate.Path;
            }
        }

        if (request.PinnedPath is { } pinned && asked.Add(pinned))
        {
            yield return pinned;
        }
    }

    /// <summary>
    /// The slot's decision from the answers about the files it asks about. Null when
    /// Restart Manager is not available, the client's creation time is unknown, or one of
    /// the slot's files could not be asked about; with no such file there is nothing to
    /// ask, and the decision is that the client has no file open.
    /// </summary>
    private VrchatLogOwners.Decision? Decide(
        PendingSlot request,
        VrchatClientSnapshot snapshot,
        IReadOnlyList<VrchatLogMatcher.Candidate> candidates,
        IReadOnlyDictionary<string, IReadOnlyList<VrchatLogOwners.Owner>?> owners)
    {
        if (_queryOwners is null || request.Client.CreationTimeUtc is null)
        {
            return null;
        }

        var files = new List<VrchatLogOwners.FileOwners>();
        foreach (var path in PathsToAsk(request, candidates))
        {
            if (!owners.TryGetValue(path, out var fileOwners) || fileOwners is null)
            {
                return null;
            }

            files.Add(new VrchatLogOwners.FileOwners(path, fileOwners));
        }

        return VrchatLogOwners.Decide(request.Client, snapshot.Clients, files);
    }

    private void RecordMixed(IEnumerable<string> mixed)
    {
        foreach (var path in mixed)
        {
            if (_mixedLogs.Add(path))
            {
                _logger.LogInformation(
                    "{Path} is open by several VRChat processes; it is not shown as any one client's log.", path);
            }
        }
    }

    /// <summary>Logged when the pin changes, not on every poll.</summary>
    private SlotLogResult Pin(int slot, VrchatClientInfo client, string path, string reason)
    {
        var pin = new PinnedLog(KeyOf(client), path);
        if (pin != _pinned[slot])
        {
            _pinned[slot] = pin;
            _logger.LogInformation(
                "Showing {Path} as the log of VRChat client {ProcessId} in slot {Slot} ({Reason}).",
                path, client.ProcessId, slot, reason);
        }

        return Show(slot, path);
    }

    private SlotLogResult Show(int slot, string path)
    {
        _lastReportedProblem[slot] = null;
        return new SlotLogResult(slot, SlotLogStatus.Shown, path, ReadLog(path));
    }

    /// <summary>
    /// Relaunch clients run without --watch-worlds, so the Reload rule (the oldest
    /// --watch-worlds client) does not apply: the first client is the running one that
    /// comes first in the record's launch order (multi-client plan section 13.3). The
    /// enumeration only lists the Bridge's own session, so PID and creation time identify
    /// the recorded client. Without a running recorded client, the Reload rule applies.
    /// </summary>
    private VrchatClientInfo? SelectFirstClient(IReadOnlyList<VrchatClientInfo> clients)
    {
        if (_options.IsRelaunchMode && _managedClients is not null)
        {
            var first = _managedClients.Known
                .OrderBy(r => r.LaunchOrder)
                .Select(r => clients.FirstOrDefault(c =>
                    c.ProcessId == r.ProcessId && c.CreationTimeUtc?.Ticks == r.CreationTimeUtc.Ticks))
                .FirstOrDefault(c => c is not null);
            if (first is not null)
            {
                return first;
            }
        }

        return VrchatProcessMonitor.SelectRepresentative(clients);
    }

    private LogSnapshotResult ReadNewest(string logDirectory)
    {
        var (newestFile, scanCapped) = FindNewestOutputLog(logDirectory);
        if (scanCapped)
        {
            return ScanCappedDiagnostic();
        }

        return newestFile is null ? LogSnapshotResult.Empty() : ReadLog(newestFile);
    }

    private LogSnapshotResult ReadLog(string path)
    {
        try
        {
            return LogSnapshotResult.Success(BuildBoundedOutput(ReadTailLines(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read VRChat log file {Path}.", path);
            return LogSnapshotResult.Empty();
        }
    }

    private SlotLogResult ScanCapped(int slot) =>
        new(slot, SlotLogStatus.Unmatched, null, ScanCappedDiagnostic());

    private static LogSnapshotResult ScanCappedDiagnostic() =>
        LogSnapshotResult.Diagnostic(
            $"Too many {OutputLogPattern} files in the VRChat log directory; " +
            $"no log was selected because the scan cap ({MaxDirectoryScan}) was exceeded.");

    /// <summary>
    /// Logged once per distinct problem and slot, not on every 5-second poll, and only as
    /// information: right after a client starts, its file usually does not exist yet.
    /// </summary>
    private SlotLogResult Problem(int slot, SlotLogStatus status, string message)
    {
        if (message != _lastReportedProblem[slot])
        {
            _logger.LogInformation("VRChat log of slot {Slot} not published: {Message}", slot, message);
            _lastReportedProblem[slot] = message;
        }

        return new SlotLogResult(slot, status, null, LogSnapshotResult.Diagnostic(message));
    }

    private LogSnapshotResult LatestProblem(string message)
    {
        if (message != _lastLatestProblem)
        {
            _logger.LogInformation("VRChat log not published: {Message}", message);
            _lastLatestProblem = message;
        }

        return LogSnapshotResult.Diagnostic(message);
    }

    /// <summary>Same bounded scan as <see cref="FindNewestOutputLog"/>; unreadable files are skipped.</summary>
    private (List<VrchatLogMatcher.Candidate> Candidates, bool ScanCapped) ListCandidates(string logDirectory) =>
        VrchatLogDirectory.ListCandidates(logDirectory, _creationTimeUtc);

    private static ClientKey KeyOf(VrchatClientInfo client) => new(client.ProcessId, client.CreationTimeUtc);

    private static ClientKey KeyOf(RosterEntry entry) => new(entry.ProcessId, entry.CreationTimeUtc);

    private readonly record struct ClientKey(int ProcessId, DateTime? CreationTimeUtc);

    private sealed record PinnedLog(ClientKey Client, string Path);

    private sealed record PendingSlot(int Slot, VrchatClientInfo Client, string? PinnedPath);

    /// <summary>
    /// Bounded single-pass scan: stops as soon as a (MaxDirectoryScan + 1)th
    /// candidate is observed, reporting "capped" rather than silently picking a
    /// possibly-wrong newest file (directory enumeration order is not
    /// newest-first) -- Codex plan review Phase 5, Round 2, confidence 0.87.
    /// </summary>
    private static (string? NewestFile, bool ScanCapped) FindNewestOutputLog(string logDirectory)
    {
        string? newestPath = null;
        var newestWriteTimeUtc = DateTime.MinValue;
        var scanned = 0;

        foreach (var path in Directory.EnumerateFiles(logDirectory, OutputLogPattern))
        {
            if (scanned == MaxDirectoryScan)
            {
                return (null, true);
            }

            scanned++;

            DateTime writeTimeUtc;
            try
            {
                writeTimeUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (newestPath is null || writeTimeUtc > newestWriteTimeUtc)
            {
                newestPath = path;
                newestWriteTimeUtc = writeTimeUtc;
            }
        }

        return (newestPath, false);
    }

    /// <summary>
    /// FileShare.ReadWrite | FileShare.Delete: VRChat itself has this file open
    /// for writing, and must remain free to keep writing, rotate, or delete it
    /// (Codex plan review Phase 5, Round 2, confidence 0.91).
    ///
    /// The window is split at CRLF (<see cref="VrchatLogLine"/>). When it does not start at
    /// byte 0, everything up to its first CRLF may be part of a line (the read seeked into
    /// the middle of the file) and is discarded rather than published as a fragment. The
    /// last line is kept even without its CRLF yet, as before.
    /// </summary>
    private static List<string> ReadTailLines(string path)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var length = fs.Length;
        var start = Math.Max(0, length - SnapshotBytes);
        fs.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[length - start];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = fs.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        ReadOnlySpan<byte> window = buffer.AsSpan(0, totalRead);
        if (start > 0)
        {
            var first = VrchatLogLine.IndexAfterFirstCrlf(window);
            window = first < 0 ? [] : window[first..];
        }

        var physical = VrchatLogLine.SplitPhysicalLines(window);
        if (physical.Count > 0 && physical[^1].Length == 0)
        {
            physical.RemoveAt(physical.Count - 1);
        }

        var lines = physical.SelectMany(VrchatLogLine.ToDisplayLines).ToList();

        // A trailing lone LF leaves an empty last part; it is not a line.
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Masks each line, then cuts it and the whole copy to their limits (client-observation plan C5).</summary>
    private static string BuildBoundedOutput(List<string> lines)
    {
        var selected = lines.TakeLast(PublishedLineLimit).Select(l => TruncateLine(LogRedactor.Redact(l))).ToArray();

        var outputLines = new List<string>();
        var totalBytes = 0;

        for (var i = selected.Length - 1; i >= 0; i--)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(selected[i]) + 1; // +1 for the newline separator
            if (totalBytes + lineBytes > BridgePublishedLogByteLimit)
            {
                break;
            }

            outputLines.Add(selected[i]);
            totalBytes += lineBytes;
        }

        outputLines.Reverse();
        return string.Join("\n", outputLines);
    }

    private static string TruncateLine(string line)
    {
        if (line.Length <= PerLineCharLimit)
        {
            return line;
        }

        return string.Concat(line.AsSpan(0, PerLineCharLimit - TruncationMarker.Length), TruncationMarker);
    }
}

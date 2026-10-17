using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Snapshot reader: every call re-resolves the log file and reads a bounded tail
/// window, rather than tracking a persistent (file, offset, incomplete-line-buffer)
/// state machine. This design replaced an original stateful-tailer proposal that
/// Codex plan review found underspecified (FileShare flags, encoding,
/// rotation/truncation handling) and over-built for a "glance at recent logs"
/// developer convenience feature (Codex plan review Phase 5, Rounds 1-3, confidence
/// converged to 0.91). Real VRChat output_log files were inspected on real Windows
/// hardware before writing this class: filename pattern
/// output_log_YYYY-MM-DD_HH-MM-SS.txt, no BOM, UTF-8-compatible encoding, lines
/// shaped like "YYYY.MM.DD HH:MM:SS Level    - Message".
///
/// With VRChat running, the log shown is the first client's (the representative
/// client of <see cref="VrchatProcessMonitor.SelectRepresentative"/>), not the most
/// recently written file, which with several clients would switch between them on
/// every poll. Every poll asks Windows Restart Manager which processes have the recent
/// files open (<see cref="VrchatLogOwners"/>): the file that only this client has open,
/// among VRChat processes, is its log. When that settles nothing (Restart Manager is
/// unavailable or fails, the client has no file open, an owner cannot be identified),
/// the file shown on the previous poll stays, or the file is matched by the times in the
/// names (<see cref="VrchatLogMatcher"/>) as before. Whichever rule picks it, a file that
/// another VRChat has open, or that was once seen open by several, is never shown. The
/// file is kept for that client (its PID and creation time) until the client exits, the
/// file disappears, or Restart Manager finds another VRChat on it or the client on
/// another file; when no file can be matched, a diagnostic is published instead.
/// Only with no VRChat running is the newest file shown, so the last session's log
/// stays readable (multi-client plan section 13.2).
/// </summary>
public sealed class VrchatLogReader : IVrchatLogReader
{
    private const string OutputLogPattern = VrchatLogDirectory.OutputLogPattern;
    private const int MaxDirectoryScan = VrchatLogDirectory.MaxDirectoryScan;

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
    private readonly Func<string, IReadOnlyList<VrchatLogOwners.Owner>>? _queryOwners;

    /// <summary>Only VrchatLogService's sequential loop calls this reader, so no synchronization is needed.</summary>
    private PinnedLog? _pinned;

    /// <summary>
    /// Files Restart Manager showed open by several VRChat processes, refused even after
    /// the other processes exit: their lines stay in the file. Dropped once the file is gone.
    /// </summary>
    private readonly HashSet<string> _mixedLogs = new(StringComparer.OrdinalIgnoreCase);

    private string? _lastReportedProblem;
    private string? _ownerQueryProblem;

    public VrchatLogReader(
        IOptions<BridgeOptions> options, IVrchatProcessMonitor processMonitor, ILogger<VrchatLogReader> logger)
        : this(options, processMonitor, logger, File.GetCreationTimeUtc, DefaultOwnerQuery())
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
        Func<string, DateTime> creationTimeUtc)
        : this(options, processMonitor, logger, creationTimeUtc, queryOwners: null)
    {
    }

    /// <summary>
    /// Test-only seam for the owners too: <paramref name="queryOwners"/> stands in for
    /// <see cref="RestartManagerFileOwners.Query"/> (null: not available) and may throw
    /// like it.
    /// </summary>
    internal VrchatLogReader(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        ILogger<VrchatLogReader> logger,
        Func<string, DateTime> creationTimeUtc,
        Func<string, IReadOnlyList<VrchatLogOwners.Owner>>? queryOwners)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _logger = logger;
        _creationTimeUtc = creationTimeUtc;
        _queryOwners = queryOwners;
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

    public LogSnapshotResult ReadSnapshot()
    {
        var logDirectory = LogDirectory;
        if (logDirectory is null || !Directory.Exists(logDirectory))
        {
            // VRChat has never run on this machine, or the derived path is
            // wrong -- never throw, just report nothing available (Codex plan
            // review Phase 5, Round 3, Scenario A).
            return LogSnapshotResult.Empty();
        }

        var snapshot = _processMonitor.GetClients();

        // The pinned client keeps its log for as long as it is seen, whichever client
        // would be chosen as representative right now (e.g. while WMI cannot read its
        // command line for one poll). Only a confirmed exit, a reused PID, a deleted
        // file or Restart Manager (see Resolve) releases the pin; an observation that
        // cannot tell keeps it.
        if (_pinned is { } pinned)
        {
            if (!File.Exists(pinned.Path))
            {
                _pinned = null;
            }
            else if (snapshot.EnumerationError is not null)
            {
                return Unmatched("VRChat processes could not be checked; the log is shown again once they can be.");
            }
            else
            {
                var owner = snapshot.Clients.FirstOrDefault(c => c.ProcessId == pinned.Client.ProcessId);
                if (owner is not null && owner.CreationTimeUtc == pinned.Client.CreationTimeUtc)
                {
                    return Resolve(logDirectory, owner, snapshot, pinned.Path);
                }

                if (owner is { CreationTimeUtc: null } || (owner is null && snapshot.UnverifiedCount > 0))
                {
                    return Unmatched(
                        $"VRChat client {pinned.Client.ProcessId} could not be checked; " +
                        "its log is shown again once it can be.");
                }

                _pinned = null;
            }
        }

        if (snapshot.EnumerationError is not null)
        {
            return Unmatched("The VRChat process list could not be read.");
        }

        if (snapshot.Clients.Count == 0 && snapshot.UnverifiedCount == 0)
        {
            _lastReportedProblem = null;
            return ReadNewest(logDirectory);
        }

        var client = VrchatProcessMonitor.SelectRepresentative(snapshot.Clients);
        if (client is null)
        {
            return Unmatched("VRChat is running, but its processes could not be checked.");
        }

        return Resolve(logDirectory, client, snapshot, pinnedPath: null);
    }

    /// <summary>
    /// The log of <paramref name="client"/>, which is also the pinned client when
    /// <paramref name="pinnedPath"/> is given: the file Restart Manager shows only it has
    /// open, else the pinned file, else the name match. A file another VRChat has open,
    /// or that was seen mixed, is refused whichever rule picks it.
    /// </summary>
    private LogSnapshotResult Resolve(
        string logDirectory, VrchatClientInfo client, VrchatClientSnapshot snapshot, string? pinnedPath)
    {
        if (pinnedPath is not null && _queryOwners is null)
        {
            // Without Restart Manager nothing here can release the pin, as before it was asked.
            return Show(pinnedPath);
        }

        var (candidates, scanCapped) = ListCandidates(logDirectory);
        if (scanCapped)
        {
            // Nothing to check a pinned file against; it stays.
            return pinnedPath is null ? ScanCapped() : Show(pinnedPath);
        }

        _mixedLogs.RemoveWhere(path => !File.Exists(path));
        var ownership = QueryOwners(client, snapshot, candidates, pinnedPath);
        if (ownership is not null)
        {
            RecordMixed(ownership.Mixed);
        }

        switch (ownership)
        {
            case { Verdict: VrchatLogOwners.Verdict.Owned, Path: { } owned } when !_mixedLogs.Contains(owned):
                return Pin(client, owned, "the only VRChat process that has it open");

            case { Verdict: VrchatLogOwners.Verdict.Owned or VrchatLogOwners.Verdict.Shared, Path: { } mixed }:
                _pinned = null;
                return Unmatched(
                    $"{Path.GetFileName(mixed)}, the {OutputLogPattern} file of the first VRChat client " +
                    $"(PID {client.ProcessId}, {Started(client)}), is or was also open by other VRChat clients, " +
                    "which happens when their logging starts within the same second; " +
                    "not showing the mixed log as this client's.");

            case { Verdict: VrchatLogOwners.Verdict.Several }:
                _pinned = null;
                return Unmatched(CouldNotMatch(client));
        }

        // Restart Manager settled nothing, so the file names decide as before, except for
        // files it showed belong to another VRChat.
        bool Refused(string path) => _mixedLogs.Contains(path) || ownership?.Refused.Contains(path) == true;

        if (pinnedPath is not null)
        {
            if (!Refused(pinnedPath))
            {
                return Show(pinnedPath);
            }

            _pinned = null;
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
                return Pin(client, match, "matched by the time in its name");
            }

            result = new VrchatLogMatcher.MatchResult(null, VrchatLogMatcher.MatchFailure.Ambiguous);
        }

        if (HasNoLogYet(client, result, ownership))
        {
            return Unmatched(
                $"The first VRChat client (PID {client.ProcessId}, {Started(client)}) has not created its " +
                $"{OutputLogPattern} file yet. This usually takes about a second, and a minute or more under " +
                "heavy CPU load; its log is shown once it appears.");
        }

        return Unmatched(CouldNotMatch(client));
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

    private static string CouldNotMatch(VrchatClientInfo client) =>
        $"Could not match an {OutputLogPattern} file to the first VRChat client " +
        $"(PID {client.ProcessId}, {Started(client)}); " +
        "not showing another client's log instead. This happens when clients start within the same second " +
        "or write to one shared file, when VRChat created the log a minute or more after the client started, " +
        "or when the clock or time zone changed after the log was created.";

    /// <summary>
    /// The owners of the files created since shortly before the client started
    /// (<see cref="VrchatLogOwners.QueryLead"/>) and of the pinned file. Null when Restart
    /// Manager is not available (not Windows) or fails; a failure is logged once, and so is
    /// the recovery. With no such file there is nothing to ask, and the decision is that
    /// the client has no file open.
    /// </summary>
    private VrchatLogOwners.Decision? QueryOwners(
        VrchatClientInfo client,
        VrchatClientSnapshot snapshot,
        IReadOnlyList<VrchatLogMatcher.Candidate> candidates,
        string? pinnedPath)
    {
        if (_queryOwners is null || client.CreationTimeUtc is not { } created)
        {
            return null;
        }

        var paths = candidates
            .Where(c => c.CreationTimeUtc >= created - VrchatLogOwners.QueryLead)
            .Select(c => c.Path)
            .ToList();
        if (pinnedPath is not null && !paths.Contains(pinnedPath, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(pinnedPath);
        }

        var files = new List<VrchatLogOwners.FileOwners>(paths.Count);
        try
        {
            foreach (var path in paths)
            {
                files.Add(new VrchatLogOwners.FileOwners(path, _queryOwners(path)));
            }
        }
        catch (Exception ex)
        {
            // Display only, so any failure (a Restart Manager error, a missing rstrtmgr.dll)
            // just falls back to the names.
            if (ex.Message != _ownerQueryProblem)
            {
                _logger.LogWarning(
                    ex,
                    "Could not ask Windows Restart Manager which processes have the VRChat logs open; " +
                    "matching them by the times in their names instead.");
                _ownerQueryProblem = ex.Message;
            }

            return null;
        }

        if (_ownerQueryProblem is not null && files.Count > 0)
        {
            _logger.LogInformation("Windows Restart Manager answers again; matching VRChat logs by their owners.");
            _ownerQueryProblem = null;
        }

        return VrchatLogOwners.Decide(client, snapshot.Clients, files);
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
    private LogSnapshotResult Pin(VrchatClientInfo client, string path, string reason)
    {
        var pin = new PinnedLog(new ClientKey(client.ProcessId, client.CreationTimeUtc), path);
        if (pin != _pinned)
        {
            _pinned = pin;
            _logger.LogInformation(
                "Showing {Path} as the log of VRChat client {ProcessId} ({Reason}).", path, client.ProcessId, reason);
        }

        return Show(path);
    }

    private LogSnapshotResult Show(string path)
    {
        _lastReportedProblem = null;
        return ReadLog(path);
    }

    private LogSnapshotResult ReadNewest(string logDirectory)
    {
        var (newestFile, scanCapped) = FindNewestOutputLog(logDirectory);
        if (scanCapped)
        {
            return ScanCapped();
        }

        return newestFile is null ? LogSnapshotResult.Empty() : ReadLog(newestFile);
    }

    private LogSnapshotResult ReadLog(string path)
    {
        try
        {
            var lines = ReadTailLines(path);
            return LogSnapshotResult.Success(BuildBoundedOutput(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read VRChat log file {Path}.", path);
            return LogSnapshotResult.Empty();
        }
    }

    private static LogSnapshotResult ScanCapped() =>
        LogSnapshotResult.Diagnostic(
            $"Too many {OutputLogPattern} files in the VRChat log directory; " +
            $"no log was selected because the scan cap ({MaxDirectoryScan}) was exceeded.");

    /// <summary>
    /// Logged once per distinct problem, not on every 5-second poll, and only as
    /// information: right after a client starts, its file usually does not exist yet.
    /// </summary>
    private LogSnapshotResult Unmatched(string message)
    {
        if (message != _lastReportedProblem)
        {
            _logger.LogInformation("VRChat log not published: {Message}", message);
            _lastReportedProblem = message;
        }

        return LogSnapshotResult.Diagnostic(message);
    }

    /// <summary>Same bounded scan as <see cref="FindNewestOutputLog"/>; unreadable files are skipped.</summary>
    private (List<VrchatLogMatcher.Candidate> Candidates, bool ScanCapped) ListCandidates(string logDirectory) =>
        VrchatLogDirectory.ListCandidates(logDirectory, _creationTimeUtc);

    private readonly record struct ClientKey(int ProcessId, DateTime? CreationTimeUtc);

    private sealed record PinnedLog(ClientKey Client, string Path);

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
    /// </summary>
    private static string[] ReadTailLines(string path)
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

        var text = DecodeTolerantly(buffer, totalRead);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }

        // A trailing newline produces a final empty element; drop it.
        if (lines.Length > 0 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        // If the read window didn't start at byte 0, the first decoded line may
        // be a partial line (we seeked into the middle of the file) -- discard it
        // rather than publish a corrupted-looking fragment.
        if (start > 0 && lines.Length > 0)
        {
            lines = lines[1..];
        }

        return lines;
    }

    /// <summary>
    /// UTF-8 tolerant of invalid byte sequences (replacement character instead
    /// of throwing) -- real VRChat logs were confirmed BOM-less and
    /// UTF-8-compatible on real hardware, but a mid-file seek could still land
    /// on a partial multi-byte sequence at the very start of the window, which
    /// this must not crash on (Codex plan review Phase 5, Round 2).
    /// </summary>
    private static string DecodeTolerantly(byte[] buffer, int count)
    {
        var decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        return decoder.GetString(buffer, 0, count);
    }

    private static string BuildBoundedOutput(string[] lines)
    {
        var selected = lines.TakeLast(PublishedLineLimit).Select(TruncateLine).ToArray();

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

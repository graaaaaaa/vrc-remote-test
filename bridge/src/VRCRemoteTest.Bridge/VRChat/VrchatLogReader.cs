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
/// every poll. The file is matched by <see cref="VrchatLogMatcher"/> and then kept
/// for that client (its PID and creation time) until the client exits or the file
/// disappears; when no file can be matched, a diagnostic is published instead.
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

    /// <summary>Only VrchatLogService's sequential loop calls this reader, so no synchronization is needed.</summary>
    private PinnedLog? _pinned;

    private string? _lastReportedProblem;

    public VrchatLogReader(
        IOptions<BridgeOptions> options, IVrchatProcessMonitor processMonitor, ILogger<VrchatLogReader> logger)
        : this(options, processMonitor, logger, File.GetCreationTimeUtc)
    {
    }

    /// <summary>
    /// Test-only seam: not every filesystem the tests run on can set a file's creation
    /// time, and matching depends on it.
    /// </summary>
    internal VrchatLogReader(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        ILogger<VrchatLogReader> logger,
        Func<string, DateTime> creationTimeUtc)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _logger = logger;
        _creationTimeUtc = creationTimeUtc;
    }

    private string? LogDirectory => VrchatLogDirectory.Resolve(_options);

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
        // command line for one poll). Only a confirmed exit, a reused PID or a deleted
        // file releases the pin; an observation that cannot tell keeps it.
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
                    _lastReportedProblem = null;
                    return ReadLog(pinned.Path);
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

        var clientKey = new ClientKey(client.ProcessId, client.CreationTimeUtc);

        var (candidates, scanCapped) = ListCandidates(logDirectory);
        if (scanCapped)
        {
            return ScanCapped();
        }

        // A VRChat that could not be checked might own any file in the range.
        var result = snapshot.UnverifiedCount == 0
            ? VrchatLogMatcher.Evaluate(client, snapshot.Clients, candidates, TimeZoneInfo.Local)
            : new VrchatLogMatcher.MatchResult(null, VrchatLogMatcher.MatchFailure.Ambiguous);
        if (result.Path is not { } match)
        {
            var started = client.CreationTimeUtc is { } created
                ? $"started {created:yyyy-MM-dd HH:mm:ss}Z"
                : "start time unknown";

            // VRChat created its output_log 0.70-0.84 s after the process start on real
            // hardware without load, and 11-22 s after it with the CPU at about 100%
            // (docs/validation/multi-client-spike.md, PR #2 verification, item 12), so the
            // first poll after a cold start usually finds no file yet. Saying so replaces the
            // previous session's log at once: publishing nothing would leave that closed
            // client's log on the share, shown as if it were this client's (a9292a1
            // re-verification). Past MaxRangeLength after the start, a new file can no
            // longer be named in the client's range, so "not yet" would never end and the
            // generic message applies.
            if (result.Failure == VrchatLogMatcher.MatchFailure.NoFileYet
                && DateTime.UtcNow - client.CreationTimeUtc < VrchatLogMatcher.MaxRangeLength)
            {
                return Unmatched(
                    $"The first VRChat client (PID {client.ProcessId}, {started}) has not created its " +
                    $"{OutputLogPattern} file yet. This usually takes about a second, and 20 seconds or more " +
                    "under heavy CPU load; its log is shown once it appears.");
            }

            return Unmatched(
                $"Could not match an {OutputLogPattern} file to the first VRChat client " +
                $"(PID {client.ProcessId}, {started}); " +
                "not showing another client's log instead. This happens when clients start within the same second " +
                "or write to one shared file, when VRChat created the log a minute or more after the client started, " +
                "or when the clock or time zone changed after the log was created.");
        }

        _pinned = new PinnedLog(clientKey, match);
        _lastReportedProblem = null;
        _logger.LogInformation(
            "Showing {Path} as the log of VRChat client {ProcessId}.", match, client.ProcessId);
        return ReadLog(match);
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

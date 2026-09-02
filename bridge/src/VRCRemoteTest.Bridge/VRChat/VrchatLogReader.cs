using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Stateless snapshot reader: every call re-resolves the newest output_log_*.txt
/// and reads a bounded tail window, rather than tracking a persistent
/// (file, offset, incomplete-line-buffer) state machine. This design replaced an
/// original stateful-tailer proposal that Codex plan review found underspecified
/// (FileShare flags, encoding, rotation/truncation handling) and over-built for a
/// "glance at recent logs" developer convenience feature (Codex plan review
/// Phase 5, Rounds 1-3, confidence converged to 0.91). Real VRChat output_log
/// files were inspected on real Windows hardware before writing this class:
/// filename pattern output_log_YYYY-MM-DD_HH-MM-SS.txt, no BOM, UTF-8-compatible
/// encoding, lines shaped like "YYYY.MM.DD HH:MM:SS Level    - Message".
/// </summary>
public sealed class VrchatLogReader : IVrchatLogReader
{
    private const string OutputLogPattern = "output_log_*.txt";

    /// <summary>Directory enumeration cap (Codex plan review Phase 5, Round 2, confidence 0.87).</summary>
    private const int MaxDirectoryScan = 512;

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
    private readonly ILogger<VrchatLogReader> _logger;

    public VrchatLogReader(IOptions<BridgeOptions> options, ILogger<VrchatLogReader> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Derived from VrchatWorldsDirectory's parent (...\VRChat\VRChat\Worlds ->
    /// ...\VRChat\VRChat, where output_log_*.txt lives), matching the derivation
    /// approach considered (and left unimplemented) in Phase 4.1. No new
    /// BridgeOptions property is added; failure to derive degrades gracefully.
    /// </summary>
    private string? LogDirectory =>
        string.IsNullOrWhiteSpace(_options.VrchatWorldsDirectory)
            ? null
            : Directory.GetParent(_options.VrchatWorldsDirectory)?.FullName;

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

        var (newestFile, scanCapped) = FindNewestOutputLog(logDirectory);
        if (scanCapped)
        {
            return LogSnapshotResult.Diagnostic(
                $"Too many {OutputLogPattern} files in the VRChat log directory; " +
                $"newest log was not selected because the scan cap ({MaxDirectoryScan}) was exceeded.");
        }

        if (newestFile is null)
        {
            return LogSnapshotResult.Empty();
        }

        try
        {
            var lines = ReadTailLines(newestFile);
            return LogSnapshotResult.Success(BuildBoundedOutput(lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read VRChat log file {Path}.", newestFile);
            return LogSnapshotResult.Empty();
        }
    }

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

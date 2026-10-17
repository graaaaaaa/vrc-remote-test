using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Independent from VrchatMonitorService and StagingWatcher (Codex plan review
/// Phase 5, Round 3: the stateless snapshot design makes per-poll I/O cheap
/// enough to justify a dedicated 5s cadence -- faster than VrchatMonitorService's
/// 10s, since a log viewer a developer is actively watching implies an
/// expectation of near-live updates that a 10s-shared loop would not meet).
///
/// Every iteration is wrapped in try/catch: an unhandled exception escaping
/// BackgroundService.ExecuteAsync stops the entire Generic Host by default (.NET
/// 6+ BackgroundServiceExceptionBehavior) -- the same lesson Phase 4a learned the
/// hard way for VrchatMonitorService, applied here from the start.
///
/// While the deploy flow shows a <see cref="VrchatLogNotice"/> (Relaunch mode, from just
/// before closing the recorded clients until the first new one is recorded), that line is
/// published instead of the log, and the log is not read. Showing or clearing it wakes this
/// loop, so the line replaces the log, and the log returns, without waiting for the next poll.
/// </summary>
public sealed class VrchatLogService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly BridgeOptions _options;
    private readonly IVrchatLogReader _logReader;
    private readonly VrchatLogNotice _notice;
    private readonly ILogger<VrchatLogService> _logger;
    private readonly SharedFileWriter _fileWriter;
    private readonly TimeSpan _pollInterval;

    public VrchatLogService(
        IOptions<BridgeOptions> options,
        IVrchatLogReader logReader,
        VrchatLogNotice notice,
        ILogger<VrchatLogService> logger)
        : this(options, logReader, notice, logger, DefaultPollInterval)
    {
    }

    /// <summary>Test-only seam: a short poll interval keeps unit tests fast without a full clock abstraction.</summary>
    internal VrchatLogService(
        IOptions<BridgeOptions> options,
        IVrchatLogReader logReader,
        VrchatLogNotice notice,
        ILogger<VrchatLogService> logger,
        TimeSpan pollInterval)
    {
        _options = options.Value;
        _logReader = logReader;
        _notice = notice;
        _logger = logger;
        _fileWriter = new SharedFileWriter(logger);
        _pollInterval = pollInterval;
    }

    private string LogsDirectory => Path.Combine(_options.StagingDirectory, "logs");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = NextSnapshot();
                if (snapshot.IsAvailable)
                {
                    Publish(snapshot.Content);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error while polling the VRChat log.");
            }

            try
            {
                await _notice.WaitForChangeAsync(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// A notice shown while the log was being read replaces what was read, which is then
    /// the log of a client being closed. One shown after this check is published on the
    /// next iteration, which its wake starts at once.
    /// </summary>
    private LogSnapshotResult NextSnapshot()
    {
        if (_notice.Message is { } shown)
        {
            return LogSnapshotResult.Diagnostic(shown);
        }

        var snapshot = _logReader.ReadSnapshot();
        return _notice.Message is { } message ? LogSnapshotResult.Diagnostic(message) : snapshot;
    }

    /// <summary>
    /// Tmp-then-rename like ResultWriter, except that a destination held open by the
    /// Mac's SMB client is replaced with POSIX semantics rather than failing every poll
    /// (see SharedFileWriter).
    /// </summary>
    private void Publish(string content)
    {
        Directory.CreateDirectory(LogsDirectory);
        _fileWriter.Write(Path.Combine(LogsDirectory, "vrchat-latest.log"), content);
    }
}

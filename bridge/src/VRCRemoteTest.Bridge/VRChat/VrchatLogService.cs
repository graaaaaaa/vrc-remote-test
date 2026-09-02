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
/// </summary>
public sealed class VrchatLogService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private readonly BridgeOptions _options;
    private readonly IVrchatLogReader _logReader;
    private readonly ILogger<VrchatLogService> _logger;
    private readonly TimeSpan _pollInterval;

    public VrchatLogService(
        IOptions<BridgeOptions> options,
        IVrchatLogReader logReader,
        ILogger<VrchatLogService> logger)
        : this(options, logReader, logger, DefaultPollInterval)
    {
    }

    /// <summary>Test-only seam: a short poll interval keeps unit tests fast without a full clock abstraction.</summary>
    internal VrchatLogService(
        IOptions<BridgeOptions> options,
        IVrchatLogReader logReader,
        ILogger<VrchatLogService> logger,
        TimeSpan pollInterval)
    {
        _options = options.Value;
        _logReader = logReader;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    private string LogsDirectory => Path.Combine(_options.StagingDirectory, "logs");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = _logReader.ReadSnapshot();
                if (snapshot.IsAvailable)
                {
                    WriteAtomic(snapshot.Content, LogsDirectory);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error while polling the VRChat log.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Same tmp-then-rename pattern as ResultWriter/VrchatStatusWriter.</summary>
    private static void WriteAtomic(string content, string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);

        var finalPath = Path.Combine(logsDirectory, "vrchat-latest.log");
        var tempPath = Path.Combine(logsDirectory, "vrchat-latest.log.tmp");

        File.WriteAllText(tempPath, content);
        File.Move(tempPath, finalPath, overwrite: true);
    }
}

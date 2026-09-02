using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Independent from StagingWatcher (Codex plan review Phase 4a, confidence: separate
/// service avoids constructor bloat and test complexity on StagingWatcher, since VRChat
/// monitoring is an unrelated concern per StagingWatcher's own doc comment). Polls at a
/// lower frequency than StagingWatcher's 5s file scan since process start/stop is a less
/// frequent event.
///
/// Every iteration is wrapped in try/catch: an unhandled exception escaping
/// BackgroundService.ExecuteAsync stops the entire Generic Host by default (.NET 6+
/// BackgroundServiceExceptionBehavior), which would take StagingWatcher's build
/// processing down with it -- confirmed as a required fix by Codex plan review Round 3
/// (confidence 0.99). Mirrors StagingWatcher.ExecuteAsync's own try/catch pattern.
/// </summary>
public sealed class VrchatMonitorService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly BridgeOptions _options;
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly IVrchatStatusWriter _statusWriter;
    private readonly ILogger<VrchatMonitorService> _logger;
    private readonly TimeSpan _pollInterval;

    public VrchatMonitorService(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatStatusWriter statusWriter,
        ILogger<VrchatMonitorService> logger)
        : this(options, processMonitor, statusWriter, logger, DefaultPollInterval)
    {
    }

    /// <summary>Test-only seam: a short poll interval keeps unit tests fast without a full clock abstraction.</summary>
    internal VrchatMonitorService(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatStatusWriter statusWriter,
        ILogger<VrchatMonitorService> logger,
        TimeSpan pollInterval)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _statusWriter = statusWriter;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    private string StatusDirectory => Path.Combine(_options.StagingDirectory, "status");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = _processMonitor.GetStatus();
                _statusWriter.Write(status, StatusDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error while polling VRChat process status.");
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
}

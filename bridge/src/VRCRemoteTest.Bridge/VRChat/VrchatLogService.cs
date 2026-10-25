using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Lan;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Independent from VrchatMonitorService and StagingWatcher (Codex plan review
/// Phase 5, Round 3: the stateless snapshot design makes per-poll I/O cheap
/// enough to justify a dedicated 5s cadence -- faster than VrchatMonitorService's
/// 10s, since a log viewer a developer is actively watching implies an
/// expectation of near-live updates that a 10s-shared loop would not meet).
///
/// Every poll publishes, from one enumeration of the VRChat clients
/// (client-observation plan F1-F3):
/// - logs/vrchat-latest.log, the first client's log as before slots (Unity 0.5.x);
/// - logs/clients/slot-{n}.log for slots 0-3, each slot's log tail or why it is not shown;
/// - logs/clients/slot-{n}.markers.log when its content changed, after keeping the previous
///   client's markers as slot-{n}.markers.prev.log when a new client took the slot;
/// - status/vrchat-clients.json.
///
/// A log copy whose content did not change since it was last written is not written again.
/// A markers or .prev file counts as written only when PrivateSnapshotWriter replaced it (not when
/// a reader kept it busy), and the observer offers it again until then; a slot's new markers
/// wait until its .prev is in place.
///
/// Every iteration is wrapped in try/catch: an unhandled exception escaping
/// BackgroundService.ExecuteAsync stops the entire Generic Host by default (.NET
/// 6+ BackgroundServiceExceptionBehavior) -- the same lesson Phase 4a learned the
/// hard way for VrchatMonitorService, applied here from the start. A file that cannot be
/// written is logged and the others are still written.
///
/// While the deploy flow shows a <see cref="VrchatLogNotice"/> (Relaunch mode, from just
/// before closing the recorded clients until the first new one is recorded), that line is
/// published instead of every log copy, and the logs are not read. Showing or clearing it
/// wakes this loop, so the line replaces the logs, and the logs return, without waiting for
/// the next poll.
/// </summary>
public sealed class VrchatLogService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly BridgeOptions _options;
    private readonly IVrchatLogReader _logReader;
    private readonly VrchatClientObserver _observer;
    private readonly VrchatLogNotice _notice;
    private readonly ILogger<VrchatLogService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly LogSourceRegistry? _logSources;

    /// <summary>One writer per published file, so each logs its own outcome changes.</summary>
    private readonly Dictionary<string, PrivateSnapshotWriter> _writers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _lastWriteProblem = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each log copy was last written with.</summary>
    private readonly Dictionary<string, string> _written = new(StringComparer.OrdinalIgnoreCase);

    public VrchatLogService(
        IOptions<BridgeOptions> options,
        IVrchatLogReader logReader,
        VrchatClientObserver observer,
        VrchatLogNotice notice,
        ILogger<VrchatLogService> logger,
        LogSourceRegistry logSources)
        : this(options, logReader, observer, notice, logger, DefaultPollInterval, logSources)
    {
    }

    /// <summary>Test-only seam: a short poll interval keeps unit tests fast without a full clock abstraction.</summary>
    internal VrchatLogService(
        IOptions<BridgeOptions> options,
        IVrchatLogReader logReader,
        VrchatClientObserver observer,
        VrchatLogNotice notice,
        ILogger<VrchatLogService> logger,
        TimeSpan pollInterval,
        LogSourceRegistry? logSources = null)
    {
        _options = options.Value;
        _logReader = logReader;
        _observer = observer;
        _notice = notice;
        _logger = logger;
        _pollInterval = pollInterval;
        _logSources = logSources;
    }

    private string LogsDirectory => Path.Combine(_options.StateDirectory, "logs");

    private string ClientLogsDirectory => Path.Combine(LogsDirectory, "clients");

    private string StatusDirectory => Path.Combine(_options.StateDirectory, "status");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PollOnce(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error while polling the VRChat log.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
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
    /// A notice shown while the logs were being read replaces what was read, which is then
    /// the logs of clients being closed. One shown after this check is published on the
    /// next iteration, which its wake starts at once.
    /// </summary>
    private void PollOnce(CancellationToken cancellationToken)
    {
        if (_notice.Message is { } shown)
        {
            PublishNotice(shown);
            return;
        }

        var read = _logReader.Read();
        if (_notice.Message is { } message)
        {
            PublishNotice(message);
            return;
        }

        var observation = _observer.Observe(read, cancellationToken);
        _logSources?.Publish(read, observation.Clients);

        if (read.Latest.IsAvailable)
        {
            PublishIfChanged(Path.Combine(LogsDirectory, "vrchat-latest.log"), read.Latest.Content);
        }

        foreach (var slot in read.Slots)
        {
            if (!slot.Content.IsAvailable)
            {
                continue;
            }

            var content = observation.SlotNotices.TryGetValue(slot.Slot, out var notices)
                ? string.Join("\n", notices.Select(n => "# " + n).Append(slot.Content.Content))
                : slot.Content.Content;
            PublishIfChanged(SlotLogPath(slot.Slot), content);
        }

        var waiting = new HashSet<int>();
        foreach (var previous in observation.PreviousMarkers)
        {
            if (KeepPreviousMarkers(previous))
            {
                _observer.AcknowledgePrevious(previous);
            }
            else
            {
                waiting.Add(previous.Slot);
            }
        }

        foreach (var markers in observation.MarkerFiles)
        {
            if (!waiting.Contains(markers.Slot) && Publish(MarkersPath(markers.Slot), markers.Content))
            {
                _observer.AcknowledgeMarkers(markers.Slot, markers.Key);
            }
        }

        Publish(
            Path.Combine(StatusDirectory, "vrchat-clients.json"),
            JsonSerializer.Serialize(observation.Clients, SerializerOptions));
    }

    private void PublishNotice(string message)
    {
        _logSources?.Clear();
        PublishIfChanged(Path.Combine(LogsDirectory, "vrchat-latest.log"), message);
        for (var slot = 0; slot < VrchatClientRoster.SlotCount; slot++)
        {
            PublishIfChanged(SlotLogPath(slot), message);
        }
    }

    private string SlotLogPath(int slot) => Path.Combine(ClientLogsDirectory, $"slot-{slot}.log");

    private string MarkersPath(int slot) => Path.Combine(ClientLogsDirectory, $"slot-{slot}.markers.log");

    private string PreviousMarkersPath(int slot) => Path.Combine(ClientLogsDirectory, $"slot-{slot}.markers.prev.log");

    /// <summary>
    /// Keeps the previous client's markers as slot-{n}.markers.prev.log, replacing the older
    /// one, so only the one generation before is kept (client-observation plan C5). Without
    /// the markers in memory (that client ran before this Bridge started), the published file
    /// is moved there as it is, without being read: the private logs directory is the Bridge's to write. True once done.
    /// </summary>
    private bool KeepPreviousMarkers(PreviousMarkers previous)
    {
        var previousPath = PreviousMarkersPath(previous.Slot);
        if (previous.Content is { } content)
        {
            return Publish(previousPath, content);
        }

        var current = MarkersPath(previous.Slot);
        try
        {
            if (File.Exists(current))
            {
                File.Move(current, previousPath, overwrite: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_lastWriteProblem.TryGetValue(previousPath, out var problem) || problem != ex.Message)
            {
                _logger.LogWarning(ex, "Could not move {Path} to {Previous}; trying again on the next poll.", current, previousPath);
                _lastWriteProblem[previousPath] = ex.Message;
            }

            return false;
        }
    }

    private void PublishIfChanged(string path, string content)
    {
        if (_written.TryGetValue(path, out var written) && written == content)
        {
            return;
        }

        if (Publish(path, content))
        {
            _written[path] = content;
        }
        else
        {
            _written.Remove(path);
        }
    }

    /// <summary>
    /// Tmp-then-rename like ResultWriter, except that a destination held open by the
    /// API reader is replaced atomically rather than observing a partial write
    /// (see PrivateSnapshotWriter). A failure is logged once per file and problem.
    /// </summary>
    private bool Publish(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!_writers.TryGetValue(path, out var writer))
            {
                writer = new PrivateSnapshotWriter(_logger);
                _writers[path] = writer;
            }

            var outcome = writer.Write(path, content);
            _lastWriteProblem.Remove(path);
            return outcome != SnapshotWriteOutcome.DestinationBusy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_lastWriteProblem.TryGetValue(path, out var problem) || problem != ex.Message)
            {
                _logger.LogError(ex, "Could not publish {Path}.", path);
                _lastWriteProblem[path] = ex.Message;
            }

            return false;
        }
    }
}

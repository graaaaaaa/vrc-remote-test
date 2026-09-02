using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Reuses IVrchatProcessMonitor (Phase 4a, already real-hardware-verified) rather
/// than duplicating process detection. Readiness requires the WMI-based
/// IsRunning/WatchWorldsDetected signal to be stable across two consecutive polls
/// AND at least StartupSettleDelay to have elapsed since the process's own
/// StartTimeUtc.
///
/// This design replaces an earlier "WMI + a new output_log file" proposal that
/// Codex plan review found internally inconsistent (an "already running" fast
/// path skipped the output_log check entirely) and over-reliant on output_log as
/// a proxy for "Worlds-directory watcher armed" -- which it doesn't actually
/// prove any better than the WMI signal does (Codex plan review Phase 4.1,
/// Rounds 1-3, confidence converged to 0.91). StartupSettleDelay is NOT a blind
/// fixed sleep (spec section 31 forbids that): it is a floor layered on top of a
/// continuously-polled real signal, not a substitute for one. This is a
/// deliberate, documented deviation from the spec's literal "new output_log
/// file" wording.
/// </summary>
public sealed class VrchatReadinessCoordinator : IVrchatReadinessCoordinator
{
    private const int RequiredStablePolls = 2;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly BridgeOptions _options;
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly IVrchatLauncher _launcher;
    private readonly ILogger<VrchatReadinessCoordinator> _logger;
    private readonly TimeSpan _startupSettleDelay;
    private readonly TimeSpan _pollInterval;

    public VrchatReadinessCoordinator(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatLauncher launcher,
        ILogger<VrchatReadinessCoordinator> logger)
        : this(
            options,
            processMonitor,
            launcher,
            logger,
            TimeSpan.FromSeconds(options.Value.VrchatStartupSettleDelaySeconds),
            DefaultPollInterval)
    {
    }

    /// <summary>Test-only seam: tiny TimeSpans keep unit tests fast without a full clock abstraction.</summary>
    internal VrchatReadinessCoordinator(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatLauncher launcher,
        ILogger<VrchatReadinessCoordinator> logger,
        TimeSpan startupSettleDelay,
        TimeSpan pollInterval)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _launcher = launcher;
        _logger = logger;
        _startupSettleDelay = startupSettleDelay;
        _pollInterval = pollInterval;
    }

    public async Task<ReadinessResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        var initial = _processMonitor.GetStatus();

        if (!IsWatchWorldsCandidate(initial))
        {
            if (initial.IsRunning)
            {
                // Already running without --watch-worlds: never auto-restart an
                // existing VRChat instance (spec section 32).
                const string message = "VRChat is already running without --watch-worlds; will not auto-restart.";
                _logger.LogWarning(message);
                return ReadinessResult.Failure(ErrorCode.VrchatWatchWorldsMissing, message);
            }

            var launched = await _launcher.LaunchAsync(_options.VrchatExecutable, _options.VrchatMode)
                .ConfigureAwait(false);
            if (!launched)
            {
                const string message = "Failed to launch VRChat.";
                _logger.LogError(message);
                return ReadinessResult.Failure(ErrorCode.VrchatStartFailed, message);
            }
        }

        return await WaitForStableWatchWorldsProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// stablePolls/lastPid/firstObservedAtUtc are local to this call, not mutable
    /// fields on this (singleton) service -- multiple sequential builds under
    /// AutoLaunchVrchat must not leak state between EnsureReadyAsync calls
    /// (Codex plan review Phase 4.1, Round 3, Scenario B).
    /// </summary>
    private async Task<ReadinessResult> WaitForStableWatchWorldsProcessAsync(CancellationToken cancellationToken)
    {
        var stablePolls = 0;
        int? lastPid = null;
        DateTimeOffset? firstObservedAtUtc = null;

        var startupTimeout = TimeSpan.FromSeconds(_options.VrchatStartupTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(startupTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (!linkedCts.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var status = _processMonitor.GetStatus();

            if (!IsWatchWorldsCandidate(status))
            {
                stablePolls = 0;
                lastPid = null;
                firstObservedAtUtc = null;
            }
            else
            {
                if (lastPid != status.ProcessId)
                {
                    lastPid = status.ProcessId;
                    stablePolls = 0;
                    firstObservedAtUtc = now;
                }

                stablePolls++;

                // StartTimeUtc can be null (VrchatProcessMonitor.TryGetStartTime
                // returns null on access failure); fall back to when this call
                // first observed the candidate process.
                var settleBaseline = status.StartTimeUtc ?? firstObservedAtUtc ?? now;
                var settled = now - settleBaseline >= _startupSettleDelay;

                if (stablePolls >= RequiredStablePolls && settled)
                {
                    return ReadinessResult.Ready();
                }
            }

            try
            {
                await Task.Delay(_pollInterval, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Bridge shutdown: propagate as a real cancellation, not a readiness failure.
                throw;
            }
            catch (OperationCanceledException)
            {
                break; // timeoutCts fired: readiness timeout, fall through below.
            }
        }

        var message = $"Timed out after {startupTimeout.TotalSeconds}s waiting for VRChat to become ready.";
        _logger.LogError(message);
        return ReadinessResult.Failure(ErrorCode.VrchatStartTimeout, message);
    }

    private static bool IsWatchWorldsCandidate(VrchatStatus status) =>
        status.IsRunning && status.WatchWorldsDetected && status.ProcessId is not null;
}

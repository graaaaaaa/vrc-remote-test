using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Runs the same-account multi-client flow (multi-client plan, option B, sections 11
/// and 13): with no VRChat running, the build is installed first and N clients are
/// launched with --url pointing at it, so they all enter the same instance without
/// waiting for --watch-worlds; with clients already running, the build is only
/// installed and every --watch-worlds client reloads into the account's shared
/// instance (docs/validation/multi-client-spike.md, cases 1, 2 and 7).
///
/// The wait before a plain deploy is the Phase 4.1 readiness rule applied to every
/// client: a client only picks up a deployed file once it has logged in (spike gate
/// for option B), and nothing tells the Bridge when that happened. So the same set of
/// (PID, creation time) must be seen on two consecutive polls, and every client must
/// be at least StartupSettleDelay past its creation time. StartupSettleDelay is NOT a
/// blind fixed sleep (spec section 31 forbids that): it is a floor layered on top of a
/// continuously-polled real signal. This is what keeps a second build that arrives
/// right after a cold start from being deployed before the new clients can see it.
///
/// Missing clients are never added next to running ones: a client started with --url
/// enters a different instance than the reloaded ones until the next deploy (spike
/// cases 1 and 2). Closing and relaunching clients is the separate-account flow
/// (option C, <see cref="VrchatRelaunchCoordinator"/>), only ever used when
/// VrchatClientMode is Relaunch.
/// </summary>
public sealed class VrchatClientCoordinator : IVrchatClientCoordinator
{
    private const int RequiredStablePolls = 2;

    /// <summary>See <see cref="VrchatLaunchPacer.LogPollInterval"/>; the launch pacing is shared with Relaunch mode.</summary>
    internal static readonly TimeSpan LogPollInterval = VrchatLaunchPacer.LogPollInterval;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly BridgeOptions _options;
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly IVrchatLauncher _launcher;
    private readonly ILogger<VrchatClientCoordinator> _logger;
    private readonly TimeSpan _startupSettleDelay;
    private readonly TimeSpan _pollInterval;
    private readonly Func<DateTime> _utcNow;
    private readonly VrchatLaunchPacer _pacer;

    /// <summary>
    /// Last client mix reported by <see cref="ReportClientMix"/>. Only StagingWatcher's
    /// sequential loop calls this service, inside the operation gate (BridgeOperationGate),
    /// so no synchronization is needed.
    /// </summary>
    private ClientMix? _lastReportedMix;

    /// <summary>Where launched clients are recorded so the LAN API can close them later; null in unit tests that do not need it.</summary>
    private readonly IManagedClientStore? _reloadRecord;

    public VrchatClientCoordinator(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatLauncher launcher,
        ILogger<VrchatClientCoordinator> logger,
        ReloadClientStore reloadClients)
        : this(
            options,
            processMonitor,
            launcher,
            logger,
            TimeSpan.FromSeconds(options.Value.VrchatStartupSettleDelaySeconds),
            DefaultPollInterval,
            Task.Delay,
            () => VrchatLaunchPacer.ListLogFiles(options.Value),
            () => DateTime.UtcNow,
            TimeZoneInfo.Local,
            reloadClients.Store)
    {
    }

    /// <summary>
    /// Test-only seam: tiny TimeSpans and a recording delay keep unit tests fast without a
    /// full clock abstraction. The launch pacing reads the time only through
    /// <paramref name="utcNow"/> and waits only through <paramref name="launchDelay"/>, so a
    /// test can drive it with a fake clock and a fake log directory.
    /// </summary>
    internal VrchatClientCoordinator(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatLauncher launcher,
        ILogger<VrchatClientCoordinator> logger,
        TimeSpan startupSettleDelay,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task> launchDelay,
        Func<IReadOnlyList<VrchatLogMatcher.Candidate>?> listLogFiles,
        Func<DateTime> utcNow,
        TimeZoneInfo timeZone,
        IManagedClientStore? reloadRecord = null)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _launcher = launcher;
        _logger = logger;
        _reloadRecord = reloadRecord;
        _startupSettleDelay = startupSettleDelay;
        _pollInterval = pollInterval;
        _utcNow = utcNow;
        _pacer = new VrchatLaunchPacer(logger, launchDelay, listLogFiles, utcNow, timeZone);
    }

    /// <summary>
    /// Applied in order (multi-client plan section 11.2): a list that cannot be read
    /// fails; any --watch-worlds client means a deploy after the readiness wait, even
    /// next to clients without it (as the single-client monitor already preferred a
    /// --watch-worlds process); a VRChat that could not be checked blocks the cold start;
    /// no VRChat at all is a cold start; only clients without --watch-worlds fail as
    /// before, without restarting them (spec section 32).
    /// </summary>
    public async Task<VrchatPreparation> PrepareAsync(CancellationToken cancellationToken)
    {
        var snapshot = _processMonitor.GetClients();
        if (snapshot.EnumerationError is not null)
        {
            return Unmanaged($"the VRChat process list could not be read: {snapshot.EnumerationError}");
        }

        if (snapshot.Clients.Any(c => c.WatchWorldsDetected))
        {
            ReportClientMix(snapshot);
            return await WaitUntilClientsReadyAsync(cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.UnverifiedCount > 0)
        {
            return Unmanaged($"{snapshot.UnverifiedCount} VRChat process(es) could not be checked");
        }

        if (snapshot.Clients.Count == 0)
        {
            return VrchatPreparation.ColdStart();
        }

        const string message = "VRChat is already running without --watch-worlds; will not auto-restart.";
        _logger.LogWarning(message);
        return VrchatPreparation.Failure(ErrorCode.VrchatWatchWorldsMissing, message);
    }

    /// <summary>
    /// One room id for the whole batch, so the clients ask for the same instance like
    /// the SDK's Build &amp; Test. Stops at the first client that fails to start: the
    /// build is already installed and archived, so the result reports how many started.
    /// A stop request is honoured before every launch, the first one included (the
    /// install and archive before it are synchronous and never look at the token), and
    /// once more before reporting success, so a stopping Bridge launches nothing more
    /// and StagingWatcher writes no result (multi-client plan section 11.3). The check
    /// is cooperative: a request that arrives during Process.Start still lets that
    /// client start.
    /// </summary>
    public async Task<VrchatLaunchOutcome> LaunchClientsAsync(
        string deployedWorldPath, CancellationToken cancellationToken)
    {
        var count = _options.ResolvedVrchatClientCount;
        var roomId = VrchatLaunchRequest.NewRoomId();
        var vrMode = string.Equals(_options.VrchatMode, "VR", StringComparison.OrdinalIgnoreCase);

        var batch = _pacer.StartBatch(count);
        var previousLaunchUtc = DateTime.MinValue;

        // A cold start means no VRChat runs, so whatever the record lists has exited.
        var recorded = new List<ManagedClient>();
        TrySaveReloadRecord(recorded);

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                await _pacer.WaitBeforeNextLaunchAsync(batch, i, previousLaunchUtc, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Only one client can use the HMD, so later ones always run on the desktop.
            var request = new VrchatLaunchRequest(
                _options.VrchatExecutable, deployedWorldPath, roomId, NoVr: !(vrMode && i == 0));

            cancellationToken.ThrowIfCancellationRequested();

            previousLaunchUtc = _utcNow();
            LaunchResult result;
            try
            {
                result = await _launcher.LaunchAsync(request).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = LaunchResult.Failure(ex.Message);
            }

            if (!result.Succeeded)
            {
                var message =
                    $"Launched {i} of {count} VRChat client(s); client {i + 1} failed to start: {result.ErrorMessage}";
                _logger.LogError(message);
                return VrchatLaunchOutcome.Failure(message, requested: count, started: i);
            }

            _logger.LogInformation(
                "Launched VRChat client {Index}/{Count} (PID {ProcessId}, created {CreationTimeUtc:O}, roomId {RoomId}).",
                i + 1, count, result.ProcessId, result.CreationTimeUtc, roomId);

            if (result is { ProcessId: { } processId, CreationTimeUtc: { } created, SessionId: { } session })
            {
                recorded.Add(new ManagedClient(processId, created, session, Profile: 0, LaunchOrder: i));
                TrySaveReloadRecord(recorded);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return VrchatLaunchOutcome.Success(count);
    }

    /// <summary>
    /// Reload mode never depends on this record (it reuses any --watch-worlds client), so a
    /// write that fails only means the LAN API cannot close that client later.
    /// </summary>
    private void TrySaveReloadRecord(IReadOnlyList<ManagedClient> clients)
    {
        if (_reloadRecord is null)
        {
            return;
        }

        try
        {
            _reloadRecord.Save(clients);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Could not record the launched VRChat clients in {Path}; they cannot be closed through the LAN API.",
                _reloadRecord.FilePath);
        }
    }

    /// <summary>
    /// stablePolls/previous/firstObservedUtc are local to this call, not mutable fields
    /// on this (singleton) service -- sequential builds must not leak state between
    /// waits (Codex plan review Phase 4.1, Round 3, Scenario B). GetClients is
    /// synchronous and neither the deadline nor a stop request can interrupt it, so
    /// both are checked again after every observation: a slow observation that returns
    /// past the deadline never counts as ready.
    /// </summary>
    private async Task<VrchatPreparation> WaitUntilClientsReadyAsync(CancellationToken cancellationToken)
    {
        var stablePolls = 0;
        HashSet<ClientKey>? previous = null;
        var firstObservedUtc = new Dictionary<ClientKey, DateTimeOffset>();
        var reportedWait = false;

        var startupTimeout = TimeSpan.FromSeconds(_options.VrchatStartupTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(startupTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (!linkedCts.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var snapshot = _processMonitor.GetClients();
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutCts.IsCancellationRequested)
            {
                break;
            }

            if (snapshot.EnumerationError is not null)
            {
                return Unmanaged($"the VRChat process list could not be read: {snapshot.EnumerationError}");
            }

            var current = snapshot.Clients
                .Where(c => c.WatchWorldsDetected)
                .Select(c => new ClientKey(c.ProcessId, c.CreationTimeUtc))
                .ToHashSet();

            if (current.Count == 0)
            {
                stablePolls = 0;
                previous = null;
            }
            else
            {
                if (previous is not null && previous.SetEquals(current))
                {
                    stablePolls++;
                }
                else
                {
                    stablePolls = 1;
                    previous = current;
                }

                foreach (var key in current)
                {
                    firstObservedUtc.TryAdd(key, now);
                }

                // A creation time that could not be read falls back to when this wait
                // first observed the client.
                var settled = current.All(key =>
                    now - (key.CreationTimeUtc is { } created
                        ? new DateTimeOffset(created, TimeSpan.Zero)
                        : firstObservedUtc[key]) >= _startupSettleDelay);

                if (stablePolls >= RequiredStablePolls && settled)
                {
                    return VrchatPreparation.DeployOnly();
                }

                if (!settled && !reportedWait)
                {
                    _logger.LogInformation(
                        "Waiting for {Count} VRChat client(s) to finish starting before deploying " +
                        "(VrchatStartupSettleDelaySeconds = {SettleSeconds}).",
                        current.Count, _startupSettleDelay.TotalSeconds);
                    reportedWait = true;
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

        // The while loop's guard (!linkedCts.IsCancellationRequested) can already be
        // false on entry to a given iteration if the external cancellationToken fires
        // between two polls -- on a slow/loaded runner this can happen before the loop
        // ever reaches the Task.Delay that would normally rethrow it below. Without this
        // check, that race silently downgrades a real external cancellation into a
        // VrchatStartTimeout failure result instead of propagating OperationCanceledException
        // (observed as CI-only flakiness on a shared GitHub Actions runner; did not
        // reproduce locally where scheduling is fast enough to always hit Task.Delay first).
        cancellationToken.ThrowIfCancellationRequested();

        var message = $"Timed out after {startupTimeout.TotalSeconds}s waiting for VRChat to become ready.";
        _logger.LogError(message);
        return VrchatPreparation.Failure(ErrorCode.VrchatStartTimeout, message);
    }

    /// <summary>
    /// Logged when the mix of running clients changes rather than on every build, since
    /// a mismatch is a deliberate or tolerated state (e.g. one client closed by hand):
    /// the deploy still reaches every --watch-worlds client.
    /// </summary>
    private void ReportClientMix(VrchatClientSnapshot snapshot)
    {
        var watchWorlds = snapshot.Clients.Count(c => c.WatchWorldsDetected);
        var mix = new ClientMix(watchWorlds, snapshot.Clients.Count - watchWorlds, snapshot.UnverifiedCount);
        if (mix == _lastReportedMix)
        {
            return;
        }

        var previous = _lastReportedMix;
        _lastReportedMix = mix;
        var expected = _options.ResolvedVrchatClientCount;

        if (mix.IsExpected(expected))
        {
            if (previous is not null && !previous.IsExpected(expected))
            {
                _logger.LogInformation("{Count} VRChat client(s) with --watch-worlds are running, as configured.", expected);
            }

            return;
        }

        var details = new List<string>();
        if (mix.WatchWorlds < expected)
        {
            details.Add(
                $"deploying to {mix.WatchWorlds} of {expected} VRChat client(s); missing clients are not launched " +
                $"while others run (close every VRChat client to cold-start {expected} on the next build)");
        }
        else if (mix.WatchWorlds > expected)
        {
            details.Add(
                $"{mix.WatchWorlds} VRChat clients with --watch-worlds are running, more than " +
                $"VrchatClientCount ({expected}); all of them reload");
        }

        if (mix.WithoutWatchWorlds > 0)
        {
            details.Add($"{mix.WithoutWatchWorlds} VRChat client(s) without --watch-worlds will not reload");
        }

        if (mix.Unverified > 0)
        {
            details.Add($"{mix.Unverified} VRChat process(es) could not be checked and are ignored");
        }

        _logger.LogWarning("VRChat clients changed: {Details}.", string.Join("; ", details));
    }

    private VrchatPreparation Unmanaged(string detail)
    {
        var message =
            $"A VRChat process is running whose state could not be checked ({detail}). " +
            "Close it manually, then send the build again.";
        _logger.LogError(message);
        return VrchatPreparation.Failure(ErrorCode.VrchatUnmanagedRunning, message);
    }

    private readonly record struct ClientKey(int ProcessId, DateTime? CreationTimeUtc);

    private sealed record ClientMix(int WatchWorlds, int WithoutWatchWorlds, int Unverified)
    {
        public bool IsExpected(int expected) => WatchWorlds == expected && WithoutWatchWorlds == 0 && Unverified == 0;
    }
}

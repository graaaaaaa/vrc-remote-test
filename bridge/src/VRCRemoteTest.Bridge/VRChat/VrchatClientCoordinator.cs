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
/// cases 1 and 2), and closing the running ones belongs to the separate-account
/// relaunch flow (option C), which is not implemented.
/// </summary>
public sealed class VrchatClientCoordinator : IVrchatClientCoordinator
{
    private const int RequiredStablePolls = 2;

    /// <summary>
    /// The SDK's interval, kept as the shortest one. Clients whose logging starts within
    /// the same second write to one output_log file (spike option B), so each launch
    /// also waits for the previous client's file (<see cref="WaitBeforeNextLaunchAsync"/>).
    /// </summary>
    internal static readonly TimeSpan LaunchInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The most a cold start waits for output_log files in total, counted from just
    /// before the first launch. VRChat created its file 0.70-0.84 s after the process
    /// start without load, but 11-22 s after it with the CPU at about 100%
    /// (docs/validation/multi-client-spike.md, PR #2 verification, item 12). The same
    /// length as <see cref="VrchatLogMatcher.MaxRangeLength"/>: a first client's file that
    /// appears in time can still be matched. It also bounds how much the waiting delays
    /// the cold start's result.
    /// </summary>
    internal static readonly TimeSpan LogWaitBudget = VrchatLogMatcher.MaxRangeLength;

    internal static readonly TimeSpan LogPollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly BridgeOptions _options;
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly IVrchatLauncher _launcher;
    private readonly ILogger<VrchatClientCoordinator> _logger;
    private readonly TimeSpan _startupSettleDelay;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _launchDelay;
    private readonly Func<IReadOnlyList<VrchatLogMatcher.Candidate>?> _listLogFiles;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeZoneInfo _timeZone;

    /// <summary>
    /// Last client mix reported by <see cref="ReportClientMix"/>. Only StagingWatcher's
    /// sequential loop calls this service, so no synchronization is needed.
    /// </summary>
    private ClientMix? _lastReportedMix;

    public VrchatClientCoordinator(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatLauncher launcher,
        ILogger<VrchatClientCoordinator> logger)
        : this(
            options,
            processMonitor,
            launcher,
            logger,
            TimeSpan.FromSeconds(options.Value.VrchatStartupSettleDelaySeconds),
            DefaultPollInterval,
            Task.Delay,
            () => ListLogFiles(options.Value),
            () => DateTime.UtcNow,
            TimeZoneInfo.Local)
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
        TimeZoneInfo timeZone)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _launcher = launcher;
        _logger = logger;
        _startupSettleDelay = startupSettleDelay;
        _pollInterval = pollInterval;
        _launchDelay = launchDelay;
        _listLogFiles = listLogFiles;
        _utcNow = utcNow;
        _timeZone = timeZone;
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

        var logWatch = count > 1 ? StartLogWatch() : null;
        var previousLaunchUtc = DateTime.MinValue;

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                await WaitBeforeNextLaunchAsync(logWatch, i, count, previousLaunchUtc, cancellationToken)
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
                return VrchatLaunchOutcome.Failure(message);
            }

            _logger.LogInformation(
                "Launched VRChat client {Index}/{Count} (PID {ProcessId}, created {CreationTimeUtc:O}, roomId {RoomId}).",
                i + 1, count, result.ProcessId, result.CreationTimeUtc, roomId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return VrchatLaunchOutcome.Success();
    }

    /// <summary>
    /// Lists the files that already exist, so files created after the first launch can
    /// be told apart. Null (no waiting for files, only the fixed interval) when the
    /// directory cannot be listed.
    /// </summary>
    private LogWatch? StartLogWatch()
    {
        var deadlineUtc = _utcNow() + LogWaitBudget;
        var existing = _listLogFiles();
        if (existing is null)
        {
            _logger.LogWarning(
                "Could not list the VRChat log directory; launching VRChat clients {Interval}s apart without waiting " +
                "for their output_log files. The Log Viewer may not be able to tell their logs apart.",
                LaunchInterval.TotalSeconds);
            return null;
        }

        return new LogWatch(existing.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase), deadlineUtc);
    }

    /// <summary>
    /// Waits <see cref="LaunchInterval"/> after the previous launch, as before, and then
    /// until every client launched so far has created an output_log file that did not
    /// exist before the batch, and until the next client's start is late enough that the
    /// newest of those files stays out of its matching range
    /// (<see cref="VrchatLogMatcher.EarliestNextStartUtc"/>). Each client's logging then
    /// starts in a later second than the previous one's, so every client gets its own file
    /// and the first client's file can be matched (multi-client plan section 13.2).
    ///
    /// This only spaces the launches: it never decides which file is whose, so a file
    /// that appears for another reason (e.g. a VRChat started by hand meanwhile) can at
    /// worst end the wait early, and <see cref="VrchatLogMatcher"/> still refuses any file
    /// it cannot attribute. Without load the files exist by the end of the interval, so
    /// the clients start 3 s apart as before. The total wait is bounded by
    /// <see cref="LogWaitBudget"/>; past it, the remaining clients start 3 s apart.
    /// </summary>
    private async Task WaitBeforeNextLaunchAsync(
        LogWatch? watch, int launched, int count, DateTime previousLaunchUtc, CancellationToken cancellationToken)
    {
        await DelayUntilAsync(previousLaunchUtc + LaunchInterval, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (watch is null || watch.GaveUp)
        {
            return;
        }

        var reportedWait = false;
        while (true)
        {
            var newFiles = _listLogFiles()?.Where(c => !watch.Existing.Contains(c.Path)).ToList();
            if (newFiles is not null && newFiles.Count >= launched)
            {
                var newest = newFiles
                    .Select(c => (File: c, Time: VrchatLogMatcher.TrustedNameUtc(c, _timeZone) ?? c.CreationTimeUtc))
                    .MaxBy(c => c.Time);

                // Logged for every client, so the Bridge log records how long VRChat took.
                _logger.LogInformation(
                    "New output_log {File} (created {CreationTimeUtc:O}, {Seconds:F1}s after VRChat client " +
                    "{Index}/{Count} was launched); launching the next client.",
                    Path.GetFileName(newest.File.Path),
                    newest.File.CreationTimeUtc,
                    (newest.File.CreationTimeUtc - previousLaunchUtc).TotalSeconds,
                    launched,
                    count);

                // A name time from a wrong clock must not hold the batch past its deadline.
                var earliest = VrchatLogMatcher.EarliestNextStartUtc(newest.Time);
                await DelayUntilAsync(earliest < watch.DeadlineUtc ? earliest : watch.DeadlineUtc, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var now = _utcNow();
            if (now >= watch.DeadlineUtc)
            {
                watch.GaveUp = true;
                _logger.LogWarning(
                    "VRChat client {Index}/{Count} has not created an output_log file within {Budget}s of the first " +
                    "launch; launching the remaining clients {Interval}s apart. Their logs may end up in one shared " +
                    "file, which the Log Viewer does not show as the first client's log.",
                    launched, count, LogWaitBudget.TotalSeconds, LaunchInterval.TotalSeconds);
                return;
            }

            if (!reportedWait)
            {
                _logger.LogInformation(
                    "Waiting for VRChat client {Index}/{Count} to create its output_log file before launching the next " +
                    "client (up to {Remaining:F0}s more), so that their logs stay apart.",
                    launched, count, (watch.DeadlineUtc - now).TotalSeconds);
                reportedWait = true;
            }

            var remaining = watch.DeadlineUtc - now;
            await _launchDelay(remaining < LogPollInterval ? remaining : LogPollInterval, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task DelayUntilAsync(DateTime utc, CancellationToken cancellationToken)
    {
        var wait = utc - _utcNow();
        if (wait > TimeSpan.Zero)
        {
            await _launchDelay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Null when the directory cannot be listed or holds too many files to list. A
    /// directory that does not exist yet is empty: VRChat creates it on its first start.
    /// </summary>
    private static IReadOnlyList<VrchatLogMatcher.Candidate>? ListLogFiles(BridgeOptions options)
    {
        var directory = VrchatLogDirectory.Resolve(options);
        if (directory is null)
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var (candidates, scanCapped) = VrchatLogDirectory.ListCandidates(directory, File.GetCreationTimeUtc);
            return scanCapped ? null : candidates;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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

    /// <summary>One launch batch's files from before its first launch, and its deadline for waiting on new ones.</summary>
    private sealed class LogWatch(HashSet<string> existing, DateTime deadlineUtc)
    {
        public HashSet<string> Existing { get; } = existing;

        public DateTime DeadlineUtc { get; } = deadlineUtc;

        /// <summary>Set once the deadline passes; the rest of the batch then uses the fixed interval.</summary>
        public bool GaveUp { get; set; }
    }

    private sealed record ClientMix(int WatchWorlds, int WithoutWatchWorlds, int Unverified)
    {
        public bool IsExpected(int expected) => WatchWorlds == expected && WithoutWatchWorlds == 0 && Unverified == 0;
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Relaunch mode (multi-client plan option C: section 12 as amended by sections 13.1,
/// 13.3 and 13.5). Clients on different accounts split into separate instances when
/// --watch-worlds reloads them (a VRChat bug, spike case 5), but all enter one instance
/// when they are closed and started again with --url (spike case 4, gate 2). So every
/// deploy closes the clients the Bridge launched, installs the build and launches one
/// client per VrchatProfiles entry with --url and --profile, without --watch-worlds.
///
/// StagingWatcher runs the steps around this class (validation, install, archive,
/// result, cleanup); <see cref="PrepareAsync"/> is steps 2-4 of section 13.3 and
/// <see cref="LaunchClientsAsync"/> is step 7:
///
/// 2. Enumerate the session's VRChat, keep a handle to each, and match each against
///    the record. A VRChat the Bridge did not launch, or one it cannot check, stops
///    the build with VRCHAT_UNMANAGED_RUNNING before anything is closed (spec section 32).
/// 3. Close the matched clients through the same handles (<see cref="VrchatClientTerminator"/>).
///    A client whose exit cannot be confirmed stops the build before the install.
/// 4. Enumerate again; a VRChat that appeared meanwhile stops the build.
/// 7. Launch in profile order, recording each client right after it starts, and check
///    before every later launch that no other VRChat has appeared. That check only
///    detects such a VRChat: nothing can keep a VRChat from being started from outside.
///
/// A client is the Bridge's only when its PID, its creation time (compared at full
/// precision), its session and its name all match the record (<see cref="IsRecorded"/>);
/// a name or a plausible log file never makes it one. A record that is corrupt counts as
/// empty, so running VRChat then stops the build; a record that cannot be read stops it
/// with UNKNOWN_ERROR.
///
/// Once nothing but the Bridge's own clients is running and the record lists any, until the
/// first new client is recorded, the Log Viewer shows <see cref="RelaunchNotice"/> instead of
/// a log (<see cref="VrchatLogNotice"/>): the closing clients' logs, and with none running the
/// newest file, are the previous session's. That holds when the recorded clients have already
/// exited too, as when the Bridge stopped while closing them and recovers the build after its
/// restart (Windows verification of PR #19, item 4: the closed client's log stayed 5.2 s
/// after the new first client started). A relaunch that stops before that point clears the
/// line here, except after a cold start that StagingWatcher does not go on to launch (the
/// install fails), where StagingWatcher clears it.
/// </summary>
public sealed class VrchatRelaunchCoordinator : IVrchatClientCoordinator
{
    private const string VrchatProcessName = "VRChat";

    internal const string RelaunchNotice =
        "The Bridge is relaunching its VRChat clients with the new build (VrchatClientMode: Relaunch): " +
        "they are closed, the build is installed, and they are started again. " +
        "The first client's log is shown once it has started.";

    private readonly BridgeOptions _options;
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly IVrchatLauncher _launcher;
    private readonly IManagedClientStore _store;
    private readonly VrchatLogNotice _logNotice;
    private readonly ILogger<VrchatRelaunchCoordinator> _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly VrchatClientTerminator _terminator;
    private readonly VrchatLaunchPacer _pacer;

    public VrchatRelaunchCoordinator(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatLauncher launcher,
        IManagedClientStore store,
        VrchatLogNotice logNotice,
        ILogger<VrchatRelaunchCoordinator> logger)
        : this(
            options,
            processMonitor,
            launcher,
            store,
            logNotice,
            logger,
            Task.Delay,
            () => VrchatLaunchPacer.ListLogFiles(options.Value),
            () => DateTime.UtcNow,
            TimeZoneInfo.Local)
    {
    }

    /// <summary>
    /// Test-only seam: closing and launching read the time only through
    /// <paramref name="utcNow"/> and wait only through <paramref name="delay"/>.
    /// </summary>
    internal VrchatRelaunchCoordinator(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IVrchatLauncher launcher,
        IManagedClientStore store,
        VrchatLogNotice logNotice,
        ILogger<VrchatRelaunchCoordinator> logger,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<IReadOnlyList<VrchatLogMatcher.Candidate>?> listLogFiles,
        Func<DateTime> utcNow,
        TimeZoneInfo timeZone)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _launcher = launcher;
        _store = store;
        _logNotice = logNotice;
        _logger = logger;
        _utcNow = utcNow;
        _terminator = new VrchatClientTerminator(logger, utcNow, delay);
        _pacer = new VrchatLaunchPacer(logger, delay, listLogFiles, utcNow, timeZone);
    }

    /// <summary>
    /// Ends with ColdStart only once every client the Bridge launched has exited, the
    /// record says so on disk, and no other VRChat is running. A stop request while
    /// closing kills nothing more and deploys nothing: the claim stays in processing/ and
    /// the Bridge starts again from step 1 after its restart (multi-client plan section 13.3).
    /// Anything but a cold start ends the relaunch, so the Log Viewer shows what runs again.
    /// </summary>
    public async Task<VrchatPreparation> PrepareAsync(CancellationToken cancellationToken)
    {
        var coldStart = false;
        try
        {
            var preparation = await PrepareCoreAsync(cancellationToken).ConfigureAwait(false);
            coldStart = preparation.Kind == VrchatPreparationKind.ColdStart;
            return preparation;
        }
        finally
        {
            if (!coldStart)
            {
                _logNotice.Clear();
            }
        }
    }

    private async Task<VrchatPreparation> PrepareCoreAsync(CancellationToken cancellationToken)
    {
        var load = _store.Load();
        if (load.Status == ManagedClientLoadStatus.Unreadable)
        {
            var message =
                $"The Bridge's record of the VRChat clients it launched ({_store.FilePath}) could not be read: " +
                $"{load.Problem}. Nothing was closed or deployed. Fix the file's permissions, or close every VRChat " +
                "and delete the file, then send the build again.";
            _logger.LogError(message);
            return VrchatPreparation.Failure(ErrorCode.UnknownError, message);
        }

        if (load.Status == ManagedClientLoadStatus.Corrupt)
        {
            _logger.LogWarning(
                "Ignoring {Path}: {Problem}. No running VRChat counts as launched by the Bridge.",
                _store.FilePath, load.Problem);
        }

        var records = load.Clients;
        using (var snapshot = _processMonitor.OpenClients())
        {
            if (Unverifiable(snapshot) is { } problem)
            {
                return Unmanaged(problem);
            }

            var managed = snapshot.Handles.Where(h => records.Any(r => IsRecorded(h, r))).ToList();
            var others = snapshot.Handles.Except(managed).ToList();
            if (others.Count > 0)
            {
                var corruptHint = load.Status == ManagedClientLoadStatus.Corrupt
                    ? $"; the Bridge's record {_store.FilePath} was corrupt and ignored"
                    : string.Empty;
                return Unmanaged(
                    $"{others.Count} VRChat client(s) that the Bridge did not launch (PID {ProcessIds(others)}){corruptHint}");
            }

            // Shown only past the check above, so a build that stops for another VRChat never
            // flashes it.
            if (records.Count > 0)
            {
                _logNotice.Show(RelaunchNotice);
            }

            if (managed.Count > 0)
            {
                var closed = await _terminator.CloseAsync(managed, cancellationToken).ConfigureAwait(false);
                if (closed.NotConfirmed.Count > 0)
                {
                    // Only a client that may still be running keeps its entry; every other
                    // entry names a process that is gone.
                    TrySave(records.Where(r => closed.NotConfirmed.Any(h => IsRecorded(h, r))).ToList());
                    cancellationToken.ThrowIfCancellationRequested();

                    var message =
                        $"Could not close {closed.NotConfirmed.Count} of {managed.Count} VRChat client(s) launched by the " +
                        $"Bridge (PID {ProcessIds(closed.NotConfirmed)}) within {VrchatClientTerminator.ExitDeadline.TotalSeconds}s, " +
                        "so the build was not deployed. Close them manually, then send the build again.";
                    _logger.LogError(message);
                    return VrchatPreparation.Failure(ErrorCode.VrchatStartFailed, message);
                }
            }
        }

        // No client of the record is running any more. Writing that down before deploying
        // also proves the record can be written, so a client launched next can be recorded.
        if (!TrySave([]))
        {
            var message =
                $"The Bridge could not update its record of the VRChat clients it launched ({_store.FilePath}), " +
                "so the build was not deployed and nothing was launched. Fix the file's permissions, then send the build again.";
            _logger.LogError(message);
            return VrchatPreparation.Failure(ErrorCode.UnknownError, message);
        }

        cancellationToken.ThrowIfCancellationRequested();

        using (var after = _processMonitor.OpenClients())
        {
            if (Unverifiable(after) is { } problem)
            {
                return Unmanaged(problem, ownClientsClosed: true);
            }

            if (after.Handles.Count > 0)
            {
                return Unmanaged(
                    $"{after.Handles.Count} VRChat client(s) that started while the Bridge was closing its own " +
                    $"(PID {ProcessIds(after.Handles)})",
                    ownClientsClosed: true);
            }
        }

        return VrchatPreparation.ColdStart();
    }

    /// <summary>
    /// One room id for the whole batch, as in Reload mode, and the same spacing
    /// (<see cref="VrchatLaunchPacer"/>). Every client is recorded before anything else
    /// happens, so a Bridge that stops between two launches still closes the clients it
    /// started on the next deploy; a client that started but could not be recorded stops
    /// the batch (it then blocks the next deploy as a VRChat the Bridge did not launch,
    /// until it is closed by hand). Stop requests are honoured as in Reload mode. The Log
    /// Viewer's notice ends once the first client is recorded, or when the batch stops before.
    /// </summary>
    public async Task<VrchatLaunchOutcome> LaunchClientsAsync(
        string deployedWorldPath, CancellationToken cancellationToken)
    {
        try
        {
            return await LaunchClientsCoreAsync(deployedWorldPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _logNotice.Clear();
        }
    }

    private async Task<VrchatLaunchOutcome> LaunchClientsCoreAsync(
        string deployedWorldPath, CancellationToken cancellationToken)
    {
        var profiles = _options.VrchatProfiles
            ?? throw new InvalidOperationException("VrchatProfiles is required in Relaunch mode.");
        var count = profiles.Length;
        var roomId = VrchatLaunchRequest.NewRoomId();
        var vrMode = string.Equals(_options.VrchatMode, "VR", StringComparison.OrdinalIgnoreCase);

        // PrepareAsync left the record empty: every client it listed has exited.
        var launched = new List<ManagedClient>();
        var batch = _pacer.StartBatch(count);
        var previousLaunchUtc = DateTime.MinValue;

        for (var i = 0; i < count; i++)
        {
            var profile = profiles[i];
            if (i > 0)
            {
                await _pacer.WaitBeforeNextLaunchAsync(batch, i, previousLaunchUtc, cancellationToken)
                    .ConfigureAwait(false);

                if (FindOtherVrchat(launched) is { } problem)
                {
                    var message =
                        $"Launched {i} of {count} VRChat client(s), then stopped: {problem}. Nothing was closed; " +
                        "close that VRChat manually, then send the build again.";
                    _logger.LogError(message);
                    return VrchatLaunchOutcome.Failure(message, ErrorCode.VrchatUnmanagedRunning);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Only one client can use the HMD, so later ones always run on the desktop.
            var request = new VrchatLaunchRequest(
                _options.VrchatExecutable,
                deployedWorldPath,
                roomId,
                NoVr: !(vrMode && i == 0),
                WatchWorlds: false,
                Profile: profile);

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
                    $"Launched {i} of {count} VRChat client(s); client {i + 1} (profile {profile}) failed to start: " +
                    result.ErrorMessage;
                _logger.LogError(message);
                return VrchatLaunchOutcome.Failure(message);
            }

            if (result.ProcessId is not { } processId
                || result.CreationTimeUtc is not { } creationTimeUtc
                || result.SessionId is not { } sessionId)
            {
                var message =
                    $"Launched {i + 1} of {count} VRChat client(s), then stopped: client {i + 1} (profile {profile}, " +
                    $"PID {result.ProcessId}) started, but its creation time or session could not be read, so the " +
                    "Bridge cannot recognize it as its own. If it is still running, close it manually before the next build.";
                _logger.LogError(message);
                return VrchatLaunchOutcome.Failure(message);
            }

            launched.Add(new ManagedClient(processId, creationTimeUtc, sessionId, profile, i));
            if (!TrySave(launched))
            {
                var message =
                    $"Launched {i + 1} of {count} VRChat client(s), then stopped: client {i + 1} (PID {processId}) could " +
                    $"not be recorded in {_store.FilePath}. Close every VRChat manually before the next build; the " +
                    "Bridge does not close a client it has no record of.";
                _logger.LogError(message);
                return VrchatLaunchOutcome.Failure(message, ErrorCode.UnknownError);
            }

            _logger.LogInformation(
                "Launched VRChat client {Index}/{Count} (profile {Profile}, PID {ProcessId}, created {CreationTimeUtc:O}, " +
                "roomId {RoomId}).",
                i + 1, count, profile, processId, creationTimeUtc, roomId);

            // The Log Viewer finds the first client through the record (VrchatLogReader), so
            // from here on it shows that client's state instead of the previous session's log.
            if (i == 0)
            {
                _logNotice.Clear();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return VrchatLaunchOutcome.Success();
    }

    /// <summary>
    /// The four-part match of multi-client plan section 12.3. The creation time is
    /// compared tick for tick: both values come from the process's FILETIME, so the
    /// Bridge's own client matches exactly and a reused PID never does.
    /// </summary>
    internal static bool IsRecorded(IVrchatProcessHandle process, ManagedClient record) =>
        process.ProcessId == record.ProcessId
        && process.CreationTimeUtc.Ticks == record.CreationTimeUtc.Ticks
        && process.SessionId == record.SessionId
        && string.Equals(process.ProcessName, VrchatProcessName, StringComparison.OrdinalIgnoreCase);

    private string? FindOtherVrchat(IReadOnlyList<ManagedClient> launched)
    {
        using var snapshot = _processMonitor.OpenClients();
        if (Unverifiable(snapshot) is { } problem)
        {
            return problem;
        }

        var others = snapshot.Handles.Where(h => !launched.Any(r => IsRecorded(h, r))).ToList();
        return others.Count == 0
            ? null
            : $"{others.Count} VRChat client(s) that the Bridge did not launch appeared (PID {ProcessIds(others)})";
    }

    private static string? Unverifiable(VrchatProcessHandleSnapshot snapshot)
    {
        if (snapshot.EnumerationError is { } error)
        {
            return $"the VRChat process list could not be read: {error}";
        }

        return snapshot.UnverifiedCount > 0
            ? $"{snapshot.UnverifiedCount} VRChat process(es) could not be checked"
            : null;
    }

    private bool TrySave(IReadOnlyList<ManagedClient> clients)
    {
        try
        {
            _store.Save(clients);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write {Path}.", _store.FilePath);
            return false;
        }
    }

    private VrchatPreparation Unmanaged(string detail, bool ownClientsClosed = false)
    {
        var outcome = ownClientsClosed
            ? "The Bridge's own clients were closed, but the build was not deployed."
            : "Nothing was closed or deployed.";
        var message =
            $"VRChat is running that the Bridge did not launch or could not check ({detail}). {outcome} " +
            "Close it manually, then send the build again.";
        _logger.LogError(message);
        return VrchatPreparation.Failure(ErrorCode.VrchatUnmanagedRunning, message);
    }

    private static string ProcessIds(IEnumerable<IVrchatProcessHandle> processes) =>
        string.Join(", ", processes.Select(p => p.ProcessId));
}

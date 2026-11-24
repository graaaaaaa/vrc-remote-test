using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>Why a single-client operation did nothing (or, for <see cref="LaunchFailed"/>, started nothing).</summary>
public enum SlotRefusal
{
    UnknownSlot,
    NotRelaunchMode,
    RecordUnreadable,
    RecordCorrupt,
    NotLaunchedByBridge,
    CannotCheck,
    NotRunning,
    SlotMismatch,
    NoLaunchPlan,
    WorldMissing,
    AlreadyRunning,
    OthersRunning,
    RecordUnwritable,
    LaunchFailed,
}

/// <param name="Refusal">Set when nothing was closed; the rest is then empty.</param>
/// <param name="RecordUpdated">The closed client's entry was removed from the record.</param>
public sealed record SlotCloseResult(
    SlotRefusal? Refusal, int Slot, int? ProcessId, int Closed, int NotConfirmed, bool RecordUpdated,
    IReadOnlyList<string> Problems)
{
    public bool Done => Refusal is null && Closed == 1 && RecordUpdated;
}

/// <param name="Refusal">Set when nothing was started.</param>
/// <param name="NotRecorded">A client started but could not be recorded: the Bridge cannot recognize it as its own.</param>
public sealed record SlotLaunchResult(
    SlotRefusal? Refusal, int Slot, int? Profile, int? ProcessId, long? ClientGeneration, bool NotRecorded,
    IReadOnlyList<string> Problems)
{
    public bool Done => Refusal is null && !NotRecorded;
}

/// <summary>
/// Closes one VRChat client the Bridge launched, or starts it again, for the LAN API
/// (POST /api/v1/clients/{slot}/close and /launch), so a test can have one client leave and
/// rejoin while the others stay. Relaunch mode only; the slot is the client's launch order,
/// which is also the observation slot the roster gives a recorded client.
///
/// Closing: the client is the Bridge's only on the four-part match of
/// <see cref="VrchatRelaunchCoordinator.IsRecorded"/> against the record entry of that launch
/// order, and is closed through that same handle (<see cref="VrchatClientTerminator"/>), held
/// until its exit is confirmed. No other client or record entry is touched. When the
/// observation shows another client in that slot (e.g. right after a launch, before the next
/// poll), nothing is closed, so what `vrcrt.sh status` shows is what gets closed.
///
/// Launching: only with the arguments the Bridge itself used for that slot in the current
/// batch (<see cref="RelaunchLaunchContext"/>, in memory, so nothing survives a host restart:
/// deploy again then), and only while no VRChat the Bridge did not launch runs and the record
/// can be written. The launch is spaced from the previous one like a batch's
/// (<see cref="VrchatLaunchPacer"/>), the process list is checked again right before
/// Process.Start, and a stop request is honoured until then; once a client has started it is
/// recorded whatever happens to the request. Nothing from the request but the slot number
/// reaches the launch.
///
/// Runs inside the operation gate (the LAN API takes it), so no deploy, close or other launch
/// uses the record meanwhile. Every outcome is logged with the slot and the client.
/// </summary>
public sealed class ClientSlotControl
{
    private readonly BridgeOptions _options;
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly IManagedClientStore _store;
    private readonly IVrchatLauncher _launcher;
    private readonly RelaunchLaunchContext _context;
    private readonly VrchatClientRoster _roster;
    private readonly ILogger<ClientSlotControl> _logger;
    private readonly VrchatClientTerminator _terminator;
    private readonly VrchatLaunchPacer _pacer;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<string, bool> _fileExists;

    public ClientSlotControl(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IManagedClientStore store,
        IVrchatLauncher launcher,
        RelaunchLaunchContext context,
        VrchatClientRoster roster,
        ILogger<ClientSlotControl> logger)
        : this(options, processMonitor, store, launcher, context, roster, logger,
            Task.Delay, () => VrchatLaunchPacer.ListLogFiles(options.Value), () => DateTime.UtcNow, File.Exists)
    {
    }

    /// <summary>Test seam: time, waits, the log directory and the world file only through these.</summary>
    internal ClientSlotControl(
        IOptions<BridgeOptions> options,
        IVrchatProcessMonitor processMonitor,
        IManagedClientStore store,
        IVrchatLauncher launcher,
        RelaunchLaunchContext context,
        VrchatClientRoster roster,
        ILogger<ClientSlotControl> logger,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<IReadOnlyList<VrchatLogMatcher.Candidate>?> listLogFiles,
        Func<DateTime> utcNow,
        Func<string, bool> fileExists)
    {
        _options = options.Value;
        _processMonitor = processMonitor;
        _store = store;
        _launcher = launcher;
        _context = context;
        _roster = roster;
        _logger = logger;
        _utcNow = utcNow;
        _fileExists = fileExists;
        _terminator = new VrchatClientTerminator(logger, utcNow, delay);
        _pacer = new VrchatLaunchPacer(logger, delay, listLogFiles, utcNow, TimeZoneInfo.Local);
    }

    public async Task<SlotCloseResult> CloseAsync(int slot, CancellationToken cancellationToken)
    {
        if (CheckSlotAndMode(slot) is { } refusal)
        {
            return RefuseClose(slot, refusal.Reason, refusal.Problem);
        }

        var load = _store.Load();
        if (Unusable(load) is { } recordProblem)
        {
            return RefuseClose(slot, recordProblem.Reason, recordProblem.Problem);
        }

        var record = load.Clients.FirstOrDefault(c => c.LaunchOrder == slot);
        if (record is null)
        {
            return RefuseClose(slot, SlotRefusal.NotLaunchedByBridge,
                $"The Bridge's record lists no VRChat client it launched in slot {slot}.");
        }

        // The handle stays open (and so its PID taken) until the exit is confirmed.
        using var snapshot = _processMonitor.OpenClients();
        if (Unverifiable(snapshot) is { } unverifiable)
        {
            return RefuseClose(slot, SlotRefusal.CannotCheck, unverifiable);
        }

        var handle = snapshot.Handles.FirstOrDefault(h => VrchatRelaunchCoordinator.IsRecorded(h, record));
        if (handle is null)
        {
            return RefuseClose(slot, SlotRefusal.NotRunning,
                $"The VRChat client the Bridge launched in slot {slot} (PID {record.ProcessId}) is not running.");
        }

        if (_roster.EntryAt(slot) is { } observed
            && (observed.ProcessId != handle.ProcessId || observed.CreationTimeUtc.Ticks != handle.CreationTimeUtc.Ticks))
        {
            return RefuseClose(slot, SlotRefusal.SlotMismatch,
                $"Slot {slot} is observed as PID {observed.ProcessId}, not the client the Bridge launched there " +
                $"(PID {handle.ProcessId}); try again once `vrcrt.sh status` shows PID {handle.ProcessId} in slot {slot}.");
        }

        var result = await _terminator.CloseAsync([handle], cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        var exited = result.NotConfirmed.Count == 0;
        var recordUpdated = false;
        if (exited)
        {
            recordUpdated = TrySave(load.Clients.Where(c => c != record).ToList());
            if (!recordUpdated)
            {
                problems.Add("The client exited, but the Bridge could not update its record of the clients it launched.");
            }
        }
        else
        {
            problems.Add(result.Canceled
                ? $"The request ended before the client (PID {handle.ProcessId}) was confirmed to exit; its record was " +
                  "kept. Check `vrcrt.sh status`."
                : $"Could not confirm that the client (PID {handle.ProcessId}) exited within " +
                  $"{VrchatClientTerminator.ExitDeadline.TotalSeconds:0} s; its record was kept. Check `vrcrt.sh status`.");
        }

        _logger.LogInformation(
            "Single-client close of slot {Slot} (PID {ProcessId}, created {CreationTimeUtc:O}, profile {Profile}): " +
            "exited {Exited}, record updated {RecordUpdated}.",
            slot, handle.ProcessId, handle.CreationTimeUtc, record.Profile, exited, recordUpdated);

        return new SlotCloseResult(
            null, slot, handle.ProcessId, exited ? 1 : 0, exited ? 0 : 1, recordUpdated, problems);
    }

    public async Task<SlotLaunchResult> LaunchAsync(int slot, CancellationToken cancellationToken)
    {
        if (CheckSlotAndMode(slot) is { } refusal)
        {
            return RefuseLaunch(slot, refusal.Reason, refusal.Problem);
        }

        if (_context.PlanFor(slot) is not { } plan)
        {
            return RefuseLaunch(slot, SlotRefusal.NoLaunchPlan,
                $"The Bridge has not launched a client in slot {slot} since this host started; deploy a build first " +
                "(a configuration change or a restart of the Bridge forgets the last launch).");
        }

        if (!_fileExists(plan.WorldPath))
        {
            return RefuseLaunch(slot, SlotRefusal.WorldMissing,
                $"The world the clients were launched with ({plan.WorldPath[(plan.WorldPath.LastIndexOfAny(['\\', '/']) + 1)..]}) " +
                "no longer exists; deploy again.");
        }

        var load = _store.Load();
        if (Unusable(load) is { } recordProblem)
        {
            return RefuseLaunch(slot, recordProblem.Reason, recordProblem.Problem);
        }

        if (CheckRunning(slot, load.Clients) is { } running)
        {
            return RefuseLaunch(slot, running.Reason, running.Problem);
        }

        // Writing the record as it is proves it can be written, so the client launched next can be
        // recorded, without changing which clients it lists: a refusal below leaves it as it was.
        // The slot's old entry (a client that has exited) is replaced only once the new one starts.
        var kept = load.Clients.Where(c => c.LaunchOrder != slot).ToList();
        if (!TrySave(load.Clients))
        {
            return RefuseLaunch(slot, SlotRefusal.RecordUnwritable,
                "The Bridge could not write its record of the clients it launched, so nothing was started.");
        }

        await _pacer.WaitBeforeSingleLaunchAsync(_context.LastLaunch, cancellationToken).ConfigureAwait(false);

        // Again after the wait: a VRChat started meanwhile stops the launch. No await from here to Process.Start.
        if (CheckRunning(slot, load.Clients) is { } changed)
        {
            return RefuseLaunch(slot, changed.Reason, changed.Problem);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var request = new VrchatLaunchRequest(
            _options.VrchatExecutable, plan.WorldPath, plan.RoomId, plan.NoVr, WatchWorlds: false, Profile: plan.Profile);

        // From here the request's cancellation is not looked at: a started client is recorded.
        using var launching = _context.BeginLaunch();
        var filesBefore = _pacer.ListPaths();
        var startedUtc = _utcNow();
        LaunchResult result;
        try
        {
            var task = _launcher.LaunchAsync(request);
            result = await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = LaunchResult.Failure(ex.Message);
        }

        if (!result.Succeeded)
        {
            return RefuseLaunch(slot, SlotRefusal.LaunchFailed,
                $"VRChat could not be started in slot {slot} (profile {plan.Profile}): {result.ErrorMessage}");
        }

        _context.RecordLaunch(startedUtc, filesBefore);
        if (result.ProcessId is { } pid && result.CreationTimeUtc is { } created)
        {
            _context.MarkSingleLaunch(pid, created);
        }

        if (result.ProcessId is not { } processId
            || result.CreationTimeUtc is not { } creationTimeUtc
            || result.SessionId is not { } sessionId)
        {
            return NotRecorded(slot, plan.Profile, result.ProcessId,
                $"VRChat started in slot {slot} (PID {result.ProcessId}), but its creation time or session could not be " +
                "read, so the Bridge cannot recognize it as its own. If it is still running, close it on Windows.");
        }

        if (!TrySave([.. kept, new ManagedClient(processId, creationTimeUtc, sessionId, plan.Profile, slot)]))
        {
            return NotRecorded(slot, plan.Profile, processId,
                $"VRChat started in slot {slot} (PID {processId}), but the Bridge could not record it, so it cannot " +
                "recognize it as its own. Close it on Windows.");
        }

        launching.Dispose();
        var generation = VrchatClientRoster.GenerationOf(processId, creationTimeUtc);
        _logger.LogInformation(
            "Single-client launch of slot {Slot} (profile {Profile}, PID {ProcessId}, created {CreationTimeUtc:O}, " +
            "generation {Generation}, roomId {RoomId}).",
            slot, plan.Profile, processId, creationTimeUtc, generation, plan.RoomId);
        return new SlotLaunchResult(null, slot, plan.Profile, processId, generation, false, []);
    }

    private (SlotRefusal Reason, string Problem)? CheckSlotAndMode(int slot)
    {
        if (slot is < 0 or >= BridgeOptions.MaxVrchatClientCount)
        {
            return (SlotRefusal.UnknownSlot, $"A client slot is 0 to {BridgeOptions.MaxVrchatClientCount - 1}.");
        }

        return _options.IsRelaunchMode
            ? null
            : (SlotRefusal.NotRelaunchMode,
                "Closing or launching one client needs VrchatClientMode Relaunch: Reload clients share one account " +
                "and a client started on its own would enter another instance.");
    }

    private (SlotRefusal Reason, string Problem)? Unusable(ManagedClientLoadResult load) => load.Status switch
    {
        ManagedClientLoadStatus.Unreadable => (SlotRefusal.RecordUnreadable,
            $"The Bridge's record of the clients it launched could not be read: {load.Problem}."),
        ManagedClientLoadStatus.Corrupt => (SlotRefusal.RecordCorrupt,
            $"The Bridge's record of the clients it launched is not valid ({load.Problem}); it was left as it is. " +
            "Deploy a build (with every VRChat closed) to start a new one."),
        _ => null,
    };

    /// <summary>Why the slot cannot be launched now: the process list, the slot's own client, or a VRChat the Bridge did not launch.</summary>
    private (SlotRefusal Reason, string Problem)? CheckRunning(int slot, IReadOnlyList<ManagedClient> records)
    {
        using var snapshot = _processMonitor.OpenClients();
        if (Unverifiable(snapshot) is { } problem)
        {
            return (SlotRefusal.CannotCheck, problem);
        }

        var own = records.FirstOrDefault(r => r.LaunchOrder == slot);
        if (own is not null && snapshot.Handles.FirstOrDefault(h => VrchatRelaunchCoordinator.IsRecorded(h, own)) is { } running)
        {
            return (SlotRefusal.AlreadyRunning,
                $"The VRChat client the Bridge launched in slot {slot} is still running (PID {running.ProcessId}).");
        }

        var others = snapshot.Handles.Where(h => !records.Any(r => VrchatRelaunchCoordinator.IsRecorded(h, r))).ToList();
        return others.Count == 0
            ? null
            : (SlotRefusal.OthersRunning,
                $"{others.Count} VRChat client(s) that the Bridge did not launch are running " +
                $"(PID {string.Join(", ", others.Select(h => h.ProcessId))}); close them on Windows first.");
    }

    private static string? Unverifiable(VrchatProcessHandleSnapshot snapshot)
    {
        if (snapshot.EnumerationError is { } error)
        {
            return $"The VRChat process list could not be read: {error}.";
        }

        return snapshot.UnverifiedCount > 0
            ? $"{snapshot.UnverifiedCount} VRChat process(es) could not be checked."
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

    private SlotCloseResult RefuseClose(int slot, SlotRefusal reason, string problem)
    {
        _logger.LogWarning("Single-client close of slot {Slot} refused ({Reason}): {Problem}", slot, reason, problem);
        return new SlotCloseResult(reason, slot, null, 0, 0, false, [problem]);
    }

    private SlotLaunchResult RefuseLaunch(int slot, SlotRefusal reason, string problem)
    {
        _logger.LogWarning("Single-client launch of slot {Slot} refused ({Reason}): {Problem}", slot, reason, problem);
        return new SlotLaunchResult(reason, slot, null, null, null, false, [problem]);
    }

    private SlotLaunchResult NotRecorded(int slot, int profile, int? processId, string problem)
    {
        _logger.LogError("Single-client launch of slot {Slot}: {Problem}", slot, problem);
        return new SlotLaunchResult(null, slot, profile, processId, null, true, [problem]);
    }
}

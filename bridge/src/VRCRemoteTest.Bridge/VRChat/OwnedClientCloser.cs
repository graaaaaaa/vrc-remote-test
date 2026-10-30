using Microsoft.Extensions.Logging;

namespace VRCRemoteTest.Bridge.VRChat;

/// <param name="Refused">Nothing was closed (the reason is <paramref name="Problem"/>).</param>
/// <param name="Closed">The Bridge's clients whose exit was confirmed.</param>
/// <param name="NotConfirmed">The Bridge's clients that may still be running.</param>
/// <param name="Others">Running VRChat the Bridge has no record of; never closed.</param>
/// <param name="RecordNotUpdated">A record could not be written after closing, so it may still list a closed client.</param>
public sealed record CloseReport(
    bool Refused, int Closed, int NotConfirmed, int Others, string? Problem, bool RecordNotUpdated = false);

/// <summary>
/// Closes the VRChat clients the Bridge launched, in either mode, for the LAN API: the
/// Relaunch record (managed-clients.json) and the Reload record (reload-clients.json). A
/// running VRChat counts as the Bridge's only on the four-part match of
/// <see cref="VrchatRelaunchCoordinator.IsRecorded"/>, and is closed through the same handle
/// by <see cref="VrchatClientTerminator"/>; any other VRChat is never closed (spec section 32).
/// A record that cannot be read, or a process list that cannot be read or checked, closes
/// nothing. With <c>requireNoOthers</c> (a change of VrchatClientMode) any VRChat without a
/// record closes nothing either, so the change is made only when the Bridge can close every
/// client itself. Runs inside the operation gate, so no deploy uses the records meanwhile.
/// </summary>
public sealed class OwnedClientCloser
{
    private readonly IVrchatProcessMonitor _processMonitor;
    private readonly IManagedClientStore _relaunchRecord;
    private readonly IManagedClientStore _reloadRecord;
    private readonly ILogger<OwnedClientCloser> _logger;
    private readonly VrchatClientTerminator _terminator;

    public OwnedClientCloser(
        IVrchatProcessMonitor processMonitor,
        IManagedClientStore relaunchRecord,
        ReloadClientStore reloadRecord,
        ILogger<OwnedClientCloser> logger)
        : this(processMonitor, relaunchRecord, reloadRecord.Store, logger, () => DateTime.UtcNow, Task.Delay)
    {
    }

    internal OwnedClientCloser(
        IVrchatProcessMonitor processMonitor,
        IManagedClientStore relaunchRecord,
        IManagedClientStore reloadRecord,
        ILogger<OwnedClientCloser> logger,
        Func<DateTime> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _processMonitor = processMonitor;
        _relaunchRecord = relaunchRecord;
        _reloadRecord = reloadRecord;
        _logger = logger;
        _terminator = new VrchatClientTerminator(logger, utcNow, delay);
    }

    public async Task<CloseReport> CloseAsync(bool requireNoOthers, CancellationToken cancellationToken)
    {
        var relaunch = _relaunchRecord.Load();
        var reload = _reloadRecord.Load();
        foreach (var (store, load) in new[] { (_relaunchRecord, relaunch), (_reloadRecord, reload) })
        {
            if (load.Status == ManagedClientLoadStatus.Unreadable)
            {
                return Refuse($"the Bridge's record {Path.GetFileName(store.FilePath)} could not be read: {load.Problem}");
            }
        }

        using var snapshot = _processMonitor.OpenClients();
        if (snapshot.EnumerationError is { } error)
        {
            return Refuse($"the VRChat process list could not be read: {error}");
        }

        if (snapshot.UnverifiedCount > 0)
        {
            return Refuse($"{snapshot.UnverifiedCount} VRChat process(es) could not be checked");
        }

        var records = relaunch.Clients.Concat(reload.Clients).ToList();
        var owned = snapshot.Handles.Where(h => records.Any(r => VrchatRelaunchCoordinator.IsRecorded(h, r))).ToList();
        var others = snapshot.Handles.Count - owned.Count;
        if (requireNoOthers && others > 0)
        {
            return new CloseReport(true, 0, 0, others,
                $"{others} VRChat client(s) that the Bridge has no record of are running " +
                $"(PID {string.Join(", ", snapshot.Handles.Except(owned).Select(h => h.ProcessId))}); nothing was closed. " +
                "Close them on Windows first.");
        }

        if (owned.Count == 0)
        {
            return new CloseReport(false, 0, 0, others, null);
        }

        var result = await _terminator.CloseAsync(owned, cancellationToken).ConfigureAwait(false);

        // Entries for clients confirmed gone are dropped; one that may still run keeps its entry.
        var recordsUpdated = Keep(_relaunchRecord, relaunch.Clients, result.NotConfirmed)
            & Keep(_reloadRecord, reload.Clients, result.NotConfirmed);

        _logger.LogInformation(
            "Closed {Closed} of {Owned} VRChat client(s) launched by the Bridge through the LAN API ({Others} other VRChat left running).",
            result.Exited.Count, owned.Count, others);

        var problems = new List<string>();
        if (result.NotConfirmed.Count > 0)
        {
            problems.Add(
                $"{result.NotConfirmed.Count} client(s) did not exit within {VrchatClientTerminator.ExitDeadline.TotalSeconds:0} s " +
                $"(PID {string.Join(", ", result.NotConfirmed.Select(h => h.ProcessId))}).");
        }

        if (!recordsUpdated)
        {
            problems.Add("The Bridge could not update its record of the clients it launched.");
        }

        return new CloseReport(
            false, result.Exited.Count, result.NotConfirmed.Count, others,
            problems.Count > 0 ? string.Join(" ", problems) : null,
            RecordNotUpdated: !recordsUpdated);
    }

    /// <summary>False when the record could not be written.</summary>
    private bool Keep(IManagedClientStore store, IReadOnlyList<ManagedClient> records, IReadOnlyList<IVrchatProcessHandle> stillRunning)
    {
        if (records.Count == 0)
        {
            return true;
        }

        var kept = records.Where(r => stillRunning.Any(h => VrchatRelaunchCoordinator.IsRecorded(h, r))).ToList();
        try
        {
            store.Save(kept);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not update {Path} after closing clients.", store.FilePath);
            return false;
        }
    }

    private CloseReport Refuse(string problem)
    {
        _logger.LogWarning("Closing VRChat through the LAN API refused: {Problem}.", problem);
        return new CloseReport(true, 0, 0, 0, problem + "; nothing was closed.");
    }
}

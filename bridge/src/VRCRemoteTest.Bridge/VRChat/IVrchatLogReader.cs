namespace VRCRemoteTest.Bridge.VRChat;

public interface IVrchatLogReader
{
    /// <summary>
    /// Enumerates the VRChat clients once, updates their slots and resolves each slot's log
    /// file, reading a bounded tail window of each (see VrchatLogReader). The only state
    /// kept across calls is which file belongs to each slot's client, which files were seen
    /// open by several VRChat clients, and which client the compatible copy follows.
    /// </summary>
    VrchatLogReadResult Read();

    /// <summary>
    /// <see cref="Read()"/>, stopping with OperationCanceledException before anything is
    /// saved when <paramref name="cancellationToken"/> fired while the clients were listed:
    /// listing can outlast a host's stop timeout, and a host being replaced must not save
    /// its roster over the next host's.
    /// </summary>
    VrchatLogReadResult Read(CancellationToken cancellationToken) => Read();
}

public enum SlotLogStatus
{
    /// <summary>The slot's client has a log, which <see cref="SlotLogResult.Path"/> names.</summary>
    Shown,

    /// <summary>The slot's client has not created its log yet.</summary>
    NotYet,

    /// <summary>No file could be told to be the slot's client's log.</summary>
    Unmatched,

    /// <summary>The slot's client, or the process list, could not be checked for now.</summary>
    CannotCheck,

    /// <summary>No client runs in the slot.</summary>
    NotRunning,

    /// <summary>The log directory does not exist; nothing is published.</summary>
    Unavailable,
}

/// <param name="Content">What logs/clients/slot-{n}.log shows: the tail of the log, or a diagnostic.</param>
public sealed record SlotLogResult(int Slot, SlotLogStatus Status, string? Path, LogSnapshotResult Content);

/// <param name="Latest">What logs/vrchat-latest.log shows, the same as before slots existed.</param>
public sealed record VrchatLogReadResult(
    VrchatClientSnapshot Snapshot, RosterView Roster, IReadOnlyList<SlotLogResult> Slots, LogSnapshotResult Latest);

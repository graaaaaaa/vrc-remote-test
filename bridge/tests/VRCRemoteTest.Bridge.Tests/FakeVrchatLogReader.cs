using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Returns <see cref="ResultToReturn"/> as vrchat-latest.log's content, with no client in any slot.</summary>
internal sealed class FakeVrchatLogReader : IVrchatLogReader
{
    private static readonly RosterView EmptyRoster = new(
        Enumerable.Range(0, 4).Select(s => new RosterSlot(s, null, RosterLiveness.Empty, null)).ToList(),
        new HashSet<int>(),
        0);

    public LogSnapshotResult ResultToReturn { get; set; } = LogSnapshotResult.Empty();

    /// <summary>When set, each slot publishes this content.</summary>
    public LogSnapshotResult? SlotContent { get; set; }

    public Exception? ThrowOnRead { get; set; }

    public int ReadCallCount { get; private set; }

    public VrchatLogReadResult Read()
    {
        ReadCallCount++;

        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        var slots = Enumerable.Range(0, 4)
            .Select(s => new SlotLogResult(s, SlotLogStatus.NotRunning, null, SlotContent ?? LogSnapshotResult.Empty()))
            .ToList();
        return new VrchatLogReadResult(VrchatClientSnapshot.None, EmptyRoster, slots, ResultToReturn);
    }
}

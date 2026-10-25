using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Lan;

/// <summary>The last verified slot-to-source mapping published by the observer.</summary>
public sealed class LogSourceRegistry
{
    public sealed record Source(int Slot, long ClientGeneration, long ObservationEpoch,
        string Path, DateTime CreationTimeUtc);

    private readonly object _gate = new();
    private readonly Source?[] _sources = new Source?[4];

    public Source? Get(int slot)
    {
        if (slot is < 0 or >= 4) return null;
        lock (_gate) return _sources[slot];
    }

    public void Clear()
    {
        lock (_gate) Array.Clear(_sources);
    }

    public void Publish(VrchatLogReadResult read, VrchatClients clients)
    {
        lock (_gate)
        {
            for (var slot = 0; slot < _sources.Length; slot++)
            {
                var log = read.Slots.FirstOrDefault(x => x.Slot == slot);
                var client = clients.Clients.FirstOrDefault(x => x.Slot == slot);
                _sources[slot] = log?.Status == SlotLogStatus.Shown
                    && log.Path is { } path && client?.ObservationEpoch is { } epoch
                    && File.Exists(path)
                    ? new Source(slot, client.ClientGeneration, epoch, path, File.GetCreationTimeUtc(path))
                    : null;
            }
        }
    }
}

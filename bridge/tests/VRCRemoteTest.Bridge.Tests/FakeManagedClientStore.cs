using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>In-memory record with injectable read and write failures.</summary>
internal sealed class FakeManagedClientStore : IManagedClientStore
{
    public string FilePath => @"C:\Users\dev\AppData\Local\VRCRemoteTest\state\managed-clients.json";

    public List<ManagedClient> Clients { get; set; } = [];

    /// <summary>When set, Load returns this instead of <see cref="Clients"/>.</summary>
    public ManagedClientLoadResult? LoadResult { get; set; }

    /// <summary>Zero-based Save calls that throw.</summary>
    public HashSet<int> FailingSaves { get; set; } = [];

    /// <summary>What every successful Save wrote, in order.</summary>
    public List<IReadOnlyList<ManagedClient>> Saved { get; } = [];

    public int SaveCalls { get; private set; }

    public ManagedClientLoadResult Load() => LoadResult ?? ManagedClientLoadResult.Loaded(Clients.ToList());

    public void Save(IReadOnlyList<ManagedClient> clients)
    {
        var call = SaveCalls++;
        if (FailingSaves.Contains(call))
        {
            throw new IOException("simulated write failure");
        }

        Clients = clients.ToList();
        Saved.Add(clients.ToArray());
    }

    public IReadOnlyList<ManagedClient> Known => Clients;
}

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// One running VRChat process in the Bridge's own interactive session, identified by
/// (ProcessId, CreationTimeUtc) so a reused PID never looks like the same client.
/// </summary>
/// <param name="CreationTimeUtc">Full precision, UTC. Null when the process could not be queried.</param>
/// <param name="WatchWorldsDetected">False also when WMI could not read the command line.</param>
public sealed record VrchatClientInfo(int ProcessId, DateTime? CreationTimeUtc, bool WatchWorldsDetected);

/// <summary>
/// The result of enumerating VRChat processes once. Anything that could not be
/// confirmed is reported, never folded into "not running": a VRChat the Bridge cannot
/// check must stop it from launching more clients next to it.
/// </summary>
public sealed class VrchatClientSnapshot
{
    public VrchatClientSnapshot(
        IReadOnlyList<VrchatClientInfo> clients, int unverifiedCount = 0, string? enumerationError = null)
    {
        Clients = clients;
        UnverifiedCount = unverifiedCount;
        EnumerationError = enumerationError;
    }

    /// <summary>Same-session clients whose session (and existence) was confirmed.</summary>
    public IReadOnlyList<VrchatClientInfo> Clients { get; }

    /// <summary>VRChat processes whose session could not be read, or that kept vanishing mid-read.</summary>
    public int UnverifiedCount { get; }

    /// <summary>Non-null when the process list itself could not be read; nothing else is known then.</summary>
    public string? EnumerationError { get; }

    public static VrchatClientSnapshot None { get; } = new([]);

    public static VrchatClientSnapshot Failed(string error) => new([], 0, error);
}

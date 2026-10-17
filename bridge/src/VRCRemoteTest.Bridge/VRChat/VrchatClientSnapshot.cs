namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// One running VRChat process in the Bridge's own interactive session, identified by
/// (ProcessId, CreationTimeUtc) so a reused PID never looks like the same client.
/// </summary>
/// <param name="CreationTimeUtc">Full precision, UTC. Null when the process could not be queried.</param>
/// <param name="WatchWorldsDetected">
/// Read from the command line WMI returned. A client whose command line could not be
/// read is not a client at all but counts in <see cref="VrchatClientSnapshot.UnverifiedCount"/>.
/// </param>
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

    /// <summary>Same-session clients whose session, identity and command line were confirmed.</summary>
    public IReadOnlyList<VrchatClientInfo> Clients { get; }

    /// <summary>
    /// VRChat processes whose session could not be read, that kept vanishing mid-read,
    /// or whose command line WMI could not provide (the query failed or timed out).
    /// </summary>
    public int UnverifiedCount { get; }

    /// <summary>Non-null when the process list itself could not be read; nothing else is known then.</summary>
    public string? EnumerationError { get; }

    public static VrchatClientSnapshot None { get; } = new([]);

    public static VrchatClientSnapshot Failed(string error) => new([], 0, error);
}

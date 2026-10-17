namespace VRCRemoteTest.Bridge;

/// <summary>
/// Holds a lock file in StateDirectory open with FileShare.None for the Bridge's whole
/// lifetime, so a second Bridge refuses to start instead of deploying, launching VRChat
/// and writing results next to the first one. Task Scheduler's "do not start a new
/// instance" setting only covers instances it starts itself (multi-client plan
/// section 13.1). The OS releases the lock when the process exits, however it exits.
/// </summary>
public sealed class BridgeInstanceLock : IDisposable
{
    internal const string FileName = "bridge.lock";

    private readonly FileStream _stream;

    private BridgeInstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static string PathFor(string stateDirectory) => Path.Combine(stateDirectory, FileName);

    /// <summary>Null when another process holds the lock.</summary>
    public static BridgeInstanceLock? TryAcquire(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);

        try
        {
            return new BridgeInstanceLock(new FileStream(
                PathFor(stateDirectory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}

namespace VRCRemoteTest.Bridge.VRChat;

public interface IVrchatLogReader
{
    /// <summary>
    /// Re-resolves the log file and reads a bounded tail window on every call. No
    /// offset/rotation state is kept across calls (Codex plan review Phase 5,
    /// confidence 0.91); the only state is which file belongs to the first VRChat
    /// client and which files were seen open by several VRChat clients (see VrchatLogReader).
    /// </summary>
    LogSnapshotResult ReadSnapshot();
}

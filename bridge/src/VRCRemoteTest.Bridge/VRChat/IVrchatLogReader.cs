namespace VRCRemoteTest.Bridge.VRChat;

public interface IVrchatLogReader
{
    /// <summary>
    /// Stateless: re-resolves the newest output_log_*.txt and reads a bounded
    /// tail window on every call. No offset/rotation state is kept across
    /// calls (Codex plan review Phase 5, confidence 0.91).
    /// </summary>
    LogSnapshotResult ReadSnapshot();
}

namespace VRCRemoteTest
{
    /// <summary>
    /// Coarse status for progress reporting. Deliberately just 4 values plus a
    /// free-text message, not a detailed per-phase enum — Phase 2 has no UI
    /// consumer that needs discrete states, and a richer state machine was
    /// already identified as over-engineering on the Bridge side (Codex plan
    /// review Round 3, confidence 0.89).
    /// </summary>
    public enum RemoteBuildStatus
    {
        Idle,
        Running,
        Succeeded,
        Failed
    }

    public sealed class RemoteBuildProgress
    {
        public RemoteBuildStatus Status { get; }
        public string Message { get; }

        public RemoteBuildProgress(RemoteBuildStatus status, string message)
        {
            Status = status;
            Message = message;
        }
    }
}

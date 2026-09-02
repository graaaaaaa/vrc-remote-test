namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>Outcome of IVrchatLogReader.ReadSnapshot().</summary>
public sealed class LogSnapshotResult
{
    /// <summary>
    /// True when Content should be published (either real log content or a
    /// diagnostic message). False means "nothing to publish this poll" (log
    /// directory/file not found) -- the caller should simply skip writing,
    /// leaving any previously-published file as-is.
    /// </summary>
    public bool IsAvailable { get; }

    public string Content { get; }

    private LogSnapshotResult(bool isAvailable, string content)
    {
        IsAvailable = isAvailable;
        Content = content;
    }

    public static LogSnapshotResult Empty() => new(false, string.Empty);

    public static LogSnapshotResult Diagnostic(string message) => new(true, message);

    public static LogSnapshotResult Success(string content) => new(true, content);
}

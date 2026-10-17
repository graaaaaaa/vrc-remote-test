namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>Outcome of IVrchatLauncher.LaunchAsync for one client.</summary>
public sealed class LaunchResult
{
    public bool Succeeded { get; }

    public int? ProcessId { get; }

    /// <summary>
    /// Read from the started process right away and kept at full precision (UTC), so it
    /// can later be compared with what process enumeration reports for the same client.
    /// Null when the process could not be queried (e.g. it already exited).
    /// </summary>
    public DateTime? CreationTimeUtc { get; }

    public string? ErrorMessage { get; }

    private LaunchResult(bool succeeded, int? processId, DateTime? creationTimeUtc, string? errorMessage)
    {
        Succeeded = succeeded;
        ProcessId = processId;
        CreationTimeUtc = creationTimeUtc;
        ErrorMessage = errorMessage;
    }

    public static LaunchResult Success(int processId, DateTime? creationTimeUtc) =>
        new(true, processId, creationTimeUtc, null);

    public static LaunchResult Failure(string errorMessage) => new(false, null, null, errorMessage);
}

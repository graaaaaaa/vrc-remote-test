namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>Outcome of IVrchatReadinessCoordinator.EnsureReadyAsync.</summary>
public sealed class ReadinessResult
{
    public bool IsReady { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    private ReadinessResult(bool isReady, string? errorCode, string? errorMessage)
    {
        IsReady = isReady;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static ReadinessResult Ready() => new(true, null, null);

    public static ReadinessResult Failure(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage);
}

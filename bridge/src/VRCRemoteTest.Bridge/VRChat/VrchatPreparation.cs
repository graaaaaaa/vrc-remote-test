namespace VRCRemoteTest.Bridge.VRChat;

public enum VrchatPreparationKind
{
    /// <summary>Install only: every running --watch-worlds client reloads the new file.</summary>
    DeployOnly,

    /// <summary>No VRChat is running: install, archive, then launch the clients with --url.</summary>
    ColdStart,

    Failed,
}

/// <summary>Outcome of IVrchatClientCoordinator.PrepareAsync, decided before installing.</summary>
public sealed class VrchatPreparation
{
    public VrchatPreparationKind Kind { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    private VrchatPreparation(VrchatPreparationKind kind, string? errorCode, string? errorMessage)
    {
        Kind = kind;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public static VrchatPreparation DeployOnly() => new(VrchatPreparationKind.DeployOnly, null, null);

    public static VrchatPreparation ColdStart() => new(VrchatPreparationKind.ColdStart, null, null);

    public static VrchatPreparation Failure(string errorCode, string errorMessage) =>
        new(VrchatPreparationKind.Failed, errorCode, errorMessage);
}

/// <summary>Outcome of IVrchatClientCoordinator.LaunchClientsAsync.</summary>
public sealed class VrchatLaunchOutcome
{
    public bool Succeeded { get; }
    public string? ErrorMessage { get; }

    private VrchatLaunchOutcome(bool succeeded, string? errorMessage)
    {
        Succeeded = succeeded;
        ErrorMessage = errorMessage;
    }

    public static VrchatLaunchOutcome Success() => new(true, null);

    public static VrchatLaunchOutcome Failure(string errorMessage) => new(false, errorMessage);
}

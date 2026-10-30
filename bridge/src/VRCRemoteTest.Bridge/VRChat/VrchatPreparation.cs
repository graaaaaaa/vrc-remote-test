namespace VRCRemoteTest.Bridge.VRChat;

public enum VrchatPreparationKind
{
    /// <summary>Install only: every running --watch-worlds client reloads the new file.</summary>
    DeployOnly,

    /// <summary>
    /// No VRChat is running (in Relaunch mode: any more, the Bridge's own clients having
    /// been closed): install, archive, then launch the clients with --url.
    /// </summary>
    ColdStart,

    Failed,
}

/// <summary>Outcome of IVrchatClientCoordinator.PrepareAsync, decided before installing.</summary>
public sealed class VrchatPreparation
{
    public VrchatPreparationKind Kind { get; }
    public string? ErrorCode { get; }
    public string? ErrorMessage { get; }

    /// <summary>How many of the Bridge's own clients were closed (Relaunch mode), for the build result's launch report.</summary>
    public int ClosedClients { get; }

    private VrchatPreparation(VrchatPreparationKind kind, string? errorCode, string? errorMessage, int closedClients)
    {
        Kind = kind;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        ClosedClients = closedClients;
    }

    public static VrchatPreparation DeployOnly() => new(VrchatPreparationKind.DeployOnly, null, null, 0);

    public static VrchatPreparation ColdStart(int closedClients = 0) =>
        new(VrchatPreparationKind.ColdStart, null, null, closedClients);

    public static VrchatPreparation Failure(string errorCode, string errorMessage, int closedClients = 0) =>
        new(VrchatPreparationKind.Failed, errorCode, errorMessage, closedClients);
}

/// <summary>Outcome of IVrchatClientCoordinator.LaunchClientsAsync.</summary>
public sealed class VrchatLaunchOutcome
{
    public bool Succeeded { get; }

    /// <summary>
    /// VRCHAT_START_FAILED unless the launch stopped for another reason (Relaunch mode: a
    /// VRChat the Bridge did not launch appeared, or a client could not be recorded).
    /// </summary>
    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    /// <summary>How many clients the batch was to launch; null when unknown (an unexpected error).</summary>
    public int? Requested { get; }

    /// <summary>How many clients started before the batch ended; null when unknown.</summary>
    public int? Started { get; }

    private VrchatLaunchOutcome(bool succeeded, string? errorCode, string? errorMessage, int? requested, int? started)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Requested = requested;
        Started = started;
    }

    public static VrchatLaunchOutcome Success(int? launched = null) => new(true, null, null, launched, launched);

    public static VrchatLaunchOutcome Failure(
        string errorMessage,
        string errorCode = Protocol.ErrorCode.VrchatStartFailed,
        int? requested = null,
        int? started = null) =>
        new(false, errorCode, errorMessage, requested, started);
}

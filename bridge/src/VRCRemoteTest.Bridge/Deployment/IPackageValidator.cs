using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.Deployment;

public interface IPackageValidator
{
    ValidationResult Validate(BuildManifest manifest, string artifactPath, long maxArtifactSizeBytes);
}

public sealed class ValidationResult
{
    public bool IsValid { get; private init; }
    public string? ErrorCode { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ValidationResult Success() => new() { IsValid = true };

    public static ValidationResult Failure(string errorCode, string errorMessage) =>
        new() { IsValid = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}

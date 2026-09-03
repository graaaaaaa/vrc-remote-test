using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.Deployment;

/// <summary>
/// Validates a claimed manifest + artifact pair before it is allowed anywhere near
/// VRChat's live Worlds directory. This is the single security choke point for v1
/// (confirmed sufficient via Codex plan review): filename allow-listing, path
/// containment (implied by rejecting any separator in fileName), size limits, and
/// SHA-256 integrity. Authentication beyond SMB share ACLs (e.g. HMAC-signed
/// manifests) is deferred to v1.1.
/// </summary>
public sealed partial class PackageValidator : IPackageValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]+\.vrcw$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public ValidationResult Validate(BuildManifest manifest, string artifactPath, long maxArtifactSizeBytes)
    {
        if (manifest.ProtocolVersion != ProtocolConstants.CurrentProtocolVersion)
        {
            return ValidationResult.Failure(
                ErrorCode.ProtocolVersionMismatch,
                $"Unsupported protocol version {manifest.ProtocolVersion}, expected {ProtocolConstants.CurrentProtocolVersion}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.BuildId))
        {
            return ValidationResult.Failure(ErrorCode.ManifestInvalid, "buildId is required.");
        }

        var fileNameResult = ValidateFileName(manifest.FileName);
        if (fileNameResult is not null)
        {
            return fileNameResult;
        }

        if (manifest.Size <= 0)
        {
            return ValidationResult.Failure(ErrorCode.ManifestInvalid, "size must be positive.");
        }

        if (manifest.Size > maxArtifactSizeBytes)
        {
            return ValidationResult.Failure(
                ErrorCode.SizeExceeded,
                $"Artifact size {manifest.Size} exceeds maximum {maxArtifactSizeBytes} bytes.");
        }

        if (string.IsNullOrEmpty(manifest.Sha256) || manifest.Sha256.Length != 64 || !IsHex(manifest.Sha256))
        {
            return ValidationResult.Failure(ErrorCode.ManifestInvalid, "sha256 must be a 64-character hex string.");
        }

        if (!File.Exists(artifactPath))
        {
            return ValidationResult.Failure(ErrorCode.ArtifactNotFound, $"Artifact not found at '{artifactPath}'.");
        }

        var actualSize = new FileInfo(artifactPath).Length;
        if (actualSize != manifest.Size)
        {
            return ValidationResult.Failure(
                ErrorCode.SizeMismatch,
                $"Artifact size {actualSize} does not match manifest size {manifest.Size}.");
        }

        var actualHash = ComputeSha256(artifactPath);
        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Failure(
                ErrorCode.HashMismatch,
                $"Artifact SHA-256 '{actualHash}' does not match manifest '{manifest.Sha256}'.");
        }

        return ValidationResult.Success();
    }

    private static ValidationResult? ValidateFileName(string fileName)
    {
        // Reject separators/traversal explicitly, in addition to the allow-list regex below,
        // so the failure reason is unambiguous even though the regex alone already excludes them.
        if (string.IsNullOrEmpty(fileName) ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.Contains(':') ||
            !FileNamePattern().IsMatch(fileName))
        {
            return ValidationResult.Failure(ErrorCode.ManifestInvalid, $"fileName '{fileName}' is not a valid basename.");
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        if (ReservedWindowsNames.Contains(nameWithoutExtension))
        {
            return ValidationResult.Failure(
                ErrorCode.ManifestInvalid,
                $"fileName '{fileName}' uses a reserved Windows device name.");
        }

        return null;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    internal static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

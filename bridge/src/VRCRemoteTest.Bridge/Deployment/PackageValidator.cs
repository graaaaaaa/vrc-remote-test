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

    // Same safe-basename allow-list as FileNamePattern, minus the .vrcw extension
    // requirement (buildId has no extension). Deliberately looser than
    // package/Editor/Utility/BuildIdGenerator.cs's generated-format regex: the
    // point here is to make Path.Combine(..., buildId) traversal-safe, not to
    // re-validate that buildId was actually produced by that generator.
    [GeneratedRegex(@"^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildIdPattern();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Windows treats a reserved device name as reserved regardless of what follows
    // the FIRST '.' (e.g. "NUL.foo.vrcw" still addresses the NUL device), so this
    // must split on the first '.', not use Path.GetFileNameWithoutExtension (which
    // only strips the LAST extension and would let "NUL.foo.vrcw" slip through as
    // "NUL.foo") (Codex code review round 2, confidence 0.8).
    private static bool IsReservedWindowsDeviceName(string name)
    {
        var firstDot = name.IndexOf('.');
        var baseName = firstDot < 0 ? name : name[..firstDot];
        return ReservedWindowsNames.Contains(baseName);
    }

    public ValidationResult Validate(BuildManifest manifest, string artifactPath, long maxArtifactSizeBytes)
    {
        if (manifest.ProtocolVersion != ProtocolConstants.CurrentProtocolVersion)
        {
            return ValidationResult.Failure(
                ErrorCode.ProtocolVersionMismatch,
                $"Unsupported protocol version {manifest.ProtocolVersion}, expected {ProtocolConstants.CurrentProtocolVersion}.");
        }

        var buildIdResult = ValidateBuildId(manifest.BuildId);
        if (buildIdResult is not null)
        {
            return buildIdResult;
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

    /// <summary>
    /// Exposed (not <c>private</c>) so <see cref="StagingWatcher"/> can reject an
    /// unsafe <c>buildId</c> before it is ever passed to <see cref="Path.Combine"/>
    /// — <c>buildId</c> is used to build filesystem paths in <see cref="StagingWatcher"/>
    /// (archive manifest), <see cref="WorldInstaller"/> (the deployed <c>.vrcw</c> name,
    /// written into VRChat's live Worlds directory), and <see cref="ResultWriter"/>
    /// (the result JSON name) well before <see cref="Validate"/> would ever reject a
    /// malformed value. A previous check only rejected null/whitespace, so a
    /// <c>buildId</c> containing an embedded path separator (e.g. <c>"x\..\..\evil"</c>)
    /// could traverse outside the intended directory once combined with a literal
    /// prefix such as <c>"vrc-remote-"</c> (Codex code review, confidence 0.85).
    /// </summary>
    internal static ValidationResult? ValidateBuildId(string buildId)
    {
        // Reject separators/traversal explicitly, in addition to the allow-list regex below,
        // so the failure reason is unambiguous even though the regex alone already excludes them.
        if (string.IsNullOrWhiteSpace(buildId) ||
            buildId.Contains("..", StringComparison.Ordinal) ||
            buildId.Contains('/') ||
            buildId.Contains('\\') ||
            buildId.Contains(':') ||
            !BuildIdPattern().IsMatch(buildId))
        {
            return ValidationResult.Failure(ErrorCode.ManifestInvalid, $"buildId '{buildId}' contains characters that are not allowed.");
        }

        if (IsReservedWindowsDeviceName(buildId))
        {
            return ValidationResult.Failure(ErrorCode.ManifestInvalid, $"buildId '{buildId}' uses a reserved Windows device name.");
        }

        return null;
    }

    /// <summary>
    /// Exposed (not <c>private</c>) so <see cref="StagingWatcher"/> can reject an
    /// unsafe <c>fileName</c> before it is ever passed to <see cref="Path.Combine"/>
    /// — that combination happens earlier than the call to <see cref="Validate"/>
    /// (to move an already-uploaded artifact from <c>incoming/</c> into
    /// <c>processing/</c>), so relying on <see cref="Validate"/> alone left a window
    /// where an untrusted <c>fileName</c> (containing "..", a separator, or an
    /// absolute path — which <see cref="Path.Combine"/> would silently let override
    /// the base directory entirely) reached the filesystem unchecked (Codex code
    /// review, confidence 0.85).
    /// </summary>
    internal static ValidationResult? ValidateFileName(string fileName)
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

        if (IsReservedWindowsDeviceName(fileName))
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

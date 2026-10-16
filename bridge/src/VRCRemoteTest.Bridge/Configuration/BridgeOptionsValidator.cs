using Microsoft.Extensions.Options;

namespace VRCRemoteTest.Bridge.Configuration;

/// <summary>
/// Fails fast on startup rather than guessing a fallback location for
/// VrchatWorldsDirectory (see spec section 25 / docs/sdk-api-notes.md).
/// </summary>
public sealed class BridgeOptionsValidator : IValidateOptions<BridgeOptions>
{
    private static readonly string[] ValidVrchatModes = { "Desktop", "VR" };

    /// <summary>
    /// Real margin beyond VrchatStartupSettleDelaySeconds + a 2-poll
    /// stabilization allowance, for VRChat's own boot time variance (Codex plan
    /// review Phase 4.1, Round 3; margin value unchanged, but the timeout floor
    /// is now relative to the configurable settle delay rather than a fixed
    /// constant, since real-hardware testing on 2026-09-02 showed VRChat's
    /// actual boot-to-home-screen time needs per-machine tuning).
    /// </summary>
    private const int MinTimeoutMarginSeconds = 30;

    public ValidateOptionsResult Validate(string? name, BridgeOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.StagingDirectory))
        {
            failures.Add("StagingDirectory must be configured.");
        }

        ValidateStateDirectory(options, failures);

        if (string.IsNullOrWhiteSpace(options.VrchatWorldsDirectory))
        {
            failures.Add("VrchatWorldsDirectory must be configured.");
        }
        else if (!Directory.Exists(options.VrchatWorldsDirectory))
        {
            failures.Add(
                $"VrchatWorldsDirectory '{options.VrchatWorldsDirectory}' does not exist. " +
                "Refusing to guess an alternate location; create the directory or fix config.json.");
        }

        if (options.MaxArtifactSizeBytes <= 0)
        {
            failures.Add("MaxArtifactSizeBytes must be positive.");
        }

        if (options.RetainBuilds < 0)
        {
            failures.Add("RetainBuilds must not be negative.");
        }

        if (options.AutoLaunchVrchat)
        {
            ValidateVrchatExecutable(options.VrchatExecutable, failures);
        }

        if (!string.IsNullOrWhiteSpace(options.VrchatMode)
            && Array.FindIndex(
                ValidVrchatModes, m => string.Equals(m, options.VrchatMode, StringComparison.OrdinalIgnoreCase)) < 0)
        {
            failures.Add(
                $"VrchatMode '{options.VrchatMode}' is invalid. Must be 'Desktop' or 'VR' (case-insensitive).");
        }

        if (options.VrchatStartupSettleDelaySeconds <= 0)
        {
            failures.Add("VrchatStartupSettleDelaySeconds must be positive.");
        }

        var minTimeout = options.VrchatStartupSettleDelaySeconds + MinTimeoutMarginSeconds;
        if (options.VrchatStartupTimeoutSeconds < minTimeout)
        {
            failures.Add(
                $"VrchatStartupTimeoutSeconds must be at least {minTimeout} " +
                $"(VrchatStartupSettleDelaySeconds [{options.VrchatStartupSettleDelaySeconds}] plus " +
                $"{MinTimeoutMarginSeconds}s of real margin for VRChat's boot time).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Fail-fast hardening for a config value that now results in Process.Start
    /// being called (Codex plan review Phase 4.1, Round 2, confidence 0.91):
    /// absolute, non-UNC, .exe, and the basename must be exactly "VRChat.exe" so
    /// a misconfigured path can't launch an arbitrary executable.
    /// </summary>
    private static void ValidateVrchatExecutable(string vrchatExecutable, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(vrchatExecutable))
        {
            failures.Add("VrchatExecutable must be configured when AutoLaunchVrchat is true.");
            return;
        }

        if (IsUncPath(vrchatExecutable))
        {
            failures.Add($"VrchatExecutable '{vrchatExecutable}' must not be a UNC/network path.");
            return;
        }

        if (!Path.IsPathFullyQualified(vrchatExecutable))
        {
            failures.Add($"VrchatExecutable '{vrchatExecutable}' must be an absolute path.");
            return;
        }

        if (!string.Equals(Path.GetExtension(vrchatExecutable), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"VrchatExecutable '{vrchatExecutable}' must have a .exe extension.");
            return;
        }

        if (!string.Equals(Path.GetFileName(vrchatExecutable), "VRChat.exe", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"VrchatExecutable '{vrchatExecutable}' must point to VRChat.exe.");
            return;
        }

        if (!File.Exists(vrchatExecutable))
        {
            failures.Add(
                $"VrchatExecutable '{vrchatExecutable}' does not exist. " +
                "Refusing to guess an alternate location; fix config.json.");
        }
    }

    /// <summary>
    /// StateDirectory holds everything the Bridge trusts after ingestion, so it
    /// must never be reachable through the SMB share. Both paths are
    /// canonicalized first: a lexical comparison alone would accept
    /// "C:\VRCRemoteTest2\..\VRCRemoteTest\state" as outside the staging root.
    /// </summary>
    private static void ValidateStateDirectory(BridgeOptions options, List<string> failures)
    {
        var stateDirectory = options.StateDirectory;

        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            failures.Add("StateDirectory must be configured.");
            return;
        }

        if (IsUncPath(stateDirectory))
        {
            failures.Add($"StateDirectory '{stateDirectory}' must not be a UNC/network path.");
            return;
        }

        if (!Path.IsPathFullyQualified(stateDirectory))
        {
            failures.Add($"StateDirectory '{stateDirectory}' must be an absolute path.");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.StagingDirectory))
        {
            // Already reported above; nothing to compare against.
            return;
        }

        string state;
        string staging;
        try
        {
            state = NormalizeDirectory(stateDirectory);
            staging = NormalizeDirectory(options.StagingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failures.Add($"StateDirectory or StagingDirectory is not a valid path: {ex.Message}");
            return;
        }

        if (IsSameOrInside(state, staging) || IsSameOrInside(staging, state))
        {
            failures.Add(
                $"StateDirectory '{stateDirectory}' must not overlap StagingDirectory '{options.StagingDirectory}': " +
                "Bridge state must stay outside the SMB share.");
            return;
        }

        if (Directory.Exists(state)
            && new DirectoryInfo(state).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            failures.Add($"StateDirectory '{stateDirectory}' must not be a symbolic link or junction.");
        }
    }

    /// <summary>
    /// Plain string-prefix check rather than relying on IsPathFullyQualified:
    /// that check's UNC handling differs across platforms (true on Windows,
    /// false on macOS/Linux for a backslash-prefixed string), but this behaves
    /// identically everywhere, keeping validation deterministic regardless of
    /// which OS the Bridge is built/tested on.
    /// </summary>
    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal);

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// Case-insensitive because the Bridge targets NTFS; on a case-sensitive
    /// filesystem this can only reject more, never less.
    /// </summary>
    private static bool IsSameOrInside(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

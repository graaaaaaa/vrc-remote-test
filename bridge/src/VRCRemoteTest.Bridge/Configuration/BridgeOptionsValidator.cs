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

        // Checked before IsPathFullyQualified: that check's UNC handling differs
        // across platforms (true on Windows, false on macOS/Linux for a
        // backslash-prefixed string), but this plain string-prefix check behaves
        // identically everywhere, keeping validation deterministic regardless of
        // which OS the Bridge is built/tested on.
        if (vrchatExecutable.StartsWith(@"\\", StringComparison.Ordinal)
            || vrchatExecutable.StartsWith("//", StringComparison.Ordinal))
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
}

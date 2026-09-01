using Microsoft.Extensions.Options;

namespace VRCRemoteTest.Bridge.Configuration;

/// <summary>
/// Fails fast on startup rather than guessing a fallback location for
/// VrchatWorldsDirectory (see spec section 25 / docs/sdk-api-notes.md).
/// </summary>
public sealed class BridgeOptionsValidator : IValidateOptions<BridgeOptions>
{
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

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

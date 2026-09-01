namespace VRCRemoteTest.Bridge.Configuration;

/// <summary>
/// Bound from %LOCALAPPDATA%\VRCRemoteTest\config.json (section "Bridge").
/// See docs/setup-windows.md for the config file format.
/// </summary>
public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    /// <summary>
    /// Root of the staging area (incoming/processing/archive/failed/results).
    /// Deliberately separate from <see cref="VrchatWorldsDirectory"/> so a
    /// malformed or partial upload can never land directly in VRChat's live
    /// watched directory.
    /// </summary>
    public string StagingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// VRChat's local Worlds directory that --watch-worlds observes. Must already
    /// exist; the Bridge refuses to start rather than guess an alternate location.
    /// </summary>
    public string VrchatWorldsDirectory { get; set; } = string.Empty;

    public long MaxArtifactSizeBytes { get; set; } = 500L * 1024 * 1024;

    public int RetainBuilds { get; set; } = 10;
}

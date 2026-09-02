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

    // === Phase 4.1: VRChat autoLaunch ===

    /// <summary>
    /// Absolute path to VRChat.exe. Required (and validated) only when
    /// <see cref="AutoLaunchVrchat"/> is true.
    /// </summary>
    public string VrchatExecutable { get; set; } = string.Empty;

    /// <summary>"Desktop" or "VR" (case-insensitive). Controls whether --no-vr is passed.</summary>
    public string VrchatMode { get; set; } = "Desktop";

    /// <summary>
    /// Default false: never launches VRChat on its own unless explicitly opted in.
    /// An already-running VRChat lacking --watch-worlds is never auto-restarted
    /// regardless of this setting (spec section 32).
    /// </summary>
    public bool AutoLaunchVrchat { get; set; } = false;

    /// <summary>
    /// Readiness timeout after launching VRChat. Default is 60s (not 30s) to
    /// leave real margin beyond the ~15-17s minimum imposed by
    /// VrchatReadinessCoordinator's StartupSettleDelay + stability polling.
    /// </summary>
    public int VrchatStartupTimeoutSeconds { get; set; } = 60;
}

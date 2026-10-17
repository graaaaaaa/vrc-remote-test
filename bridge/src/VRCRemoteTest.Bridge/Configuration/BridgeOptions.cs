namespace VRCRemoteTest.Bridge.Configuration;

/// <summary>
/// Bound from %LOCALAPPDATA%\VRCRemoteTest\config.json (section "Bridge").
/// See docs/setup-windows.md for the config file format.
/// </summary>
public sealed class BridgeOptions
{
    public const string SectionName = "Bridge";

    public const int DefaultVrchatClientCount = 1;

    /// <summary>The largest client count verified on real hardware (docs/validation/multi-client-spike.md, case 7).</summary>
    public const int MaxVrchatClientCount = 4;

    /// <summary>
    /// Root of the SMB-shared staging area (incoming/results/status/logs).
    /// Deliberately separate from <see cref="VrchatWorldsDirectory"/> so a
    /// malformed or partial upload can never land directly in VRChat's live
    /// watched directory.
    /// </summary>
    public string StagingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Bridge-private state (processing/archive/failed). Never inside
    /// <see cref="StagingDirectory"/>: the SMB account can write the share, so
    /// nothing the Bridge later trusts (claimed manifests, validated artifact
    /// snapshots, idempotency records) may live there. Program.cs always sets
    /// this to %LOCALAPPDATA%\VRCRemoteTest\state, overriding config.json, so the
    /// directory inherits the user profile's private ACL instead of needing a
    /// runtime DACL check; the setter exists for test injection.
    /// </summary>
    public string StateDirectory { get; set; } = string.Empty;

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

    /// <summary>
    /// "Desktop" or "VR" (case-insensitive). Controls whether --no-vr is passed; with
    /// several clients only the first one uses the HMD.
    /// </summary>
    public string VrchatMode { get; set; } = "Desktop";

    /// <summary>
    /// Default false: never launches VRChat on its own unless explicitly opted in.
    /// An already-running VRChat lacking --watch-worlds is never auto-restarted
    /// regardless of this setting (spec section 32).
    /// </summary>
    public bool AutoLaunchVrchat { get; set; } = false;

    /// <summary>
    /// How many VRChat clients a cold start launches (all on the same account, like the
    /// SDK's Number of Clients). Nullable so validation can tell an explicit value from
    /// an omitted one; read it through <see cref="ResolvedVrchatClientCount"/>.
    /// </summary>
    public int? VrchatClientCount { get; set; }

    public int ResolvedVrchatClientCount => VrchatClientCount ?? DefaultVrchatClientCount;

    /// <summary>
    /// Upper bound on the wait before deploying to already-running clients. Must leave
    /// real margin beyond VrchatStartupSettleDelaySeconds + stability polling.
    /// </summary>
    public int VrchatStartupTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Minimum time VrchatClientCoordinator waits after each running client's process
    /// creation time before deploying to it, even if the WMI-based --watch-worlds
    /// signal is already stable -- the command line is visible to WMI essentially at
    /// process creation, long before VRChat has logged in and armed its
    /// Worlds-directory watcher. Default (30s) is a starting point; tune this per
    /// machine (real-hardware testing on 2026-09-02 observed ~15-30s to the home
    /// screen, and the original 15s default proved too short -- a build deployed at
    /// that point did not trigger a reload). A cold start does not wait for it: the
    /// clients load the deployed world through --url.
    /// </summary>
    public int VrchatStartupSettleDelaySeconds { get; set; } = 30;
}

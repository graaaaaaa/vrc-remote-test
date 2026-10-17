using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// End-to-end tests against a real temp filesystem (no mocks for I/O), matching the
/// plan's Bridge test strategy: manifest validation, atomic deploy, idempotency,
/// and startup recovery.
/// </summary>
public class StagingWatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _stagingDir;
    private readonly string _stateDir;
    private readonly string _worldsDir;
    private readonly VrchatLogNotice _logNotice = new();

    public StagingWatcherTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-watcher-").FullName;
        _stagingDir = Path.Combine(_tempDir, "staging");
        _stateDir = Path.Combine(_tempDir, "state");
        _worldsDir = Path.Combine(_tempDir, "worlds");
        Directory.CreateDirectory(_worldsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private BridgeOptions CreateOptions(
        long maxSize = 1024,
        int retainBuilds = 10,
        bool autoLaunchVrchat = false,
        int? clientCount = null,
        int startupTimeoutSeconds = 60) => new()
    {
        StagingDirectory = _stagingDir,
        StateDirectory = _stateDir,
        VrchatWorldsDirectory = _worldsDir,
        MaxArtifactSizeBytes = maxSize,
        RetainBuilds = retainBuilds,
        AutoLaunchVrchat = autoLaunchVrchat,
        VrchatExecutable = @"C:\VRChat\VRChat.exe",
        VrchatClientCount = clientCount,
        VrchatStartupTimeoutSeconds = startupTimeoutSeconds,
    };

    private StagingWatcher CreateWatcher(
        long maxSize = 1024,
        int retainBuilds = 10,
        bool autoLaunchVrchat = false,
        IVrchatClientCoordinator? clientCoordinator = null,
        IPackageValidator? validator = null,
        IWorldInstaller? installer = null,
        IResultWriter? resultWriter = null,
        int? clientCount = null,
        ILogger<StagingWatcher>? logger = null) =>
        new(
            Options.Create(CreateOptions(maxSize, retainBuilds, autoLaunchVrchat, clientCount)),
            validator ?? new PackageValidator(),
            installer ?? new WorldInstaller(),
            resultWriter ?? new ResultWriter(),
            new CleanupService(),
            clientCoordinator ?? new FakeVrchatClientCoordinator(),
            _logNotice,
            logger ?? NullLogger<StagingWatcher>.Instance);

    /// <summary>
    /// The real coordinator over fake processes, for the multi-client integration tests.
    /// The log directory is reported as unreadable, so launches keep the fixed interval
    /// without waiting for output_log files (VrchatClientCoordinatorTests covers that).
    /// </summary>
    private VrchatClientCoordinator CreateCoordinator(
        FakeVrchatProcessMonitor monitor,
        FakeVrchatLauncher launcher,
        int? clientCount,
        TimeSpan? settleDelay = null,
        Func<TimeSpan, CancellationToken, Task>? launchDelay = null,
        int startupTimeoutSeconds = 60) =>
        new(
            Options.Create(CreateOptions(
                autoLaunchVrchat: true, clientCount: clientCount, startupTimeoutSeconds: startupTimeoutSeconds)),
            monitor,
            launcher,
            NullLogger<VrchatClientCoordinator>.Instance,
            settleDelay ?? TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(5),
            launchDelay ?? ((_, _) => Task.CompletedTask),
            () => null,
            () => DateTime.UtcNow,
            TimeZoneInfo.Utc);

    private static void WriteIncomingBuild(string stagingDir, string buildId, byte[] artifactBytes)
    {
        var incoming = Path.Combine(stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        var artifactName = $"{buildId}.vrcw";
        File.WriteAllBytes(Path.Combine(incoming, artifactName), artifactBytes);
        File.WriteAllText(
            Path.Combine(incoming, $"{buildId}.ready.json"),
            JsonSerializer.Serialize(ManifestFor(buildId, artifactBytes)));
    }

    private static BuildManifest ManifestFor(string buildId, byte[] artifactBytes) => new()
    {
        ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
        BuildId = buildId,
        FileName = $"{buildId}.vrcw",
        Size = artifactBytes.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private string ResultPath(string buildId) => Path.Combine(_stagingDir, "results", $"{buildId}.json");

    private BuildResult ReadResult(string buildId)
    {
        var resultJson = File.ReadAllText(ResultPath(buildId));
        return JsonSerializer.Deserialize<BuildResult>(
            resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public async Task Deploys_a_valid_build_and_archives_it()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-1", bytes);

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-1.vrcw")).Should().BeTrue();
        File.Exists(Path.Combine(_stateDir, "archive", "build-1.ready.json")).Should().BeTrue();
        File.Exists(Path.Combine(_stagingDir, "results", "build-1.json")).Should().BeTrue();
        File.Exists(Path.Combine(_stagingDir, "incoming", "build-1.ready.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Does_not_reprocess_an_already_archived_build_and_keeps_its_result()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-2", bytes);
        await watcher.RunOnceAsync(CancellationToken.None);
        var deployed = File.ReadAllText(ResultPath("build-2"));

        // Simulate the same build id arriving a second time, with different bytes.
        WriteIncomingBuild(_stagingDir, "build-2", Encoding.UTF8.GetBytes("different-bundle"));
        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir, "vrc-remote-build-2*.vrcw").Should().HaveCount(1);
        File.ReadAllBytes(Path.Combine(_worldsDir, "vrc-remote-build-2.vrcw")).Should().Equal(bytes);
        Directory.GetFiles(Path.Combine(_stagingDir, "incoming")).Should().BeEmpty("the duplicate is consumed");
        File.ReadAllText(ResultPath("build-2")).Should().Be(deployed, "the duplicate never replaces the result");
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_duplicate_of_an_archived_build_without_a_result_reports_BuildAlreadyProcessed()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-noresult-dup", bytes);
        await watcher.RunOnceAsync(CancellationToken.None);

        // As after a cold start the Bridge stopped during the launch, or results/ was cleaned up.
        File.Delete(ResultPath("build-noresult-dup"));
        WriteIncomingBuild(_stagingDir, "build-noresult-dup", bytes);
        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("build-noresult-dup").ErrorCode.Should().Be(ErrorCode.BuildAlreadyProcessed);
        Directory.GetFiles(Path.Combine(_stagingDir, "incoming")).Should().BeEmpty();
    }

    /// <summary>
    /// The 2026-10-18 case: about 10 s after a deploy, an empty file with the deployed
    /// build's manifest name appeared in incoming/ and its MANIFEST_INVALID replaced the
    /// deployed result. Anything under that name that cannot name a build is now dropped.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ \"protocolVersion\": 1, \"buildI")]
    [InlineData("{}")]
    [InlineData("{ \"buildId\": \"not allowed\" }")]
    public async Task A_late_unreadable_manifest_named_after_a_deployed_build_keeps_its_deployed_result(string content)
    {
        var logger = new ListLogger<StagingWatcher>();
        var watcher = CreateWatcher(logger: logger);
        WriteIncomingBuild(_stagingDir, "build-zombie", Encoding.UTF8.GetBytes("fake-world-bundle"));
        await watcher.RunOnceAsync(CancellationToken.None);
        var deployed = File.ReadAllText(ResultPath("build-zombie"));

        File.WriteAllText(Path.Combine(_stagingDir, "incoming", "build-zombie.ready.json"), content);
        await watcher.RunOnceAsync(CancellationToken.None);

        File.ReadAllText(ResultPath("build-zombie")).Should().Be(deployed);
        ReadResult("build-zombie").Status.Should().Be("deployed");
        Directory.GetFiles(Path.Combine(_stagingDir, "incoming")).Should().BeEmpty("the late copy is consumed");
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
        File.Exists(ArchivedManifest("build-zombie")).Should().BeTrue();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("build-zombie was already processed"));
    }

    [Fact]
    public async Task A_late_empty_manifest_named_after_a_failed_build_keeps_its_failure_and_quarantined_manifest()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        var artifactBytes = Encoding.UTF8.GetBytes("corrupted-data");
        File.WriteAllBytes(Path.Combine(incoming, "build-failed-zombie.vrcw"), artifactBytes);
        var manifest = ManifestFor("build-failed-zombie", Encoding.UTF8.GetBytes("expected-data"));
        var manifestJson = JsonSerializer.Serialize(new BuildManifest
        {
            ProtocolVersion = manifest.ProtocolVersion,
            BuildId = manifest.BuildId,
            FileName = manifest.FileName,
            Size = artifactBytes.Length,
            Sha256 = manifest.Sha256, // hash of different content
            CreatedAtUtc = manifest.CreatedAtUtc,
        });
        File.WriteAllText(Path.Combine(incoming, "build-failed-zombie.ready.json"), manifestJson);
        await watcher.RunOnceAsync(CancellationToken.None);
        ReadResult("build-failed-zombie").ErrorCode.Should().Be(ErrorCode.HashMismatch);

        File.WriteAllText(Path.Combine(incoming, "build-failed-zombie.ready.json"), string.Empty);
        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("build-failed-zombie").ErrorCode.Should().Be(ErrorCode.HashMismatch);
        File.ReadAllText(Path.Combine(_stateDir, "failed", "build-failed-zombie.ready.json"))
            .Should().Be(manifestJson, "the quarantined manifest is not replaced by the empty copy");
        Directory.GetFiles(incoming).Should().BeEmpty();
    }

    [Fact]
    public async Task A_late_oversized_manifest_named_after_a_deployed_build_keeps_its_deployed_result()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild(_stagingDir, "build-big-zombie", Encoding.UTF8.GetBytes("fake-world-bundle"));
        await watcher.RunOnceAsync(CancellationToken.None);

        File.WriteAllText(
            Path.Combine(_stagingDir, "incoming", "build-big-zombie.ready.json"),
            new string(' ', StagingWatcher.MaxManifestBytes + 1));
        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("build-big-zombie").Status.Should().Be("deployed");
        Directory.GetFiles(Path.Combine(_stagingDir, "incoming")).Should().BeEmpty();
    }

    [Fact]
    public async Task Recovery_drops_a_late_unreadable_manifest_named_after_a_deployed_build()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild(_stagingDir, "build-zombie-crash", Encoding.UTF8.GetBytes("fake-world-bundle"));
        await watcher.RunOnceAsync(CancellationToken.None);

        // The Bridge stopped right after claiming the late copy.
        File.WriteAllText(Path.Combine(_stateDir, "processing", "build-zombie-crash.ready.json"), string.Empty);
        await CreateWatcher().RecoverProcessingAsync(CancellationToken.None);

        ReadResult("build-zombie-crash").Status.Should().Be("deployed");
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_manifest_for_a_new_build_is_still_reported_as_invalid()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "build-empty.ready.json"), string.Empty);

        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("build-empty").ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
        File.Exists(Path.Combine(_stateDir, "failed", "build-empty.ready.json")).Should().BeTrue();
        Directory.GetFiles(incoming).Should().BeEmpty();
    }

    [Fact]
    public async Task A_duplicate_with_a_path_traversal_file_name_never_touches_the_file_it_points_at()
    {
        // The duplicate check now runs before the file name is validated, so the shared
        // artifact it consumes must still be a safe basename.
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-dup-traversal", bytes);
        await watcher.RunOnceAsync(CancellationToken.None);

        var victimPath = Path.Combine(_stagingDir, "victim.vrcw");
        File.WriteAllBytes(victimPath, bytes);
        var duplicate = ManifestFor("build-dup-traversal", bytes);
        File.WriteAllText(
            Path.Combine(_stagingDir, "incoming", "build-dup-traversal.ready.json"),
            JsonSerializer.Serialize(new BuildManifest
            {
                ProtocolVersion = duplicate.ProtocolVersion,
                BuildId = duplicate.BuildId,
                FileName = "../victim.vrcw", // incoming/../victim.vrcw == _stagingDir/victim.vrcw
                Size = duplicate.Size,
                Sha256 = duplicate.Sha256,
                CreatedAtUtc = duplicate.CreatedAtUtc,
            }));

        await watcher.RunOnceAsync(CancellationToken.None);

        File.ReadAllBytes(victimPath).Should().Equal(bytes);
        ReadResult("build-dup-traversal").Status.Should().Be("deployed");
        Directory.GetFiles(Path.Combine(_stagingDir, "incoming")).Should().BeEmpty();
    }

    [Fact]
    public async Task Quarantines_a_build_with_hash_mismatch_and_does_not_deploy_it()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        var artifactBytes = Encoding.UTF8.GetBytes("corrupted-data");
        File.WriteAllBytes(Path.Combine(incoming, "build-3.vrcw"), artifactBytes);

        var manifest = new BuildManifest
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            BuildId = "build-3",
            FileName = "build-3.vrcw",
            Size = artifactBytes.Length,
            Sha256 = new string('0', 64), // deliberately wrong
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(incoming, "build-3.ready.json"), JsonSerializer.Serialize(manifest));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_stateDir, "failed", "build-3.ready.json")).Should().BeTrue();

        var resultJson = await File.ReadAllTextAsync(Path.Combine(_stagingDir, "results", "build-3.json"));
        var result = JsonSerializer.Deserialize<BuildResult>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        result!.Status.Should().Be("failed");
        result.ErrorCode.Should().Be(ErrorCode.HashMismatch);
    }

    [Fact]
    public async Task Quarantines_a_manifest_with_path_traversal_file_name()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        var manifest = new BuildManifest
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            BuildId = "build-evil",
            FileName = "../../evil.vrcw",
            Size = 4,
            Sha256 = new string('a', 64),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(incoming, "build-evil.ready.json"), JsonSerializer.Serialize(manifest));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_stateDir, "failed", "build-evil.ready.json")).Should().BeTrue();
    }

    [Fact]
    public async Task A_path_traversal_file_name_never_touches_the_file_it_points_at()
    {
        // Path.Combine(incomingDir, fileName) is evaluated (and File.Exists/File.Move
        // attempted against the result) before PackageValidator.Validate() runs, so a
        // rejection that only happens inside Validate() would be too late — the move
        // could already have happened. This test plants a real file at the path the
        // traversal resolves to and asserts it is left completely untouched, which
        // only holds if the fileName is rejected before that Path.Combine result is
        // ever used for filesystem I/O (Codex code review, confidence 0.85).
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        var victimPath = Path.Combine(_stagingDir, "victim.vrcw");
        var victimBytes = Encoding.UTF8.GetBytes("do-not-touch-me");
        File.WriteAllBytes(victimPath, victimBytes);
        var victimLastWrite = File.GetLastWriteTimeUtc(victimPath);

        var manifest = new BuildManifest
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            BuildId = "build-traversal",
            FileName = "../victim.vrcw", // incoming/../victim.vrcw == _stagingDir/victim.vrcw
            Size = victimBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(victimBytes)).ToLowerInvariant(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(incoming, "build-traversal.ready.json"), JsonSerializer.Serialize(manifest));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_stateDir, "failed", "build-traversal.ready.json")).Should().BeTrue();
        File.Exists(victimPath).Should().BeTrue("the traversal target must never be moved/consumed");
        File.GetLastWriteTimeUtc(victimPath).Should().Be(victimLastWrite);
        File.ReadAllBytes(victimPath).Should().Equal(victimBytes);
    }

    [Fact]
    public async Task A_path_traversal_build_id_never_touches_the_file_it_points_at()
    {
        // buildId is used to build filesystem paths in StagingWatcher (archive manifest,
        // checked before PackageValidator.Validate() runs), WorldInstaller (deployed
        // .vrcw name, written into VRChat's live Worlds directory), and ResultWriter
        // (result JSON name). Before this fix, only null/whitespace was rejected, so an
        // embedded separator could steer any of those Path.Combine calls outside the
        // intended directory once concatenated with a literal prefix such as
        // "vrc-remote-" — e.g. buildId "x/../../evil" makes WorldInstaller's
        // deployedFileName "vrc-remote-x/../../evil.vrcw", whose "vrc-remote-x" segment
        // is a harmless literal name but whose "/../../evil.vrcw" tail escapes two
        // directories above the Worlds directory (Codex code review, confidence 0.85).
        // This test plants a real file at that escape target and asserts it is left
        // completely untouched, which only holds if buildId is rejected before any of
        // those Path.Combine results is ever used for filesystem I/O.
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        var victimPath = Path.Combine(_tempDir, "evil.vrcw");
        var victimBytes = Encoding.UTF8.GetBytes("do-not-touch-me");
        File.WriteAllBytes(victimPath, victimBytes);
        var victimLastWrite = File.GetLastWriteTimeUtc(victimPath);

        const string maliciousBuildId = "x/../../evil";
        var artifactBytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        File.WriteAllBytes(Path.Combine(incoming, "quarantine.vrcw"), artifactBytes);

        var manifest = new BuildManifest
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            BuildId = maliciousBuildId,
            FileName = "quarantine.vrcw",
            Size = artifactBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(incoming, "quarantine.ready.json"), JsonSerializer.Serialize(manifest));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_stateDir, "failed", "quarantine.ready.json")).Should().BeTrue();
        File.Exists(victimPath).Should().BeTrue("the traversal target must never be moved/consumed");
        File.GetLastWriteTimeUtc(victimPath).Should().Be(victimLastWrite);
        File.ReadAllBytes(victimPath).Should().Equal(victimBytes);
    }

    [Fact]
    public async Task Recovers_unfinished_build_left_in_processing_directory()
    {
        var watcher = CreateWatcher();
        var processing = Path.Combine(_stateDir, "processing");
        Directory.CreateDirectory(processing);

        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        File.WriteAllBytes(Path.Combine(processing, "build-4.vrcw"), bytes);

        var manifest = new BuildManifest
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            BuildId = "build-4",
            FileName = "build-4.vrcw",
            Size = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(Path.Combine(processing, "build-4.ready.json"), JsonSerializer.Serialize(manifest));

        await watcher.RecoverProcessingAsync(CancellationToken.None);

        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-4.vrcw")).Should().BeTrue();
    }

    [Fact]
    public async Task Malformed_manifest_json_is_quarantined_without_throwing()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "build-bad.ready.json"), "{ not valid json");

        var act = async () => await watcher.RunOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        File.Exists(Path.Combine(_stateDir, "failed", "build-bad.ready.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Cleans_up_old_deployments_after_a_successful_deploy()
    {
        var watcher = CreateWatcher(retainBuilds: 2);

        for (var i = 0; i < 4; i++)
        {
            var bytes = Encoding.UTF8.GetBytes($"fake-world-bundle-{i}");
            WriteIncomingBuild(_stagingDir, $"build-{i}", bytes);
            await watcher.RunOnceAsync(CancellationToken.None);
            // Ensure distinct LastWriteTimeUtc ordering for the cleanup comparison.
            await Task.Delay(10);
        }

        Directory.GetFiles(_worldsDir, "vrc-remote-*.vrcw").Should().HaveCount(2);
    }

    [Fact]
    public async Task AutoLaunchVrchat_disabled_never_calls_the_client_coordinator()
    {
        var coordinator = new FakeVrchatClientCoordinator();
        var watcher = CreateWatcher(autoLaunchVrchat: false, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-no-autolaunch", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        coordinator.PrepareCalled.Should().BeFalse();
        coordinator.LaunchedWorldPaths.Should().BeEmpty();
        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-no-autolaunch.vrcw")).Should().BeTrue();
    }

    [Fact]
    public async Task AutoLaunchVrchat_enabled_calls_the_client_coordinator_before_deploying()
    {
        var coordinator = new FakeVrchatClientCoordinator { PreparationToReturn = VrchatPreparation.DeployOnly() };
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-autolaunch-ok", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        coordinator.PrepareCalled.Should().BeTrue();
        coordinator.LaunchedWorldPaths.Should().BeEmpty("a deploy to running clients launches nothing");
        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-autolaunch-ok.vrcw")).Should().BeTrue();
    }

    [Fact]
    public async Task AutoLaunchVrchat_enabled_and_readiness_failure_quarantines_the_build_without_deploying()
    {
        var coordinator = new FakeVrchatClientCoordinator
        {
            PreparationToReturn = VrchatPreparation.Failure(ErrorCode.VrchatStartTimeout, "Timed out waiting for VRChat."),
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-autolaunch-timeout", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_stateDir, "failed", "build-autolaunch-timeout.ready.json")).Should().BeTrue();

        var resultJson = await File.ReadAllTextAsync(
            Path.Combine(_stagingDir, "results", "build-autolaunch-timeout.json"));
        var result = JsonSerializer.Deserialize<BuildResult>(
            resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        result!.Status.Should().Be("failed");
        result.ErrorCode.Should().Be(ErrorCode.VrchatStartTimeout);
    }

    [Fact]
    public async Task AutoLaunchVrchat_enabled_and_readiness_throws_writes_VrchatStartFailed_without_crashing()
    {
        var coordinator = new FakeVrchatClientCoordinator
        {
            ThrowOnPrepare = new InvalidOperationException("simulated WMI failure"),
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-autolaunch-throws", Encoding.UTF8.GetBytes("fake-world-bundle"));

        var act = async () => await watcher.RunOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        Directory.GetFiles(_worldsDir).Should().BeEmpty();

        var resultJson = await File.ReadAllTextAsync(
            Path.Combine(_stagingDir, "results", "build-autolaunch-throws.json"));
        var result = JsonSerializer.Deserialize<BuildResult>(
            resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        result!.Status.Should().Be("failed");
        result.ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
    }

    [Fact]
    public async Task Ingests_through_private_state_and_leaves_nothing_in_the_share()
    {
        var watcher = CreateWatcher();
        WriteIncomingBuild(_stagingDir, "build-private", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(Path.Combine(_stagingDir, "incoming")).Should().BeEmpty();
        Directory.Exists(Path.Combine(_stagingDir, "processing")).Should().BeFalse();
        Directory.Exists(Path.Combine(_stagingDir, "archive")).Should().BeFalse();
        Directory.Exists(Path.Combine(_stagingDir, "failed")).Should().BeFalse();
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_stateDir, "archive")).Should().ContainSingle()
            .Which.Should().EndWith("build-private.ready.json", "only the manifest is archived");
    }

    [Fact]
    public async Task Replacing_the_shared_artifact_after_validation_does_not_change_what_is_deployed()
    {
        // The window between validation and install includes the VRChat readiness wait.
        // The installed bytes must come from the validated private snapshot, not from
        // anything an SMB writer can still reach (security review S15).
        var original = Encoding.UTF8.GetBytes("validated-world-bundle");
        var coordinator = new FakeVrchatClientCoordinator
        {
            OnPrepare = () => File.WriteAllText(
                Path.Combine(_stagingDir, "incoming", "build-swap.vrcw"), "attacker-world-bundle"),
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-swap", original);

        await watcher.RunOnceAsync(CancellationToken.None);

        coordinator.PrepareCalled.Should().BeTrue();
        File.ReadAllBytes(Path.Combine(_worldsDir, "vrc-remote-build-swap.vrcw")).Should().Equal(original);
    }

    [Fact]
    public async Task Rejects_an_oversized_manifest_without_claiming_it()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllText(
            Path.Combine(incoming, "build-huge.ready.json"),
            new string(' ', StagingWatcher.MaxManifestBytes + 1));

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(incoming, "build-huge.ready.json")).Should().BeFalse();
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
        ReadResult("build-huge").ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
    }

    [Fact]
    public async Task Enforces_the_size_limit_on_actual_bytes_when_the_manifest_understates_them()
    {
        var watcher = CreateWatcher(maxSize: 16);
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        var actualBytes = new byte[64];
        File.WriteAllBytes(Path.Combine(incoming, "build-big.vrcw"), actualBytes);
        var manifest = ManifestFor("build-big", new byte[8]); // claims 8 bytes
        File.WriteAllText(Path.Combine(incoming, "build-big.ready.json"), JsonSerializer.Serialize(manifest));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty("no partial copy may survive");
        Directory.GetFiles(Path.Combine(_stateDir, "failed"), "*.vrcw").Should().BeEmpty();
        File.Exists(Path.Combine(incoming, "build-big.vrcw")).Should().BeFalse();
        ReadResult("build-big").ErrorCode.Should().Be(ErrorCode.SizeExceeded);
    }

    [Fact]
    public async Task Rejects_a_manifest_with_an_unknown_field()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        File.WriteAllBytes(Path.Combine(incoming, "build-extra.vrcw"), bytes);

        var json = JsonSerializer.SerializeToNode(ManifestFor("build-extra", bytes))!;
        json["unexpected"] = "value";
        File.WriteAllText(Path.Combine(incoming, "build-extra.ready.json"), json.ToJsonString());

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_stateDir, "failed", "build-extra.ready.json")).Should().BeTrue();
        ReadResult("build-extra").ErrorCode.Should().Be(ErrorCode.ManifestInvalid);
    }

    [Fact]
    public async Task Rejects_an_invalid_declared_manifest_before_copying_the_artifact()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        File.WriteAllBytes(Path.Combine(incoming, "build-v99.vrcw"), bytes);

        var manifest = ManifestFor("build-v99", bytes);
        var futureManifest = new BuildManifest
        {
            ProtocolVersion = 99,
            BuildId = manifest.BuildId,
            FileName = manifest.FileName,
            Size = manifest.Size,
            Sha256 = manifest.Sha256,
            CreatedAtUtc = manifest.CreatedAtUtc,
        };
        File.WriteAllText(Path.Combine(incoming, "build-v99.ready.json"), JsonSerializer.Serialize(futureManifest));

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(incoming, "build-v99.vrcw")).Should().BeFalse("the shared artifact is consumed");
        Directory.GetFiles(Path.Combine(_stateDir, "failed"), "*.vrcw").Should().BeEmpty("it is never copied");
        ReadResult("build-v99").ErrorCode.Should().Be(ErrorCode.ProtocolVersionMismatch);
    }

    [Fact]
    public async Task Recovery_discards_a_stale_partial_copy_and_recopies_from_incoming()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        var processing = Path.Combine(_stateDir, "processing");
        Directory.CreateDirectory(processing);

        // State after a crash mid-copy: claimed manifest, partial snapshot, shared source still present.
        File.WriteAllText(
            Path.Combine(processing, "build-crash.ready.json"),
            JsonSerializer.Serialize(ManifestFor("build-crash", bytes)));
        File.WriteAllText(Path.Combine(processing, "build-crash.vrcw.part"), "truncated");
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllBytes(Path.Combine(incoming, "build-crash.vrcw"), bytes);

        await watcher.RecoverProcessingAsync(CancellationToken.None);

        File.ReadAllBytes(Path.Combine(_worldsDir, "vrc-remote-build-crash.vrcw")).Should().Equal(bytes);
        Directory.GetFiles(processing).Should().BeEmpty();
    }

    [Fact]
    public async Task Recovery_never_imports_a_legacy_processing_directory_from_the_share()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        var legacyProcessing = Path.Combine(_stagingDir, "processing");
        Directory.CreateDirectory(legacyProcessing);
        File.WriteAllBytes(Path.Combine(legacyProcessing, "build-legacy.vrcw"), bytes);
        File.WriteAllText(
            Path.Combine(legacyProcessing, "build-legacy.ready.json"),
            JsonSerializer.Serialize(ManifestFor("build-legacy", bytes)));

        await watcher.RecoverProcessingAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        Directory.GetFiles(legacyProcessing).Should().HaveCount(2, "legacy files are left for manual cleanup");
    }

    [Fact]
    public async Task An_unsafe_build_id_guessed_from_a_filename_never_names_a_result_file()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "unsafe build.ready.json"), "{ not valid json");

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(_stagingDir, "results", "unsafe build.json")).Should().BeFalse();
        File.Exists(Path.Combine(_stateDir, "failed", "unsafe build.ready.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Keeps_only_the_newest_RetainBuilds_failed_artifacts()
    {
        var watcher = CreateWatcher(retainBuilds: 1);
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        for (var i = 0; i < 3; i++)
        {
            var bytes = Encoding.UTF8.GetBytes($"corrupted-{i}");
            File.WriteAllBytes(Path.Combine(incoming, $"build-bad-{i}.vrcw"), bytes);
            var manifest = ManifestFor($"build-bad-{i}", Encoding.UTF8.GetBytes($"expected-{i}"));
            var mismatched = new BuildManifest
            {
                ProtocolVersion = manifest.ProtocolVersion,
                BuildId = manifest.BuildId,
                FileName = manifest.FileName,
                Size = bytes.Length,
                Sha256 = manifest.Sha256, // hash of different content
                CreatedAtUtc = manifest.CreatedAtUtc,
            };
            File.WriteAllText(
                Path.Combine(incoming, $"build-bad-{i}.ready.json"), JsonSerializer.Serialize(mismatched));

            await watcher.RunOnceAsync(CancellationToken.None);
            await Task.Delay(10);
        }

        Directory.GetFiles(Path.Combine(_stateDir, "failed"), "*.vrcw").Should().ContainSingle();
        Directory.GetFiles(Path.Combine(_stateDir, "failed"), "*.ready.json").Should().HaveCount(3);
    }

    [Fact]
    public async Task Accepts_a_manifest_exactly_at_the_size_limit()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllBytes(Path.Combine(incoming, "build-limit.vrcw"), bytes);

        var json = JsonSerializer.Serialize(ManifestFor("build-limit", bytes));
        File.WriteAllText(
            Path.Combine(incoming, "build-limit.ready.json"),
            json.PadRight(StagingWatcher.MaxManifestBytes));

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-limit.vrcw")).Should().BeTrue();
    }

    [Fact]
    public async Task Accepts_an_artifact_exactly_at_the_size_limit()
    {
        var bytes = new byte[32];
        var watcher = CreateWatcher(maxSize: bytes.Length);
        WriteIncomingBuild(_stagingDir, "build-exact", bytes);

        await watcher.RunOnceAsync(CancellationToken.None);

        File.ReadAllBytes(Path.Combine(_worldsDir, "vrc-remote-build-exact.vrcw")).Should().Equal(bytes);
    }

    [Fact]
    public async Task Two_manifests_referencing_one_artifact_consume_it_only_once()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-shared", bytes);

        var second = ManifestFor("build-shared", bytes);
        var secondManifest = new BuildManifest
        {
            ProtocolVersion = second.ProtocolVersion,
            BuildId = "build-other",
            FileName = second.FileName, // same artifact as the first manifest
            Size = second.Size,
            Sha256 = second.Sha256,
            CreatedAtUtc = second.CreatedAtUtc,
        };
        File.WriteAllText(
            Path.Combine(_stagingDir, "incoming", "build-other.ready.json"), JsonSerializer.Serialize(secondManifest));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir, "vrc-remote-*.vrcw").Should().ContainSingle();
        new[] { ReadResult("build-shared").Status, ReadResult("build-other").Status }
            .Should().BeEquivalentTo(new[] { "deployed", "failed" });
    }

    [Fact]
    public async Task Recovery_reuses_a_complete_private_snapshot_instead_of_the_shared_copy()
    {
        var watcher = CreateWatcher();
        var validated = Encoding.UTF8.GetBytes("validated-world-bundle");
        var processing = Path.Combine(_stateDir, "processing");
        Directory.CreateDirectory(processing);
        File.WriteAllText(
            Path.Combine(processing, "build-resume.ready.json"),
            JsonSerializer.Serialize(ManifestFor("build-resume", validated)));
        File.WriteAllBytes(Path.Combine(processing, "build-resume.vrcw"), validated);

        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "build-resume.vrcw"), "replaced-after-crash");

        await watcher.RecoverProcessingAsync(CancellationToken.None);

        File.ReadAllBytes(Path.Combine(_worldsDir, "vrc-remote-build-resume.vrcw")).Should().Equal(validated);
    }

    [Fact]
    public async Task RetainBuilds_zero_keeps_every_failed_artifact()
    {
        var watcher = CreateWatcher(retainBuilds: 0);
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);

        for (var i = 0; i < 2; i++)
        {
            File.WriteAllText(Path.Combine(incoming, $"build-keep-{i}.vrcw"), $"corrupted-{i}");
            var manifest = ManifestFor($"build-keep-{i}", Encoding.UTF8.GetBytes($"corrupted-{i}"));
            var mismatched = new BuildManifest
            {
                ProtocolVersion = manifest.ProtocolVersion,
                BuildId = manifest.BuildId,
                FileName = manifest.FileName,
                Size = manifest.Size,
                Sha256 = new string('0', 64),
                CreatedAtUtc = manifest.CreatedAtUtc,
            };
            File.WriteAllText(
                Path.Combine(incoming, $"build-keep-{i}.ready.json"), JsonSerializer.Serialize(mismatched));
        }

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(Path.Combine(_stateDir, "failed"), "*.vrcw").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_maximum_length_manifest_name_does_not_stall_later_builds()
    {
        // 240 + ".ready.json" = 251 characters: valid in the share, but appending
        // ".part" for the private copy would exceed the 255-character component limit.
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        var longName = new string('a', 240) + ".ready.json";
        File.WriteAllText(Path.Combine(incoming, longName), "{}");
        WriteIncomingBuild(_stagingDir, "build-after-long", Encoding.UTF8.GetBytes("fake-world-bundle"));

        var act = async () => await watcher.RunOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-after-long.vrcw")).Should().BeTrue();
        File.Exists(Path.Combine(incoming, longName)).Should().BeFalse("the long manifest is claimed, not stuck");
    }

    [Fact]
    public async Task A_symbolic_link_manifest_is_never_ingested()
    {
        var watcher = CreateWatcher();
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        File.WriteAllBytes(Path.Combine(incoming, "build-link.vrcw"), bytes);

        var target = Path.Combine(_tempDir, "private-manifest.json");
        var targetJson = JsonSerializer.Serialize(ManifestFor("build-link", bytes));
        File.WriteAllText(target, targetJson);
        File.CreateSymbolicLink(Path.Combine(incoming, "build-link.ready.json"), target);

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(incoming, "build-link.ready.json")).Should().BeFalse("the link itself is removed");
        File.ReadAllText(target).Should().Be(targetJson, "the link target is never touched");
    }

    [Fact]
    public async Task A_symbolic_link_artifact_is_never_copied()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        var target = Path.Combine(_tempDir, "private-file.bin");
        File.WriteAllBytes(target, bytes);

        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.CreateSymbolicLink(Path.Combine(incoming, "build-linkart.vrcw"), target);
        File.WriteAllText(
            Path.Combine(incoming, "build-linkart.ready.json"),
            JsonSerializer.Serialize(ManifestFor("build-linkart", bytes)));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.ReadAllBytes(target).Should().Equal(bytes);
        ReadResult("build-linkart").ErrorCode.Should().Be(ErrorCode.ArtifactNotFound);
    }

    [Fact]
    public async Task A_shared_manifest_that_cannot_be_deleted_is_not_processed_until_it_can_be()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Simulates the failed delete with Unix directory permissions.
        }

        var watcher = CreateWatcher();
        WriteIncomingBuild(_stagingDir, "build-locked", Encoding.UTF8.GetBytes("fake-world-bundle"));
        var incoming = Path.Combine(_stagingDir, "incoming");

        File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await watcher.RunOnceAsync(CancellationToken.None);
        }
        finally
        {
            File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty("the claim is withdrawn");
        File.Exists(Path.Combine(incoming, "build-locked.ready.json")).Should().BeTrue();

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-locked.vrcw")).Should().BeTrue();
        ReadResult("build-locked").Status.Should().Be("deployed");
    }

    [Fact]
    public async Task An_unexpected_error_quarantines_the_claim_and_later_builds_still_deploy()
    {
        var validator = new ThrowingPackageValidator("build-boom");
        var watcher = CreateWatcher(validator: validator);
        WriteIncomingBuild(_stagingDir, "build-boom", Encoding.UTF8.GetBytes("fake-world-bundle"));
        WriteIncomingBuild(_stagingDir, "build-fine", Encoding.UTF8.GetBytes("another-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty("no claim is stranded");
        File.Exists(Path.Combine(_stateDir, "failed", "build-boom.ready.json")).Should().BeTrue();
        File.Exists(Path.Combine(_stateDir, "failed", "build-boom.vrcw")).Should().BeTrue();
        ReadResult("build-boom").ErrorCode.Should().Be(ErrorCode.UnknownError);
        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-fine.vrcw")).Should().BeTrue();
    }

    [Fact]
    public async Task Recovery_continues_past_a_claim_that_throws()
    {
        var validator = new ThrowingPackageValidator("build-boom");
        var watcher = CreateWatcher(validator: validator);
        var processing = Path.Combine(_stateDir, "processing");
        Directory.CreateDirectory(processing);
        foreach (var buildId in new[] { "build-boom", "build-fine" })
        {
            var bytes = Encoding.UTF8.GetBytes($"bundle-{buildId}");
            File.WriteAllBytes(Path.Combine(processing, $"{buildId}.vrcw"), bytes);
            File.WriteAllText(
                Path.Combine(processing, $"{buildId}.ready.json"), JsonSerializer.Serialize(ManifestFor(buildId, bytes)));
        }

        var act = async () => await watcher.RecoverProcessingAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-fine.vrcw")).Should().BeTrue();
        File.Exists(Path.Combine(_stateDir, "failed", "build-boom.ready.json")).Should().BeTrue();
    }

    /// <summary>Throws an exception the watcher does not anticipate, for one build only.</summary>
    private sealed class ThrowingPackageValidator(string failingBuildId) : IPackageValidator
    {
        private readonly PackageValidator _inner = new();

        public ValidationResult Validate(BuildManifest manifest, string artifactPath, long maxArtifactSizeBytes) =>
            manifest.BuildId == failingBuildId
                ? throw new InvalidOperationException("simulated unexpected failure")
                : _inner.Validate(manifest, artifactPath, maxArtifactSizeBytes);
    }

    [Fact]
    public async Task A_duplicate_of_a_stranded_claim_resumes_the_private_claim_not_the_shared_copy()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        var processing = Path.Combine(_stateDir, "processing");
        Directory.CreateDirectory(processing);
        File.WriteAllText(
            Path.Combine(processing, "build-dup.ready.json"), JsonSerializer.Serialize(ManifestFor("build-dup", bytes)));

        // The shared duplicate disagrees with the private claim; only the claim is trusted.
        WriteIncomingBuild(_stagingDir, "build-dup", bytes);
        var shared = ManifestFor("build-dup", bytes);
        var tampered = new BuildManifest
        {
            ProtocolVersion = shared.ProtocolVersion,
            BuildId = shared.BuildId,
            FileName = shared.FileName,
            Size = shared.Size,
            Sha256 = new string('0', 64),
            CreatedAtUtc = shared.CreatedAtUtc,
        };
        File.WriteAllText(
            Path.Combine(_stagingDir, "incoming", "build-dup.ready.json"), JsonSerializer.Serialize(tampered));

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(_stagingDir, "incoming", "build-dup.ready.json")).Should().BeFalse();
        Directory.GetFiles(processing).Should().BeEmpty("the stranded claim is resumed without a restart");
        File.ReadAllBytes(Path.Combine(_worldsDir, "vrc-remote-build-dup.vrcw")).Should().Equal(bytes);
        ReadResult("build-dup").Status.Should().Be("deployed");
    }

    // === Multi-client launch (multi-client plan sections 8, 11.3 and 13.4) ===

    private string DeployedPath(string buildId) => Path.Combine(_worldsDir, $"vrc-remote-{buildId}.vrcw");

    private string ArchivedManifest(string buildId) => Path.Combine(_stateDir, "archive", $"{buildId}.ready.json");

    [Fact]
    public async Task Cold_start_deploys_and_archives_before_launching_every_client_with_the_deployed_file()
    {
        var monitor = new FakeVrchatProcessMonitor();
        var launcher = new FakeVrchatLauncher();
        var stateAtEachLaunch = new List<(bool Deployed, bool Archived, bool ResultWritten)>();
        launcher.OnLaunch = _ => stateAtEachLaunch.Add((
            File.Exists(DeployedPath("build-cold")),
            File.Exists(ArchivedManifest("build-cold")),
            File.Exists(Path.Combine(_stagingDir, "results", "build-cold.json"))));
        var watcher = CreateWatcher(
            autoLaunchVrchat: true, clientCoordinator: CreateCoordinator(monitor, launcher, clientCount: 2));
        WriteIncomingBuild(_stagingDir, "build-cold", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        stateAtEachLaunch.Should().Equal((true, true, false), (true, true, false));
        launcher.Requests.Should().AllSatisfy(r => r.WorldPath.Should().Be(Path.GetFullPath(DeployedPath("build-cold"))));
        launcher.Requests.Select(r => r.RoomId).Distinct().Should().ContainSingle();
        ReadResult("build-cold").Status.Should().Be("deployed");
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_client_cold_start_does_not_wait_for_the_settle_delay()
    {
        var monitor = new FakeVrchatProcessMonitor();
        var launcher = new FakeVrchatLauncher();
        var coordinator = CreateCoordinator(monitor, launcher, clientCount: null, settleDelay: TimeSpan.FromMinutes(5));
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-one", Encoding.UTF8.GetBytes("fake-world-bundle"));

        var runTask = watcher.RunOnceAsync(CancellationToken.None);
        var finished = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(runTask, "the clients open the deployed world through --url");
        launcher.Requests.Should().ContainSingle();
        ReadResult("build-one").Status.Should().Be("deployed");
    }

    [Fact]
    public async Task A_build_right_after_a_cold_start_waits_for_the_new_clients_and_launches_nothing()
    {
        var settleDelay = TimeSpan.FromMilliseconds(400);
        var monitor = new FakeVrchatProcessMonitor();
        var launcher = new FakeVrchatLauncher();
        DateTime? lastLaunchUtc = null;
        launcher.OnLaunch = pid =>
        {
            lastLaunchUtc = DateTime.UtcNow;
            monitor.AddClient(pid, lastLaunchUtc);
        };
        var installer = new RecordingWorldInstaller();
        var watcher = CreateWatcher(
            autoLaunchVrchat: true,
            installer: installer,
            clientCoordinator: CreateCoordinator(monitor, launcher, clientCount: 2, settleDelay: settleDelay));

        // Both builds are picked up by the same scan, as in StagingWatcher.RunOnceAsync.
        WriteIncomingBuild(_stagingDir, "build-a", Encoding.UTF8.GetBytes("bundle-a"));
        WriteIncomingBuild(_stagingDir, "build-b", Encoding.UTF8.GetBytes("bundle-b"));

        await watcher.RunOnceAsync(CancellationToken.None);

        // Directory enumeration order decides which build cold-starts.
        launcher.Requests.Should().HaveCount(2, "only the first build cold-starts");
        installer.Installs.Select(i => i.BuildId).Should().BeEquivalentTo("build-a", "build-b");
        (installer.Installs[1].AtUtc - lastLaunchUtc!.Value).Should().BeGreaterThanOrEqualTo(settleDelay);
        ReadResult("build-a").Status.Should().Be("deployed");
        ReadResult("build-b").Status.Should().Be("deployed");
    }

    [Fact]
    public async Task A_build_right_after_a_cold_start_is_not_deployed_before_the_new_clients_settle()
    {
        // Clients that never settle within the timeout: without the settle check the second
        // build would be deployed after two identical polls.
        var monitor = new FakeVrchatProcessMonitor();
        var launcher = new FakeVrchatLauncher();
        launcher.OnLaunch = pid => monitor.AddClient(pid, DateTime.UtcNow.AddHours(1));
        var installer = new RecordingWorldInstaller();
        var watcher = CreateWatcher(
            autoLaunchVrchat: true,
            installer: installer,
            clientCoordinator: CreateCoordinator(monitor, launcher, clientCount: 2, startupTimeoutSeconds: 1));
        WriteIncomingBuild(_stagingDir, "build-a", Encoding.UTF8.GetBytes("bundle-a"));
        WriteIncomingBuild(_stagingDir, "build-b", Encoding.UTF8.GetBytes("bundle-b"));

        await watcher.RunOnceAsync(CancellationToken.None);

        // Directory enumeration order decides which build cold-starts.
        var coldStarted = installer.Installs.Should().ContainSingle().Subject.BuildId;
        var other = coldStarted == "build-a" ? "build-b" : "build-a";
        ReadResult(coldStarted).Status.Should().Be("deployed");
        ReadResult(other).ErrorCode.Should().Be(ErrorCode.VrchatStartTimeout);
        launcher.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Running_clients_get_the_build_without_any_launch()
    {
        var monitor = new FakeVrchatProcessMonitor();
        monitor.AddClient(1234, DateTime.UtcNow.AddMinutes(-5));
        monitor.AddClient(1235, DateTime.UtcNow.AddMinutes(-5));
        var launcher = new FakeVrchatLauncher();
        var watcher = CreateWatcher(
            autoLaunchVrchat: true, clientCoordinator: CreateCoordinator(monitor, launcher, clientCount: 2));
        WriteIncomingBuild(_stagingDir, "build-warm", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        launcher.LaunchCalled.Should().BeFalse();
        File.Exists(DeployedPath("build-warm")).Should().BeTrue();
        ReadResult("build-warm").Status.Should().Be("deployed");
    }

    [Fact]
    public async Task Reload_deploys_neither_show_a_notice_nor_wake_the_Log_Viewer()
    {
        // Only Relaunch closes clients; Reload keeps the Log Viewer's 5 s polls as they were.
        var monitor = new FakeVrchatProcessMonitor();
        var launcher = new FakeVrchatLauncher();
        var noticeAtLaunch = new List<string?>();
        launcher.OnLaunch = pid =>
        {
            noticeAtLaunch.Add(_logNotice.Message);
            monitor.AddClient(pid, DateTime.UtcNow.AddMinutes(-5));
        };
        var watcher = CreateWatcher(
            autoLaunchVrchat: true, clientCoordinator: CreateCoordinator(monitor, launcher, clientCount: 2));

        WriteIncomingBuild(_stagingDir, "build-cold", Encoding.UTF8.GetBytes("bundle-cold"));
        await watcher.RunOnceAsync(CancellationToken.None);
        WriteIncomingBuild(_stagingDir, "build-warm", Encoding.UTF8.GetBytes("bundle-warm"));
        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("build-cold").Status.Should().Be("deployed");
        ReadResult("build-warm").Status.Should().Be("deployed");
        launcher.Requests.Should().HaveCount(2, "the second build reloads the running clients");
        noticeAtLaunch.Should().Equal(new string?[] { null, null });
        _logNotice.Message.Should().BeNull();
        (await _logNotice.WaitForChangeAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_VRChat_that_cannot_be_checked_fails_the_build_without_deploying_or_launching()
    {
        var monitor = new FakeVrchatProcessMonitor { EnumerationError = "access denied" };
        var launcher = new FakeVrchatLauncher();
        var watcher = CreateWatcher(
            autoLaunchVrchat: true, clientCoordinator: CreateCoordinator(monitor, launcher, clientCount: 2));
        WriteIncomingBuild(_stagingDir, "build-unmanaged", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        launcher.LaunchCalled.Should().BeFalse();
        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_stateDir, "failed", "build-unmanaged.ready.json")).Should().BeTrue();
        ReadResult("build-unmanaged").ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
    }

    [Fact]
    public async Task A_build_that_fails_validation_neither_deploys_nor_launches()
    {
        var launcher = new FakeVrchatLauncher();
        var watcher = CreateWatcher(
            autoLaunchVrchat: true,
            clientCoordinator: CreateCoordinator(new FakeVrchatProcessMonitor(), launcher, clientCount: 2));
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        var incoming = Path.Combine(_stagingDir, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllBytes(Path.Combine(incoming, "build-badhash.vrcw"), bytes);
        var manifest = ManifestFor("build-badhash", bytes);
        File.WriteAllText(
            Path.Combine(incoming, "build-badhash.ready.json"),
            JsonSerializer.Serialize(new BuildManifest
            {
                ProtocolVersion = manifest.ProtocolVersion,
                BuildId = manifest.BuildId,
                FileName = manifest.FileName,
                Size = manifest.Size,
                Sha256 = new string('0', 64),
                CreatedAtUtc = manifest.CreatedAtUtc,
            }));

        await watcher.RunOnceAsync(CancellationToken.None);

        launcher.LaunchCalled.Should().BeFalse();
        Directory.GetFiles(_worldsDir).Should().BeEmpty();
        ReadResult("build-badhash").ErrorCode.Should().Be(ErrorCode.HashMismatch);
    }

    [Fact]
    public async Task A_failed_install_launches_nothing()
    {
        var launcher = new FakeVrchatLauncher();
        var watcher = CreateWatcher(
            autoLaunchVrchat: true,
            installer: new ThrowingWorldInstaller(),
            clientCoordinator: CreateCoordinator(new FakeVrchatProcessMonitor(), launcher, clientCount: 2));
        WriteIncomingBuild(_stagingDir, "build-noinstall", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        launcher.LaunchCalled.Should().BeFalse();
        File.Exists(Path.Combine(_stateDir, "failed", "build-noinstall.ready.json")).Should().BeTrue();
        ReadResult("build-noinstall").ErrorCode.Should().Be(ErrorCode.DeployFailed);
    }

    [Fact]
    public async Task A_failed_launch_reports_VrchatStartFailed_and_keeps_the_build_archived()
    {
        var launcher = new FakeVrchatLauncher { FailingLaunchIndexes = [1] };
        var watcher = CreateWatcher(
            autoLaunchVrchat: true,
            clientCoordinator: CreateCoordinator(new FakeVrchatProcessMonitor(), launcher, clientCount: 3));
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-halfstart", bytes);

        await watcher.RunOnceAsync(CancellationToken.None);

        var result = ReadResult("build-halfstart");
        result.ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        result.ErrorMessage.Should().Contain("Launched 1 of 3");
        File.Exists(ArchivedManifest("build-halfstart")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty("an archived build is never quarantined");
        File.Exists(DeployedPath("build-halfstart")).Should().BeTrue();

        // Sending the same build again neither deploys it again nor replaces its result.
        WriteIncomingBuild(_stagingDir, "build-halfstart", bytes);
        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("build-halfstart").ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        launcher.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_exception_while_launching_reports_VrchatStartFailed_and_keeps_the_build_archived()
    {
        var coordinator = new FakeVrchatClientCoordinator
        {
            PreparationToReturn = VrchatPreparation.ColdStart(),
            ThrowOnLaunch = new InvalidOperationException("simulated launcher bug"),
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-launchthrows", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        ReadResult("build-launchthrows").ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        File.Exists(ArchivedManifest("build-launchthrows")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_archive_that_cannot_be_written_launches_nothing()
    {
        // A directory where the archived manifest belongs makes the move fail on every OS.
        Directory.CreateDirectory(ArchivedManifest("build-noarchive"));
        var coordinator = new FakeVrchatClientCoordinator { PreparationToReturn = VrchatPreparation.ColdStart() };
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-noarchive", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        coordinator.LaunchedWorldPaths.Should().BeEmpty();
        ReadResult("build-noarchive").ErrorCode.Should().Be(ErrorCode.UnknownError);
        File.Exists(Path.Combine(_stateDir, "failed", "build-noarchive.ready.json")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_stateDir, "processing")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_that_cannot_be_written_after_the_archive_never_quarantines_the_build()
    {
        var coordinator = new FakeVrchatClientCoordinator { PreparationToReturn = VrchatPreparation.ColdStart() };
        var watcher = CreateWatcher(
            autoLaunchVrchat: true,
            clientCoordinator: coordinator,
            resultWriter: new FailingSuccessResultWriter());
        WriteIncomingBuild(_stagingDir, "build-noresult", Encoding.UTF8.GetBytes("fake-world-bundle"));

        var act = async () => await watcher.RunOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        coordinator.LaunchedWorldPaths.Should().ContainSingle();
        File.Exists(ArchivedManifest("build-noresult")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
        ReadResult("build-noresult").ErrorCode.Should().Be(ErrorCode.UnknownError);
    }

    [Fact]
    public async Task Stopping_during_the_launch_interval_never_redeploys_the_build_after_a_restart()
    {
        using var cts = new CancellationTokenSource();
        var monitor = new FakeVrchatProcessMonitor();
        var launcher = new FakeVrchatLauncher();
        launcher.OnLaunch = pid => monitor.AddClient(pid, DateTime.UtcNow.AddMinutes(-5));
        var installer = new RecordingWorldInstaller();
        var coordinator = CreateCoordinator(
            monitor,
            launcher,
            clientCount: 2,
            launchDelay: (_, token) =>
            {
                cts.Cancel(); // the Bridge is stopped between the first and second client
                return Task.Delay(Timeout.Infinite, token);
            });
        var watcher = CreateWatcher(autoLaunchVrchat: true, installer: installer, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-stopped", Encoding.UTF8.GetBytes("fake-world-bundle"));

        var act = async () => await watcher.RunOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        launcher.Requests.Should().ContainSingle();
        File.Exists(Path.Combine(_stagingDir, "results", "build-stopped.json")).Should().BeFalse(
            "no result is written; Unity reports RESULT_TIMEOUT");

        var restarted = CreateWatcher(
            autoLaunchVrchat: true,
            installer: installer,
            clientCoordinator: CreateCoordinator(monitor, launcher, clientCount: 2));
        await restarted.RecoverProcessingAsync(CancellationToken.None);
        await restarted.RunOnceAsync(CancellationToken.None);

        installer.Installs.Should().ContainSingle("the archived build is not deployed again");
        launcher.Requests.Should().ContainSingle("the client that did start is left as it is");
    }

    [Fact]
    public async Task Stopping_during_the_install_launches_nothing_and_never_redeploys_after_a_restart()
    {
        using var cts = new CancellationTokenSource();
        var launcher = new FakeVrchatLauncher();
        var installer = new RecordingWorldInstaller { OnInstall = cts.Cancel };
        var watcher = CreateWatcher(
            autoLaunchVrchat: true,
            installer: installer,
            clientCoordinator: CreateCoordinator(new FakeVrchatProcessMonitor(), launcher, clientCount: 1));
        WriteIncomingBuild(_stagingDir, "build-stop-install", Encoding.UTF8.GetBytes("fake-world-bundle"));

        var act = async () => await watcher.RunOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        launcher.LaunchCalled.Should().BeFalse("the install and archive do not look at the token, the launch does");
        File.Exists(ArchivedManifest("build-stop-install")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_stagingDir, "results")).Should().BeEmpty("neither a result nor its .tmp");

        var restarted = CreateWatcher(
            autoLaunchVrchat: true,
            installer: installer,
            clientCoordinator: CreateCoordinator(new FakeVrchatProcessMonitor(), launcher, clientCount: 1));
        await restarted.RecoverProcessingAsync(CancellationToken.None);
        await restarted.RunOnceAsync(CancellationToken.None);

        installer.Installs.Should().ContainSingle();
        launcher.LaunchCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Bridge_stopping_after_the_launch_publishes_no_result(bool launchSucceeded)
    {
        using var cts = new CancellationTokenSource();
        var coordinator = new FakeVrchatClientCoordinator
        {
            PreparationToReturn = VrchatPreparation.ColdStart(),
            LaunchOutcomeToReturn = launchSucceeded
                ? VrchatLaunchOutcome.Success()
                : VrchatLaunchOutcome.Failure("simulated launch failure"),
            OnLaunch = cts.Cancel,
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-stop-publish", Encoding.UTF8.GetBytes("fake-world-bundle"));

        var act = async () => await watcher.RunOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(Path.Combine(_stagingDir, "results", "build-stop-publish.json")).Should().BeFalse();
        File.Exists(ArchivedManifest("build-stop-publish")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_stateDir, "failed")).Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_after_a_cold_start_never_removes_the_world_passed_to_url()
    {
        var newerLooking = Path.Combine(_worldsDir, "vrc-remote-other.vrcw");
        File.WriteAllText(newerLooking, "other");
        File.SetLastWriteTimeUtc(newerLooking, DateTime.UtcNow.AddDays(1));
        var coordinator = new FakeVrchatClientCoordinator { PreparationToReturn = VrchatPreparation.ColdStart() };
        var watcher = CreateWatcher(retainBuilds: 1, autoLaunchVrchat: true, clientCoordinator: coordinator);
        WriteIncomingBuild(_stagingDir, "build-keep", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        var passedToUrl = coordinator.LaunchedWorldPaths.Should().ContainSingle().Subject;
        passedToUrl.Should().Be(Path.GetFullPath(DeployedPath("build-keep")));
        File.Exists(passedToUrl).Should().BeTrue();
        File.Exists(newerLooking).Should().BeFalse();
    }

    /// <summary>Records every install in order, so installing the same build twice shows up.</summary>
    private sealed class RecordingWorldInstaller : IWorldInstaller
    {
        private readonly WorldInstaller _inner = new();

        public List<(string BuildId, DateTime AtUtc)> Installs { get; } = [];

        /// <summary>Runs after the file is in place, before StagingWatcher continues.</summary>
        public Action? OnInstall { get; set; }

        public string Install(string validatedArtifactPath, string buildId, string worldsDirectory)
        {
            var name = _inner.Install(validatedArtifactPath, buildId, worldsDirectory);
            Installs.Add((buildId, DateTime.UtcNow));
            OnInstall?.Invoke();
            return name;
        }
    }

    private sealed class ThrowingWorldInstaller : IWorldInstaller
    {
        public string Install(string validatedArtifactPath, string buildId, string worldsDirectory) =>
            throw new IOException("simulated disk full");
    }

    /// <summary>Fails every success result, as a share that refuses writes would.</summary>
    private sealed class FailingSuccessResultWriter : IResultWriter
    {
        private readonly ResultWriter _inner = new();

        public void WriteSuccess(string buildId, string deployedFileName, string sha256, string resultsDirectory) =>
            throw new IOException("simulated share failure");

        public void WriteFailure(string buildId, string errorCode, string errorMessage, string resultsDirectory) =>
            _inner.WriteFailure(buildId, errorCode, errorMessage, resultsDirectory);
    }
}

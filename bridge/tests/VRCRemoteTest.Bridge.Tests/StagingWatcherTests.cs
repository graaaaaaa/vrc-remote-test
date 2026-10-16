using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
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

    private StagingWatcher CreateWatcher(
        long maxSize = 1024,
        int retainBuilds = 10,
        bool autoLaunchVrchat = false,
        IVrchatReadinessCoordinator? readinessCoordinator = null,
        IPackageValidator? validator = null) =>
        new(
            Options.Create(new BridgeOptions
            {
                StagingDirectory = _stagingDir,
                StateDirectory = _stateDir,
                VrchatWorldsDirectory = _worldsDir,
                MaxArtifactSizeBytes = maxSize,
                RetainBuilds = retainBuilds,
                AutoLaunchVrchat = autoLaunchVrchat,
            }),
            validator ?? new PackageValidator(),
            new WorldInstaller(),
            new ResultWriter(),
            new CleanupService(),
            readinessCoordinator ?? new FakeVrchatReadinessCoordinator(),
            NullLogger<StagingWatcher>.Instance);

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

    private BuildResult ReadResult(string buildId)
    {
        var resultJson = File.ReadAllText(Path.Combine(_stagingDir, "results", $"{buildId}.json"));
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
    public async Task Does_not_reprocess_an_already_archived_build()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-2", bytes);
        await watcher.RunOnceAsync(CancellationToken.None);

        // Simulate the same build id arriving a second time, with different bytes.
        WriteIncomingBuild(_stagingDir, "build-2", Encoding.UTF8.GetBytes("different-bundle"));
        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir, "vrc-remote-build-2*.vrcw").Should().HaveCount(1);
        File.ReadAllBytes(Path.Combine(_worldsDir, "vrc-remote-build-2.vrcw")).Should().Equal(bytes);
        Directory.GetFiles(Path.Combine(_stagingDir, "incoming")).Should().BeEmpty("the duplicate is consumed");
        ReadResult("build-2").ErrorCode.Should().Be(ErrorCode.BuildAlreadyProcessed);
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
    public async Task AutoLaunchVrchat_disabled_never_calls_the_readiness_coordinator()
    {
        var readiness = new FakeVrchatReadinessCoordinator();
        var watcher = CreateWatcher(autoLaunchVrchat: false, readinessCoordinator: readiness);
        WriteIncomingBuild(_stagingDir, "build-no-autolaunch", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        readiness.EnsureReadyCalled.Should().BeFalse();
        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-no-autolaunch.vrcw")).Should().BeTrue();
    }

    [Fact]
    public async Task AutoLaunchVrchat_enabled_calls_the_readiness_coordinator_before_deploying()
    {
        var readiness = new FakeVrchatReadinessCoordinator { ResultToReturn = ReadinessResult.Ready() };
        var watcher = CreateWatcher(autoLaunchVrchat: true, readinessCoordinator: readiness);
        WriteIncomingBuild(_stagingDir, "build-autolaunch-ok", Encoding.UTF8.GetBytes("fake-world-bundle"));

        await watcher.RunOnceAsync(CancellationToken.None);

        readiness.EnsureReadyCalled.Should().BeTrue();
        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-autolaunch-ok.vrcw")).Should().BeTrue();
    }

    [Fact]
    public async Task AutoLaunchVrchat_enabled_and_readiness_failure_quarantines_the_build_without_deploying()
    {
        var readiness = new FakeVrchatReadinessCoordinator
        {
            ResultToReturn = ReadinessResult.Failure(ErrorCode.VrchatStartTimeout, "Timed out waiting for VRChat."),
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, readinessCoordinator: readiness);
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
        var readiness = new FakeVrchatReadinessCoordinator
        {
            ThrowOnEnsureReady = new InvalidOperationException("simulated WMI failure"),
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, readinessCoordinator: readiness);
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
        var readiness = new FakeVrchatReadinessCoordinator
        {
            OnEnsureReady = () => File.WriteAllText(
                Path.Combine(_stagingDir, "incoming", "build-swap.vrcw"), "attacker-world-bundle"),
        };
        var watcher = CreateWatcher(autoLaunchVrchat: true, readinessCoordinator: readiness);
        WriteIncomingBuild(_stagingDir, "build-swap", original);

        await watcher.RunOnceAsync(CancellationToken.None);

        readiness.EnsureReadyCalled.Should().BeTrue();
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
}

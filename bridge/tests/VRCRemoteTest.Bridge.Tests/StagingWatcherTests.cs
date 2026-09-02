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
    private readonly string _worldsDir;

    public StagingWatcherTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-watcher-").FullName;
        _stagingDir = Path.Combine(_tempDir, "staging");
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
        IVrchatReadinessCoordinator? readinessCoordinator = null) =>
        new(
            Options.Create(new BridgeOptions
            {
                StagingDirectory = _stagingDir,
                VrchatWorldsDirectory = _worldsDir,
                MaxArtifactSizeBytes = maxSize,
                RetainBuilds = retainBuilds,
                AutoLaunchVrchat = autoLaunchVrchat,
            }),
            new PackageValidator(),
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

        var manifest = new BuildManifest
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            BuildId = buildId,
            FileName = artifactName,
            Size = artifactBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        File.WriteAllText(Path.Combine(incoming, $"{buildId}.ready.json"), JsonSerializer.Serialize(manifest));
    }

    [Fact]
    public async Task Deploys_a_valid_build_and_archives_it()
    {
        var watcher = CreateWatcher();
        var bytes = Encoding.UTF8.GetBytes("fake-world-bundle");
        WriteIncomingBuild(_stagingDir, "build-1", bytes);

        await watcher.RunOnceAsync(CancellationToken.None);

        File.Exists(Path.Combine(_worldsDir, "vrc-remote-build-1.vrcw")).Should().BeTrue();
        File.Exists(Path.Combine(_stagingDir, "archive", "build-1.ready.json")).Should().BeTrue();
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

        // Simulate the exact same build arriving a second time.
        WriteIncomingBuild(_stagingDir, "build-2", bytes);
        await watcher.RunOnceAsync(CancellationToken.None);

        Directory.GetFiles(_worldsDir, "vrc-remote-build-2*.vrcw").Should().HaveCount(1);
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
        File.Exists(Path.Combine(_stagingDir, "failed", "build-3.ready.json")).Should().BeTrue();

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
        File.Exists(Path.Combine(_stagingDir, "failed", "build-evil.ready.json")).Should().BeTrue();
    }

    [Fact]
    public async Task Recovers_unfinished_build_left_in_processing_directory()
    {
        var watcher = CreateWatcher();
        var processing = Path.Combine(_stagingDir, "processing");
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
        File.Exists(Path.Combine(_stagingDir, "failed", "build-bad.ready.json")).Should().BeTrue();
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
        File.Exists(Path.Combine(_stagingDir, "failed", "build-autolaunch-timeout.ready.json")).Should().BeTrue();

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
}

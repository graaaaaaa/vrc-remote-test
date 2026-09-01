using FluentAssertions;
using VRCRemoteTest.Bridge.Deployment;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class WorldInstallerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorldInstaller _installer = new();

    public WorldInstallerTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-installer-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Installs_artifact_with_unique_prefixed_filename()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        var worldsDir = Path.Combine(_tempDir, "worlds");
        Directory.CreateDirectory(sourceDir);

        var sourcePath = Path.Combine(sourceDir, "build.vrcw");
        File.WriteAllText(sourcePath, "fake-world-bundle");

        var deployedFileName = _installer.Install(sourcePath, "20260901T112522481Z-a91f02cc", worldsDir);

        deployedFileName.Should().Be("vrc-remote-20260901T112522481Z-a91f02cc.vrcw");
        var finalPath = Path.Combine(worldsDir, deployedFileName);
        File.Exists(finalPath).Should().BeTrue();
        File.ReadAllText(finalPath).Should().Be("fake-world-bundle");
    }

    [Fact]
    public void Does_not_leave_temp_file_behind_after_install()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        var worldsDir = Path.Combine(_tempDir, "worlds");
        Directory.CreateDirectory(sourceDir);
        var sourcePath = Path.Combine(sourceDir, "build.vrcw");
        File.WriteAllText(sourcePath, "fake-world-bundle");

        _installer.Install(sourcePath, "buildid-1", worldsDir);

        Directory.GetFiles(worldsDir).Should().ContainSingle()
            .Which.Should().Be(Path.Combine(worldsDir, "vrc-remote-buildid-1.vrcw"));
    }

    [Fact]
    public void Creates_worlds_directory_if_missing()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        var worldsDir = Path.Combine(_tempDir, "does-not-exist-yet");
        Directory.CreateDirectory(sourceDir);
        var sourcePath = Path.Combine(sourceDir, "build.vrcw");
        File.WriteAllText(sourcePath, "data");

        var act = () => _installer.Install(sourcePath, "buildid-1", worldsDir);

        act.Should().NotThrow();
        Directory.Exists(worldsDir).Should().BeTrue();
    }
}

using FluentAssertions;
using VRCRemoteTest.Bridge.Deployment;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class CleanupServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CleanupService _cleanup = new();

    public CleanupServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-cleanup-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private void CreateFile(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "data");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    [Fact]
    public void Retains_only_the_newest_N_vrc_remote_builds()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 15; i++)
        {
            CreateFile($"vrc-remote-build-{i:D2}.vrcw", now.AddMinutes(-i));
        }

        _cleanup.Cleanup(_tempDir, retainBuilds: 10);

        Directory.GetFiles(_tempDir, "vrc-remote-*.vrcw").Should().HaveCount(10);
        File.Exists(Path.Combine(_tempDir, "vrc-remote-build-00.vrcw")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "vrc-remote-build-14.vrcw")).Should().BeFalse();
    }

    [Fact]
    public void Never_deletes_files_outside_the_vrc_remote_naming_pattern()
    {
        CreateFile("some-other-world.vrcw", DateTime.UtcNow.AddDays(-30));
        for (var i = 0; i < 12; i++)
        {
            CreateFile($"vrc-remote-build-{i:D2}.vrcw", DateTime.UtcNow.AddMinutes(-i));
        }

        _cleanup.Cleanup(_tempDir, retainBuilds: 10);

        File.Exists(Path.Combine(_tempDir, "some-other-world.vrcw")).Should().BeTrue();
    }

    [Fact]
    public void Does_nothing_when_retain_builds_is_zero_or_negative()
    {
        CreateFile("vrc-remote-build-01.vrcw", DateTime.UtcNow);

        _cleanup.Cleanup(_tempDir, retainBuilds: 0);

        File.Exists(Path.Combine(_tempDir, "vrc-remote-build-01.vrcw")).Should().BeTrue();
    }

    [Fact]
    public void Does_nothing_when_directory_does_not_exist()
    {
        var act = () => _cleanup.Cleanup(Path.Combine(_tempDir, "missing"), retainBuilds: 10);

        act.Should().NotThrow();
    }
}

using FluentAssertions;
using VRCRemoteTest.Bridge.Configuration;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class BridgeOptionsValidatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BridgeOptionsValidator _validator = new();

    public BridgeOptionsValidatorTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-options-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Succeeds_when_worlds_directory_exists_and_options_are_valid()
    {
        var options = new BridgeOptions
        {
            StagingDirectory = Path.Combine(_tempDir, "staging"),
            VrchatWorldsDirectory = _tempDir,
            MaxArtifactSizeBytes = 1024,
            RetainBuilds = 10,
        };

        var result = _validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_worlds_directory_does_not_exist()
    {
        var options = new BridgeOptions
        {
            StagingDirectory = Path.Combine(_tempDir, "staging"),
            VrchatWorldsDirectory = Path.Combine(_tempDir, "does-not-exist"),
            MaxArtifactSizeBytes = 1024,
            RetainBuilds = 10,
        };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("does not exist"));
    }

    [Fact]
    public void Fails_when_staging_directory_is_blank()
    {
        var options = new BridgeOptions
        {
            StagingDirectory = string.Empty,
            VrchatWorldsDirectory = _tempDir,
            MaxArtifactSizeBytes = 1024,
            RetainBuilds = 10,
        };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("StagingDirectory"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fails_when_max_artifact_size_is_not_positive(long maxSize)
    {
        var options = new BridgeOptions
        {
            StagingDirectory = Path.Combine(_tempDir, "staging"),
            VrchatWorldsDirectory = _tempDir,
            MaxArtifactSizeBytes = maxSize,
            RetainBuilds = 10,
        };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("MaxArtifactSizeBytes"));
    }
}

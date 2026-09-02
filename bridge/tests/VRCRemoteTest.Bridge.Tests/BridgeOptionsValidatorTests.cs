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

    private BridgeOptions ValidBaseOptions() => new()
    {
        StagingDirectory = Path.Combine(_tempDir, "staging"),
        VrchatWorldsDirectory = _tempDir,
        MaxArtifactSizeBytes = 1024,
        RetainBuilds = 10,
    };

    [Fact]
    public void AutoLaunch_disabled_does_not_require_VrchatExecutable()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = false;
        options.VrchatExecutable = string.Empty;

        var result = _validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void AutoLaunch_enabled_fails_when_VrchatExecutable_is_blank()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = string.Empty;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatExecutable must be configured"));
    }

    [Fact]
    public void AutoLaunch_enabled_fails_for_relative_path()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = @"VRChat\VRChat.exe";

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("absolute path"));
    }

    [Fact]
    public void AutoLaunch_enabled_fails_for_unc_path()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = @"\\server\share\VRChat.exe";

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("UNC"));
    }

    [Fact]
    public void AutoLaunch_enabled_fails_for_non_exe_extension()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = Path.Combine(_tempDir, "VRChat.bat");
        File.WriteAllText(options.VrchatExecutable, "not vrchat");

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains(".exe extension"));
    }

    [Fact]
    public void AutoLaunch_enabled_fails_when_basename_is_not_VRChat_exe()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = Path.Combine(_tempDir, "NotVRChat.exe");
        File.WriteAllText(options.VrchatExecutable, "not vrchat");

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("must point to VRChat.exe"));
    }

    [Fact]
    public void AutoLaunch_enabled_fails_when_executable_does_not_exist()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = Path.Combine(_tempDir, "VRChat.exe");

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("does not exist"));
    }

    [Fact]
    public void AutoLaunch_enabled_succeeds_for_valid_VRChat_exe_path()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = Path.Combine(_tempDir, "VRChat.exe");
        File.WriteAllText(options.VrchatExecutable, "fake exe bytes");

        var result = _validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Desktop")]
    [InlineData("desktop")]
    [InlineData("VR")]
    [InlineData("vr")]
    public void VrchatMode_accepts_known_values_case_insensitively(string mode)
    {
        var options = ValidBaseOptions();
        options.VrchatMode = mode;

        var result = _validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void VrchatMode_rejects_unknown_value()
    {
        var options = ValidBaseOptions();
        options.VrchatMode = "Mobile";

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatMode"));
    }

    [Theory]
    [InlineData(44)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fails_when_VrchatStartupTimeoutSeconds_is_below_the_minimum(int timeoutSeconds)
    {
        var options = ValidBaseOptions();
        options.VrchatStartupTimeoutSeconds = timeoutSeconds;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatStartupTimeoutSeconds"));
    }

    [Fact]
    public void Succeeds_at_the_minimum_VrchatStartupTimeoutSeconds()
    {
        var options = ValidBaseOptions();
        options.VrchatStartupTimeoutSeconds = 45;

        var result = _validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }
}

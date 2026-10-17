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
            StateDirectory = Path.Combine(_tempDir, "state"),
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
            StateDirectory = Path.Combine(_tempDir, "state"),
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
            StateDirectory = Path.Combine(_tempDir, "state"),
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
        StateDirectory = Path.Combine(_tempDir, "state"),
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
    [InlineData(59)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fails_when_VrchatStartupTimeoutSeconds_is_below_the_minimum(int timeoutSeconds)
    {
        // Default VrchatStartupSettleDelaySeconds is 30, so the floor is 30+30=60.
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
        options.VrchatStartupTimeoutSeconds = 60;

        var result = _validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fails_when_VrchatStartupSettleDelaySeconds_is_not_positive(int settleDelaySeconds)
    {
        var options = ValidBaseOptions();
        options.VrchatStartupSettleDelaySeconds = settleDelaySeconds;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatStartupSettleDelaySeconds"));
    }

    [Fact]
    public void VrchatStartupTimeoutSeconds_minimum_scales_with_a_custom_VrchatStartupSettleDelaySeconds()
    {
        var options = ValidBaseOptions();
        options.VrchatStartupSettleDelaySeconds = 45;
        options.VrchatStartupTimeoutSeconds = 74; // 45 + 30 - 1

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatStartupTimeoutSeconds"));

        options.VrchatStartupTimeoutSeconds = 75; // 45 + 30
        result = _validator.Validate(name: null, options);
        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void VrchatClientCount_accepts_one_to_four(int clientCount)
    {
        var options = ValidBaseOptions();
        options.VrchatClientCount = clientCount;

        _validator.Validate(name: null, options).Succeeded.Should().BeTrue();
        options.ResolvedVrchatClientCount.Should().Be(clientCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void VrchatClientCount_rejects_values_outside_one_to_four(int clientCount)
    {
        var options = ValidBaseOptions();
        options.VrchatClientCount = clientCount;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatClientCount"));
    }

    [Fact]
    public void An_omitted_VrchatClientCount_means_one_client()
    {
        var options = ValidBaseOptions();

        _validator.Validate(name: null, options).Succeeded.Should().BeTrue();
        options.VrchatClientCount.Should().BeNull();
        options.ResolvedVrchatClientCount.Should().Be(1);
    }

    [Fact]
    public void A_VrchatClientCount_above_one_is_allowed_without_AutoLaunchVrchat()
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = false;
        options.VrchatClientCount = 2;

        _validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_when_StateDirectory_is_blank(string stateDirectory)
    {
        var options = ValidBaseOptions();
        options.StateDirectory = stateDirectory;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("StateDirectory"));
    }

    [Fact]
    public void Fails_when_StateDirectory_is_relative()
    {
        var options = ValidBaseOptions();
        options.StateDirectory = Path.Combine("relative", "state");

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("absolute"));
    }

    [Theory]
    [InlineData(@"\\server\share\state")]
    [InlineData("//server/share/state")]
    public void Fails_when_StateDirectory_is_a_UNC_path(string stateDirectory)
    {
        var options = ValidBaseOptions();
        options.StateDirectory = stateDirectory;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("UNC"));
    }

    [Fact]
    public void Fails_when_StateDirectory_equals_StagingDirectory()
    {
        var options = ValidBaseOptions();
        options.StateDirectory = options.StagingDirectory + Path.DirectorySeparatorChar;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("must not overlap"));
    }

    [Fact]
    public void Fails_when_StateDirectory_is_inside_StagingDirectory()
    {
        var options = ValidBaseOptions();
        options.StateDirectory = Path.Combine(options.StagingDirectory, "state");

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("must not overlap"));
    }

    [Fact]
    public void Fails_when_StagingDirectory_is_inside_StateDirectory()
    {
        var options = ValidBaseOptions();
        options.StagingDirectory = Path.Combine(options.StateDirectory, "share");

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("must not overlap"));
    }

    [Fact]
    public void Overlap_check_canonicalizes_dot_segments_before_comparing()
    {
        // Lexically "staging2/../staging/state" does not start with "staging/", but it
        // resolves inside it.
        var options = ValidBaseOptions();
        options.StateDirectory = Path.Combine(_tempDir, "staging2", "..", "staging", "state");

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("must not overlap"));
    }

    [Fact]
    public void A_sibling_whose_name_extends_StagingDirectory_is_not_treated_as_inside_it()
    {
        var options = ValidBaseOptions();
        options.StateDirectory = options.StagingDirectory + "-state";

        var result = _validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Fails_when_StateDirectory_is_a_symbolic_link()
    {
        var target = Path.Combine(_tempDir, "real-state");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_tempDir, "linked-state");
        Directory.CreateSymbolicLink(link, target);

        var options = ValidBaseOptions();
        options.StateDirectory = link;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("symbolic link"));
    }
}

using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
            LanBindAddress = "192.168.1.10",
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
            LanBindAddress = "192.168.1.10",
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
    public void Fails_when_lan_bind_address_is_blank()
    {
        var options = new BridgeOptions
        {
            LanBindAddress = string.Empty,
            VrchatWorldsDirectory = _tempDir,
            MaxArtifactSizeBytes = 1024,
            RetainBuilds = 10,
        };

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("LanBindAddress"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fails_when_max_artifact_size_is_not_positive(long maxSize)
    {
        var options = new BridgeOptions
        {
            LanBindAddress = "192.168.1.10",
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
        LanBindAddress = "192.168.1.10",
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

    // === VrchatClientMode (multi-client plan section 13.3) ===

    private BridgeOptions ValidRelaunchOptions(params int[] profiles)
    {
        var options = ValidBaseOptions();
        options.AutoLaunchVrchat = true;
        options.VrchatExecutable = Path.Combine(_tempDir, "VRChat.exe");
        File.WriteAllText(options.VrchatExecutable, "fake exe bytes");
        options.VrchatClientMode = "Relaunch";
        options.VrchatProfiles = profiles.Length == 0 ? [0, 1] : profiles;
        return options;
    }

    [Fact]
    public void The_client_mode_defaults_to_Reload()
    {
        var options = ValidBaseOptions();

        options.VrchatClientMode.Should().Be("Reload");
        options.IsRelaunchMode.Should().BeFalse();
        _validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("Reload", false)]
    [InlineData("reload", false)]
    [InlineData("Relaunch", true)]
    [InlineData("RELAUNCH", true)]
    public void The_client_mode_is_Reload_or_Relaunch_case_insensitively(string mode, bool relaunch)
    {
        var options = relaunch ? ValidRelaunchOptions() : ValidBaseOptions();
        options.VrchatClientMode = mode;

        _validator.Validate(name: null, options).Succeeded.Should().BeTrue();
        options.IsRelaunchMode.Should().Be(relaunch);
    }

    [Theory]
    [InlineData("Restart")]
    [InlineData("")]
    [InlineData("Relaunch ")]
    public void An_unknown_client_mode_is_rejected(string mode)
    {
        var options = ValidBaseOptions();
        options.VrchatClientMode = mode;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatClientMode"));
        options.IsRelaunchMode.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0, 1)]
    [InlineData(99, 0, 5, 1)]
    public void Relaunch_accepts_one_to_four_distinct_profiles_from_0_to_99(params int[] profiles)
    {
        var result = _validator.Validate(name: null, ValidRelaunchOptions(profiles));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Relaunch_requires_AutoLaunchVrchat()
    {
        var options = ValidRelaunchOptions();
        options.AutoLaunchVrchat = false;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("AutoLaunchVrchat must be true"));
    }

    [Fact]
    public void Relaunch_requires_VrchatProfiles()
    {
        var options = ValidRelaunchOptions();
        options.VrchatProfiles = null;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatProfiles must list 1 to 4"));
    }

    [Fact]
    public void Relaunch_rejects_an_empty_profile_list()
    {
        var options = ValidRelaunchOptions();
        options.VrchatProfiles = [];

        _validator.Validate(name: null, options).Failures.Should().Contain(f => f.Contains("VrchatProfiles must list"));
    }

    [Fact]
    public void Relaunch_rejects_more_than_four_profiles()
    {
        var result = _validator.Validate(name: null, ValidRelaunchOptions(0, 1, 2, 3, 4));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("at most 4"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void Relaunch_rejects_a_profile_outside_0_to_99(int profile)
    {
        var result = _validator.Validate(name: null, ValidRelaunchOptions(0, profile));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("between 0 and 99"));
    }

    [Fact]
    public void Relaunch_rejects_a_repeated_profile()
    {
        var result = _validator.Validate(name: null, ValidRelaunchOptions(1, 0, 1));

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("must not repeat") && f.Contains("1"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Relaunch_rejects_an_explicit_VrchatClientCount(int clientCount)
    {
        var options = ValidRelaunchOptions();
        options.VrchatClientCount = clientCount;

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatClientCount must not be set"));
    }

    [Fact]
    public void Reload_rejects_VrchatProfiles()
    {
        var options = ValidBaseOptions();
        options.VrchatProfiles = [0, 1];

        var result = _validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("VrchatProfiles is only used"));
    }

    [Fact]
    public void Relaunch_does_not_check_the_readiness_wait_settings_it_never_uses()
    {
        var options = ValidRelaunchOptions();
        options.VrchatStartupSettleDelaySeconds = 0;
        options.VrchatStartupTimeoutSeconds = 1;

        _validator.Validate(name: null, options).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void VrchatProfiles_bind_from_a_json_array_in_config()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bridge:VrchatClientMode"] = "Relaunch",
                ["Bridge:VrchatProfiles:0"] = "3",
                ["Bridge:VrchatProfiles:1"] = "0",
            })
            .Build();
        var options = new BridgeOptions();

        configuration.GetSection("Bridge").Bind(options);

        options.IsRelaunchMode.Should().BeTrue();
        options.VrchatProfiles.Should().Equal(3, 0);
        options.VrchatClientCount.Should().BeNull();
    }

    // ---- LogMarkerPrefixes (client-observation plan F3) ----

    private BridgeOptions ValidOptions(string[]? prefixes) => new()
    {
        LanBindAddress = "192.168.1.10",
        StateDirectory = Path.Combine(_tempDir, "state"),
        VrchatWorldsDirectory = _tempDir,
        LogMarkerPrefixes = prefixes,
    };

    [Fact]
    public void LogMarkerPrefixes_default_to_VRCRT_when_omitted()
    {
        var options = ValidOptions(null);

        _validator.Validate(name: null, options).Succeeded.Should().BeTrue();
        options.ResolvedLogMarkerPrefixes.Should().Equal("[VRCRT]");
    }

    [Fact]
    public void LogMarkerPrefixes_from_config_json_replace_the_default()
    {
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"Bridge\": {\"LogMarkerPrefixes\": [\"[SPK003]\", \"[VRCRT]\"]}}"));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var options = new BridgeOptions();

        configuration.GetSection("Bridge").Bind(options);

        options.ResolvedLogMarkerPrefixes.Should().Equal("[SPK003]", "[VRCRT]");
    }

    [Fact]
    public void An_empty_LogMarkerPrefixes_array_in_config_json_binds_as_omitted()
    {
        // The JSON provider yields no key for an empty array, so the default applies
        // (docs/setup-windows.md, step 11-1).
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"Bridge\": {\"LogMarkerPrefixes\": []}}"));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var options = new BridgeOptions();

        configuration.GetSection("Bridge").Bind(options);

        options.ResolvedLogMarkerPrefixes.Should().Equal("[VRCRT]");
    }

    [Theory]
    [InlineData("")]
    [InlineData("[0123456789012345678901234567890123]")]
    [InlineData("tab\there")]
    public void LogMarkerPrefixes_entries_must_be_short_and_printable(string prefix)
    {
        var result = _validator.Validate(name: null, ValidOptions([prefix]));

        result.Failures.Should().ContainSingle(f => f.Contains("LogMarkerPrefixes"));
    }

    [Fact]
    public void LogMarkerPrefixes_list_at_most_eight()
    {
        var result = _validator.Validate(name: null, ValidOptions(Enumerable.Range(0, 9).Select(i => $"[P{i}]").ToArray()));

        result.Failures.Should().ContainSingle(f => f.Contains("at most 8"));
    }
}

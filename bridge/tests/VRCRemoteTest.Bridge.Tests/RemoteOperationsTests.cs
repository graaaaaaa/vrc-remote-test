using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Deployment;
using VRCRemoteTest.Bridge.Lan;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.Runtime;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public sealed class ConfigFileTests
{
    private static byte[] Bytes(string text, bool bom = false) =>
        (bom ? Encoding.UTF8.Preamble.ToArray() : []).Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    [Fact]
    public void Reads_comments_trailing_commas_and_a_bom_like_the_configuration_provider()
    {
        var root = ConfigFile.Parse(Bytes("{ // note\n \"Bridge\": { \"RetainBuilds\": 3, }, }", bom: true));

        root["Bridge"]!["RetainBuilds"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public void A_patch_replaces_values_removes_nulls_replaces_arrays_and_keeps_everything_else()
    {
        var before = Bytes("{\"Serilog\": {\"A\": 1}, \"bridge\": {\"lanport\": 38443, \"VrchatProfiles\": [0, 1, 2], \"VrchatClientCount\": 2}}");
        var patch = JsonNode.Parse("{\"VrchatProfiles\": [3], \"VrchatClientCount\": null, \"RetainBuilds\": 4}")!.AsObject();

        var after = JsonNode.Parse(BridgeAdminApi.Apply(before, patch, []))!;

        after["Serilog"]!["A"]!.GetValue<int>().Should().Be(1);
        var section = after["bridge"]!.AsObject();
        section["lanport"]!.GetValue<int>().Should().Be(38443);
        section["VrchatProfiles"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(3);
        section.ContainsKey("VrchatClientCount").Should().BeFalse();
        section["RetainBuilds"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public void An_existing_name_keeps_its_spelling()
    {
        var after = JsonNode.Parse(BridgeAdminApi.Apply(
            Bytes("{\"Bridge\": {\"retainbuilds\": 10}}"), JsonNode.Parse("{\"RetainBuilds\": 4}")!.AsObject(), []))!;

        after["Bridge"]!.AsObject().Select(p => p.Key).Should().Equal("retainbuilds");
        after["Bridge"]!["retainbuilds"]!.GetValue<int>().Should().Be(4);
    }

    [Theory]
    [InlineData("{\"Bridge\": {\"RetainBuilds\": 1, \"retainBuilds\": 2}}")]
    [InlineData("{\"Bridge\": {}, \"bridge\": {}}")]
    [InlineData("{\"Bridge\": 3}")]
    [InlineData("[1, 2]")]
    [InlineData("{ not json")]
    public void A_file_whose_meaning_is_unclear_is_refused(string text)
    {
        var act = () => BridgeAdminApi.Apply(Bytes(text), JsonNode.Parse("{\"RetainBuilds\": 4}")!.AsObject(), []);

        act.Should().Throw<ConfigFileException>();
    }

    [Fact]
    public void A_missing_file_gets_a_bridge_section()
    {
        var after = JsonNode.Parse(BridgeAdminApi.Apply(null, JsonNode.Parse("{\"RetainBuilds\": 4}")!.AsObject(), []))!;

        after["Bridge"]!["RetainBuilds"]!.GetValue<int>().Should().Be(4);
    }

    [Fact]
    public void An_executable_can_only_be_one_of_the_candidates()
    {
        var candidates = new[] { new ExecutableCandidate("steam-0", @"D:\SteamLibrary\steamapps\common\VRChat\VRChat.exe") };

        var after = JsonNode.Parse(BridgeAdminApi.Apply(
            Bytes("{}"), JsonNode.Parse("{\"VrchatExecutableCandidate\": \"steam-0\"}")!.AsObject(), candidates))!;
        after["Bridge"]!["VrchatExecutable"]!.GetValue<string>().Should().Be(candidates[0].Path);
        after["Bridge"]!.AsObject().ContainsKey(BridgeAdminApi.CandidateKey).Should().BeFalse();

        var unknown = () => BridgeAdminApi.Apply(
            Bytes("{}"), JsonNode.Parse("{\"VrchatExecutableCandidate\": \"steam-9\"}")!.AsObject(), candidates);
        unknown.Should().Throw<ConfigFileException>();
    }

    [Fact]
    public void Validation_uses_the_production_binding_and_fixes_the_state_directory()
    {
        var worlds = Directory.CreateTempSubdirectory("vrcrt-validate-").FullName;
        try
        {
            var good = Bytes($"{{\"Bridge\": {{\"LanBindAddress\": \"192.0.2.10\", \"VrchatWorldsDirectory\": {JsonSerializer.Serialize(worlds)}}}}}");
            var (options, failures) = BridgeAdminApi.Validate(good, Path.Combine(worlds, "state"));
            failures.Should().BeEmpty();
            options!.StateDirectory.Should().Be(Path.Combine(worlds, "state"));

            var relaunchWithoutLaunch = Bytes(
                $"{{\"Bridge\": {{\"LanBindAddress\": \"192.0.2.10\", \"VrchatWorldsDirectory\": {JsonSerializer.Serialize(worlds)}, \"VrchatClientMode\": \"Relaunch\", \"VrchatProfiles\": [0, 1]}}}}");
            BridgeAdminApi.Validate(relaunchWithoutLaunch, Path.Combine(worlds, "state")).Options.Should().BeNull();
        }
        finally
        {
            Directory.Delete(worlds, recursive: true);
        }
    }

    [Fact]
    public void A_backup_keeps_the_exact_bytes_and_only_the_newest_five()
    {
        var directory = Directory.CreateTempSubdirectory("vrcrt-backup-").FullName;
        try
        {
            var config = Path.Combine(directory, "config.json");
            var start = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 7; i++)
            {
                ConfigFile.Backup(config, [(byte)i, 0xEF], start.AddSeconds(i));
            }

            var kept = Directory.GetFiles(directory, "config.json.bak-*").OrderBy(p => p, StringComparer.Ordinal).ToList();
            kept.Should().HaveCount(ConfigFile.KeptBackups);
            File.ReadAllBytes(kept[^1]).Should().Equal(6, 0xEF);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class SteamVrchatLocatorTests
{
    private const string Vdf = """
        "libraryfolders"
        {
            "0"
            {
                "path"		"C:\\Program Files (x86)\\Steam"
            }
            "1"
            {
                "path"		"D:\\SteamLibrary"
            }
            "2"
            {
                "path"		"\\\\server\\share"
            }
        }
        """;

    [Fact]
    public void Lists_vrchat_in_every_local_steam_library_in_path_order()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\SteamLibrary\steamapps\common\VRChat\VRChat.exe",
            @"C:\Program Files (x86)\Steam\steamapps\common\VRChat\VRChat.exe",
            @"\\server\share\steamapps\common\VRChat\VRChat.exe",
        };
        var locator = new SteamVrchatLocator(
            () => "c:/program files (x86)/steam", existing.Contains, _ => Vdf);

        var found = locator.Find();

        found.Select(c => c.Path).Should().Equal(
            @"c:\program files (x86)\steam\steamapps\common\VRChat\VRChat.exe",
            @"D:\SteamLibrary\steamapps\common\VRChat\VRChat.exe");
        found.Select(c => c.Id).Should().Equal("steam-0", "steam-1");
    }

    [Fact]
    public void No_steam_or_an_unreadable_vdf_gives_fewer_candidates_not_an_error()
    {
        new SteamVrchatLocator(() => null, _ => true, _ => Vdf).Find().Should().BeEmpty();
        new SteamVrchatLocator(() => @"C:\Steam", _ => true, _ => "{ broken").Find()
            .Select(c => c.Path).Should().Equal(@"C:\Steam\steamapps\common\VRChat\VRChat.exe");
    }
}

public sealed class AccountTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("vrcrt-account-").FullName;

    public void Dispose() => Directory.Delete(_state, recursive: true);

    private const string UserId = "usr_0a1b2c3d-1111-2222-3333-444455556666";

    [Fact]
    public void A_digest_is_stable_per_key_and_never_the_id()
    {
        var digest = AccountDigest.LoadOrCreate(_state);
        digest.Regenerated.Should().BeTrue();
        var again = AccountDigest.LoadOrCreate(_state);
        again.Regenerated.Should().BeFalse();

        again.Of(UserId).Should().Be(digest.Of(UserId)).And.HaveLength(12).And.MatchRegex("^[0-9a-f]{12}$");
        again.Of(UserId).Should().NotBe(again.Of("usr_other"));
        again.Of(UserId).Should().NotContain("usr_");
    }

    [Fact]
    public void A_broken_key_is_replaced_and_accounts_recorded_under_the_old_key_are_never_read()
    {
        var first = AccountDigest.LoadOrCreate(_state);
        new ProfileAccountStore(_state, first.KeyId).Record(Account(0, "aaaaaaaaaaaa", 1)).Should().BeTrue();
        new ProfileAccountStore(_state, first.KeyId).All.Should().ContainSingle("the same key reads them back");

        File.WriteAllBytes(Path.Combine(_state, AccountDigest.KeyFileName), [1, 2, 3]);
        var second = AccountDigest.LoadOrCreate(_state);

        second.Regenerated.Should().BeTrue();
        second.KeyId.Should().NotBe(first.KeyId);
        new ProfileAccountStore(_state, second.KeyId).All.Should().BeEmpty();
    }

    [Fact]
    public void A_login_that_could_not_be_written_is_written_with_the_next_poll()
    {
        var status = Path.Combine(_state, "status");
        File.WriteAllText(status, "a file where the folder belongs");
        var store = new ProfileAccountStore(_state, "key");

        store.Record(Account(0, "aaaaaaaaaaaa", 1)).Should().BeFalse();
        store.All.Should().BeEmpty();

        File.Delete(status);
        store.Record(Account(0, "aaaaaaaaaaaa", 1)).Should().BeTrue();
        new ProfileAccountStore(_state, "key").All.Should().ContainSingle();
    }

    [Fact]
    public void A_profile_keeps_its_newest_login_and_two_profiles_on_one_account_are_warned()
    {
        var store = new ProfileAccountStore(_state, "key");

        store.Record(Account(0, "aaaaaaaaaaaa", 10)).Should().BeTrue();
        store.Record(Account(0, "bbbbbbbbbbbb", 5)).Should().BeFalse("an older line read again never replaces a newer one");
        store.Record(Account(1, "aaaaaaaaaaaa", 11)).Should().BeTrue();

        var reopened = new ProfileAccountStore(_state, "key");
        reopened.All.Select(a => (a.Profile, a.IdDigest)).Should().Equal((0, "aaaaaaaaaaaa"), (1, "aaaaaaaaaaaa"));
        reopened.SameAccountWarnings([0, 1]).Should().ContainSingle().Which.Should().Contain("Profiles 0 and 1");
        reopened.SameAccountWarnings([0]).Should().BeEmpty();
    }

    [Fact]
    public void The_tracker_keeps_the_name_and_digest_but_not_the_id()
    {
        var tracker = new VrchatClientStateTracker(id => "digest-of-" + id.Length);
        tracker.OnLine(Line(1, $"2026.10.30 22:09:44 Debug      -  User Authenticated: graaaaa ({UserId})"));

        tracker.Authenticated.Should().BeTrue();
        tracker.Account.Should().NotBeNull();
        tracker.Account!.Value.Name.Should().Be("graaaaa");
        tracker.Account.Value.IdDigest.Should().Be("digest-of-" + UserId.Length);
        tracker.Account.Value.LocalTime.Should().Be(new DateTime(2026, 10, 30, 22, 9, 44));
    }

    private static VrchatLogPhysicalLine Line(long seq, string text) => new(seq, text, Truncated: false);

    private static ProfileAccount Account(int profile, string digest, int minute) => new()
    {
        Profile = profile,
        DisplayName = "name",
        IdDigest = digest,
        AuthenticatedAtUtc = new DateTimeOffset(2026, 10, 30, 13, minute, 0, TimeSpan.Zero),
    };
}

public sealed class OwnedClientCloserTests
{
    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly FakeManagedClientStore _relaunch = new();
    private readonly FakeManagedClientStore _reload = new();
    private DateTime _now = new(2026, 10, 30, 13, 0, 0, DateTimeKind.Utc);

    private OwnedClientCloser Closer() => new(
        _monitor, _relaunch, _reload, NullLogger<OwnedClientCloser>.Instance, () => _now,
        (delay, _) => { _now += delay; return Task.CompletedTask; });

    private FakeVrchatProcess Process(int pid, FakeManagedClientStore? recordIn)
    {
        var process = new FakeVrchatProcess(pid, _now.AddMinutes(-pid)) { Clock = () => _now };
        _monitor.Processes.Add(process);
        recordIn?.Clients.Add(new ManagedClient(pid, process.CreationTimeUtc, process.SessionId, 0, recordIn.Clients.Count));
        return process;
    }

    [Fact]
    public async Task Closes_the_clients_of_both_records_and_leaves_others()
    {
        var relaunched = Process(11, _relaunch);
        var reloaded = Process(12, _reload);
        var stranger = Process(13, null);

        var report = await Closer().CloseAsync(requireNoOthers: false, CancellationToken.None);

        report.Should().Be(new CloseReport(false, 2, 0, 1, null));
        relaunched.CloseCalls.Should().Be(1);
        reloaded.CloseCalls.Should().Be(1);
        stranger.CloseCalls.Should().Be(0);
        _relaunch.Saved[^1].Should().BeEmpty();
        _reload.Saved[^1].Should().BeEmpty();
    }

    [Fact]
    public async Task For_a_mode_change_any_vrchat_without_a_record_closes_nothing()
    {
        var own = Process(11, _relaunch);
        Process(13, null);

        var report = await Closer().CloseAsync(requireNoOthers: true, CancellationToken.None);

        report.Refused.Should().BeTrue();
        report.Others.Should().Be(1);
        own.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_unreadable_record_or_process_list_closes_nothing()
    {
        var own = Process(11, _relaunch);
        _reload.LoadResult = ManagedClientLoadResult.Unreadable("access denied");

        (await Closer().CloseAsync(false, CancellationToken.None)).Refused.Should().BeTrue();

        _reload.LoadResult = null;
        _monitor.OpenUnverifiedCount = 1;
        (await Closer().CloseAsync(false, CancellationToken.None)).Refused.Should().BeTrue();
        own.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_record_that_cannot_be_written_after_closing_is_reported()
    {
        Process(11, _relaunch);
        _relaunch.FailingSaves = [0];

        var report = await Closer().CloseAsync(requireNoOthers: true, CancellationToken.None);

        report.Closed.Should().Be(1);
        report.RecordNotUpdated.Should().BeTrue();
        report.Problem.Should().Contain("could not update its record");
    }

    [Fact]
    public async Task A_reused_pid_is_not_the_bridges_client()
    {
        var reused = Process(11, null);
        _relaunch.Clients.Add(new ManagedClient(11, reused.CreationTimeUtc.AddTicks(1), reused.SessionId, 0, 0));

        var report = await Closer().CloseAsync(false, CancellationToken.None);

        report.Closed.Should().Be(0);
        report.Others.Should().Be(1);
        reused.CloseCalls.Should().Be(0);
    }
}

public sealed class BridgeOperationGateTests
{
    [Fact]
    public void One_operation_at_a_time_and_none_while_a_restart_is_pending()
    {
        var gate = new BridgeOperationGate();

        var first = gate.TryEnter("deploy");
        first.Should().NotBeNull();
        gate.TryEnter("config").Should().BeNull();
        gate.Current.Should().Be("deploy");
        first!.Dispose();
        first.Dispose();

        using (gate.TryEnter("config"))
        {
            gate.MarkRestartPending();
        }

        gate.TryEnter("deploy").Should().BeNull();
        gate.HostStarted();
        gate.TryEnter("deploy").Should().NotBeNull();
    }

    [Fact]
    public void Nothing_enters_before_the_first_host_has_started()
    {
        var state = Directory.CreateTempSubdirectory("vrcrt-runtime-").FullName;
        try
        {
            var runtime = new BridgeRuntime(state, BridgeIdentity.LoadOrCreate(state), AccountDigest.ForKey(new byte[32]), null);

            runtime.Gate.TryEnter("config").Should().BeNull("Kestrel answers before the host is recorded as started");
            runtime.OnHostStarted("etag");
            using (runtime.Gate.TryEnter("config"))
            {
                runtime.RequestRestart(null).Should().Be(RestartRequestResult.TooSoon);
            }
        }
        finally
        {
            Directory.Delete(state, recursive: true);
        }
    }

    [Fact]
    public void A_replace_never_overwrites_an_edit_made_meanwhile()
    {
        var directory = Directory.CreateTempSubdirectory("vrcrt-replace-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, "{\"edited\": true}");

            var act = () => ConfigFile.Replace(path, Encoding.UTF8.GetBytes("{}"), ConfigFile.EtagOf(Encoding.UTF8.GetBytes("{\"old\": 1}")));

            act.Should().Throw<ConfigChangedException>();
            File.ReadAllText(path).Should().Be("{\"edited\": true}");
            Directory.GetFiles(directory).Should().ContainSingle("the temporary file is removed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Host_ready_waits_for_application_started()
    {
        var lifetime = new FakeLifetime();
        var waiting = HostReady.WaitAsync(lifetime, CancellationToken.None);
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse();

        lifetime.Start();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Host_ready_ends_when_the_service_stops_first()
    {
        using var stop = new CancellationTokenSource();
        var waiting = HostReady.WaitAsync(new FakeLifetime(), stop.Token);

        stop.Cancel();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => _started.Cancel();

        public void StopApplication()
        {
        }
    }
}

public sealed class LaunchReportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vrcrt-launch-").FullName;
    private string State => Path.Combine(_root, "state");
    private string Worlds => Path.Combine(_root, "Worlds");

    public LaunchReportTests() => Directory.CreateDirectory(Worlds);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private StagingWatcher Watcher(bool autoLaunch, IVrchatClientCoordinator coordinator, bool relaunch = false,
        ProfileAccountStore? profiles = null) => new(
        Options.Create(new BridgeOptions
        {
            StateDirectory = State,
            VrchatWorldsDirectory = Worlds,
            MaxArtifactSizeBytes = 1024,
            AutoLaunchVrchat = autoLaunch,
            VrchatExecutable = @"C:\VRChat\VRChat.exe",
            VrchatClientMode = relaunch ? BridgeOptions.RelaunchClientMode : BridgeOptions.ReloadClientMode,
            VrchatProfiles = relaunch ? [0, 1] : null,
        }),
        new PackageValidator(),
        new WorldInstaller(),
        new ResultWriter(),
        new CleanupService(),
        coordinator,
        new VrchatLogNotice(),
        NullLogger<StagingWatcher>.Instance,
        profileAccounts: profiles);

    private void Commit(string buildId)
    {
        var bytes = Encoding.UTF8.GetBytes("world-" + buildId);
        var incoming = Path.Combine(State, "incoming");
        Directory.CreateDirectory(incoming);
        File.WriteAllBytes(Path.Combine(incoming, buildId + ".vrcw"), bytes);
        File.WriteAllText(Path.Combine(incoming, buildId + ProtocolConstants.ManifestExtension), JsonSerializer.Serialize(new BuildManifest
        {
            ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
            BuildId = buildId,
            FileName = buildId + ".vrcw",
            Size = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
    }

    private JsonNode Result(string buildId) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(State, "results", buildId + ".json")))!;

    [Fact]
    public async Task With_auto_launch_off_the_result_says_nothing_was_launched()
    {
        Commit("b1");
        await Watcher(false, new FakeVrchatClientCoordinator()).RunOnceAsync(CancellationToken.None);

        var launch = Result("b1")["launch"]!;
        launch["attempted"]!.GetValue<bool>().Should().BeFalse();
        launch["outcome"]!.GetValue<string>().Should().Be("disabled");
    }

    [Fact]
    public async Task A_reload_into_running_clients_is_reported_as_reused()
    {
        Commit("b2");
        await Watcher(true, new FakeVrchatClientCoordinator()).RunOnceAsync(CancellationToken.None);

        Result("b2")["launch"]!["outcome"]!.GetValue<string>().Should().Be("reusedRunning");
    }

    [Fact]
    public async Task A_relaunch_reports_closed_and_started_clients_and_same_account_profiles()
    {
        var profiles = new ProfileAccountStore(State, "key");
        foreach (var profile in new[] { 0, 1 })
        {
            profiles.Record(new ProfileAccount
            {
                Profile = profile, DisplayName = "same", IdDigest = "aaaaaaaaaaaa",
                AuthenticatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(profile),
            });
        }

        var coordinator = new FakeVrchatClientCoordinator
        {
            PreparationToReturn = VrchatPreparation.ColdStart(closedClients: 2),
            LaunchOutcomeToReturn = VrchatLaunchOutcome.Success(2),
        };
        Commit("b3");
        await Watcher(true, coordinator, relaunch: true, profiles).RunOnceAsync(CancellationToken.None);

        var result = Result("b3");
        result["launch"]!["outcome"]!.GetValue<string>().Should().Be("relaunched");
        result["launch"]!["closed"]!.GetValue<int>().Should().Be(2);
        result["launch"]!["started"]!.GetValue<int>().Should().Be(2);
        result["warnings"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task A_batch_that_stops_after_some_clients_is_partial()
    {
        var coordinator = new FakeVrchatClientCoordinator
        {
            PreparationToReturn = VrchatPreparation.ColdStart(),
            LaunchOutcomeToReturn = VrchatLaunchOutcome.Failure("client 2 failed", requested: 2, started: 1),
        };
        Commit("b4");
        await Watcher(true, coordinator).RunOnceAsync(CancellationToken.None);

        var result = Result("b4");
        result["status"]!.GetValue<string>().Should().Be("failed");
        result["launch"]!["outcome"]!.GetValue<string>().Should().Be("partial");
        result["launch"]!["attempted"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_deploy_waits_while_a_management_operation_holds_the_gate()
    {
        var gate = new BridgeOperationGate();
        var watcher = new StagingWatcher(
            Options.Create(new BridgeOptions { StateDirectory = State, VrchatWorldsDirectory = Worlds, MaxArtifactSizeBytes = 1024 }),
            new PackageValidator(), new WorldInstaller(), new ResultWriter(), new CleanupService(),
            new FakeVrchatClientCoordinator(), new VrchatLogNotice(), NullLogger<StagingWatcher>.Instance, gate);
        Commit("b5");

        using (gate.TryEnter("config"))
        {
            await watcher.RunOnceAsync(CancellationToken.None);
            File.Exists(Path.Combine(State, "results", "b5.json")).Should().BeFalse();
        }

        await watcher.RunOnceAsync(CancellationToken.None);
        File.Exists(Path.Combine(State, "results", "b5.json")).Should().BeTrue();
    }

    [Fact]
    public void A_result_without_launch_from_an_older_bridge_still_reads()
    {
        var old = """{"protocolVersion":1,"buildId":"x","status":"deployed","deployedFileName":"a","sha256":"b","errorCode":null,"errorMessage":null,"updatedAtUtc":"2026-10-30T00:00:00Z"}""";

        var result = JsonSerializer.Deserialize<BuildResult>(old)!;

        result.Launch.Should().BeNull();
        result.Warnings.Should().BeNull();
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Lan;
using VRCRemoteTest.Bridge.Runtime;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// The production host path (BridgeHost.RunAsync with the real DI, options binding,
/// hosted services and Kestrel) on loopback, with a fake VRChat process list: restarts,
/// configuration changes over the LAN API, rollback, and the operation gate.
/// </summary>
public sealed class BridgeHostTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("vrcrt-host-").FullName;
    private readonly CancellationTokenSource _stop = new();
    private readonly FakeVrchatProcessMonitor _processes = new();
    private DateTimeOffset _now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    private BridgeRuntime? _runtime;
    private Task<int>? _run;
    private HttpClient? _client;
    private int _port;

    /// <summary>How many of the next hosts fail to build (a host that cannot start after a change).</summary>
    private int _failBuilds;

    private string ConfigDirectory => Path.Combine(_root, "VRCRemoteTest");
    private string ConfigPath => Path.Combine(ConfigDirectory, "config.json");
    private string Worlds => Path.Combine(_root, "Worlds");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(Worlds);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_run is not null)
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(30));
        }

        _client?.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static string Config(string bridgeBody, string extra = "") =>
        "{\n  // written by hand on Windows\n" + extra + "  \"Bridge\": {\n" + bridgeBody + "\n  },\n}\n";

    private string DefaultBridge(string more = "") =>
        $"    \"LanBindAddress\": \"192.0.2.10\",\n    \"VrchatWorldsDirectory\": {JsonSerializer.Serialize(Worlds)},\n    \"RetainBuilds\": 10{more}";

    private async Task StartAsync(string? config = null, Action<IServiceCollection>? services = null)
    {
        var extra = services;
        File.WriteAllText(ConfigPath, config ?? Config(DefaultBridge(), "  \"Serilog\": { \"Note\": \"kept\" },\n"));
        var identity = BridgeIdentity.LoadOrCreate(ConfigDirectory);
        _runtime = new BridgeRuntime(
            ConfigDirectory, identity, AccountDigest.ForKey(new byte[32]), "exe-hash", () => _now);
        _port = FreePort();
        var test = new BridgeHostTestOptions
        {
            ListenOverride = new IPEndPoint(IPAddress.Loopback, _port),
            BindRetryWindow = TimeSpan.FromSeconds(10),
            ConfigureServices = services =>
            {
                if (Interlocked.Decrement(ref _failBuilds) >= 0)
                {
                    throw new InvalidOperationException("this host cannot start (test)");
                }

                services.RemoveAll<IVrchatProcessMonitor>();
                services.AddSingleton<IVrchatProcessMonitor>(_processes);
                extra?.Invoke(services);
            },
        };
        _run = Task.Run(() => BridgeHost.RunAsync(_runtime, [], test, _stop.Token));

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null
                && Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant() == identity.Fingerprint,
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_port}/api/v1/") };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
        await WaitForGenerationAsync(1);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<JsonNode> GetJsonAsync(string path)
    {
        var response = await _client!.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private async Task<JsonNode> WaitForGenerationAsync(int generation)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(_run!.IsCompleted, "the Bridge stopped");
            try
            {
                var response = await _client!.GetAsync("bridge");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var info = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                    if (info["hostGeneration"]!.GetValue<int>() >= generation)
                    {
                        return info;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Between hosts.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"host generation {generation} never started");
    }

    private async Task<HttpResponseMessage> PutConfigAsync(string patch, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "config")
        {
            Content = new StringContent(patch, Encoding.UTF8, "application/json"),
        };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{etag}\"");
        }

        return await _client!.SendAsync(request);
    }

    private async Task<string> EtagAsync() => (await GetJsonAsync("config"))["etag"]!.GetValue<string>();

    [Fact]
    public async Task Bridge_info_reports_the_running_host()
    {
        await StartAsync();

        var info = await GetJsonAsync("bridge");

        Assert.Equal(1, info["hostGeneration"]!.GetValue<int>());
        Assert.Equal("exe-hash", info["exeSha256"]!.GetValue<string>());
        Assert.Equal(BridgeRuntime.Version, info["version"]!.GetValue<string>());
        Assert.Equal(info["activeConfigEtag"]!.GetValue<string>(), info["savedConfigEtag"]!.GetValue<string>());
        Assert.Equal(ConfigFile.EtagOf(File.ReadAllBytes(ConfigPath)), info["activeConfigEtag"]!.GetValue<string>());
        Assert.Equal("https://192.0.2.10:38443", info["listenUrl"]!.GetValue<string>());
        Assert.True(info["paths"]!["vrchatWorldsDirectory"]!["exists"]!.GetValue<bool>());
        Assert.Contains(info["configNotes"]!.AsArray(), n => n!.GetValue<string>().StartsWith("AutoLaunchVrchat is false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_configuration_change_is_written_and_applied_by_the_next_host()
    {
        await StartAsync();
        var before = File.ReadAllBytes(ConfigPath);

        var response = await PutConfigAsync("{\"RetainBuilds\": 3, \"AutoLaunchVrchat\": false}", await EtagAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var info = await WaitForGenerationAsync(2);
        Assert.Equal(3, info["activeConfig"]!["RetainBuilds"]!.GetValue<int>());
        Assert.Equal("applied", info["lastConfigApply"]!["outcome"]!.GetValue<string>());
        Assert.Equal(accepted["operationId"]!.GetValue<string>(), info["lastConfigApply"]!["operationId"]!.GetValue<string>());
        Assert.Equal(accepted["requestedEtag"]!.GetValue<string>(), info["activeConfigEtag"]!.GetValue<string>());

        // The other section and settings stay; the exact previous bytes are kept next to it.
        var written = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal("kept", written["Serilog"]!["Note"]!.GetValue<string>());
        Assert.Equal("192.0.2.10", written["Bridge"]!["LanBindAddress"]!.GetValue<string>());
        var backup = Assert.Single(Directory.GetFiles(ConfigDirectory, "config.json.bak-*"));
        Assert.Equal(before, File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task A_change_needs_the_current_etag()
    {
        await StartAsync();

        Assert.Equal((HttpStatusCode)428, (await PutConfigAsync("{\"RetainBuilds\": 3}", null)).StatusCode);
        var stale = await PutConfigAsync("{\"RetainBuilds\": 3}", new string('a', 64));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Contains(await EtagAsync(), await stale.Content.ReadAsStringAsync());

        // Edited on Windows after GET /config.
        var etag = await EtagAsync();
        File.AppendAllText(ConfigPath, "\n");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutConfigAsync("{\"RetainBuilds\": 3}", etag)).StatusCode);
        Assert.Empty(Directory.GetFiles(ConfigDirectory, "config.json.bak-*"));
    }

    [Theory]
    [InlineData("{\"LanPort\": 40000}")]
    [InlineData("{\"LanBindAddress\": \"192.0.2.11\"}")]
    [InlineData("{\"VrchatWorldsDirectory\": \"C:\\\\Other\"}")]
    [InlineData("{\"VrchatExecutable\": \"C:\\\\evil\\\\VRChat.exe\"}")]
    [InlineData("{\"RetainBuilds\": 3, \"RetainBuilds\": 4}")]
    [InlineData("{\"RetainBuilds\": 0}")]
    [InlineData("{\"RetainBuilds\": \"3\"}")]
    [InlineData("{\"MaxArtifactSizeBytes\": 4294967296}")]
    [InlineData("{\"VrchatClientMode\": \"relaunch\"}")]
    [InlineData("{\"VrchatExecutableCandidate\": null}")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public async Task A_patch_outside_the_editable_settings_is_refused_and_nothing_is_written(string patch)
    {
        await StartAsync();
        var before = File.ReadAllBytes(ConfigPath);

        var response = await PutConfigAsync(patch, await EtagAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Empty(Directory.GetFiles(ConfigDirectory, "config.json.bak-*"));
        Assert.Equal(1, (await GetJsonAsync("bridge"))["hostGeneration"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_change_the_validator_refuses_is_not_written()
    {
        await StartAsync();
        var before = File.ReadAllBytes(ConfigPath);

        // Relaunch needs AutoLaunchVrchat, VrchatProfiles and an existing VrchatExecutable.
        var response = await PutConfigAsync("{\"VrchatClientMode\": \"Relaunch\"}", await EtagAsync());

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains("problems", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public async Task A_host_that_cannot_start_after_a_change_is_rebuilt_from_the_previous_file()
    {
        await StartAsync();
        var before = File.ReadAllBytes(ConfigPath);
        _failBuilds = 1;

        var response = await PutConfigAsync("{\"RetainBuilds\": 4}", await EtagAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var info = await WaitForGenerationAsync(2);
        Assert.Equal("rolledBack", info["lastConfigApply"]!["outcome"]!.GetValue<string>());
        Assert.Contains("this host cannot start", info["lastConfigApply"]!["problems"]![0]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Equal(10, info["activeConfig"]!["RetainBuilds"]!.GetValue<int>());
    }

    [Fact]
    public async Task When_the_previous_file_cannot_start_either_the_change_fails_and_the_bridge_stops()
    {
        await StartAsync();
        _failBuilds = 2;

        var response = await PutConfigAsync("{\"RetainBuilds\": 4}", await EtagAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await _run!.WaitAsync(TimeSpan.FromSeconds(30)));
        var report = _runtime!.LastConfigApply!;
        Assert.Equal("failed", report.Outcome);
        Assert.Equal(2, report.Problems.Count);
        Assert.Contains("previous config.json could not start either", report.Problems[1]);
        _run = null;
    }

    [Fact]
    public async Task A_name_in_another_case_changes_that_setting_and_keeps_the_files_spelling()
    {
        await StartAsync(Config(DefaultBridge().Replace("\"RetainBuilds\"", "\"retainbuilds\"", StringComparison.Ordinal)));

        var response = await PutConfigAsync("{\"retainBuilds\": 3}", await EtagAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var info = await WaitForGenerationAsync(2);
        Assert.Equal(3, info["activeConfig"]!["RetainBuilds"]!.GetValue<int>());
        Assert.Equal(3, JsonNode.Parse(File.ReadAllText(ConfigPath))!["Bridge"]!["retainbuilds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Two_names_for_one_setting_are_a_conflict()
    {
        await StartAsync();

        var response = await PutConfigAsync("{\"RetainBuilds\": 3, \"retainBuilds\": 4}", await EtagAsync());

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task Restarts_keep_the_process_identity_and_rebind_the_same_port()
    {
        await StartAsync();
        var first = await GetJsonAsync("bridge");

        for (var generation = 2; generation <= 4; generation++)
        {
            _now += TimeSpan.FromSeconds(11);
            var response = await _client!.PostAsync("bridge/restart", null);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var info = await WaitForGenerationAsync(generation);
            Assert.Equal(first["processStartedAtUtc"]!.GetValue<DateTimeOffset>(), info["processStartedAtUtc"]!.GetValue<DateTimeOffset>());
            Assert.Equal(first["certificateSha256"]!.GetValue<string>(), info["certificateSha256"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_restart_right_after_a_start_is_refused()
    {
        await StartAsync();

        var response = await _client!.PostAsync("bridge/restart", null);

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal(1, (await GetJsonAsync("bridge"))["hostGeneration"]!.GetValue<int>());
    }

    [Fact]
    public async Task Management_operations_are_busy_while_another_holds_the_gate()
    {
        await StartAsync();
        using (_runtime!.Gate.TryEnter("deploy"))
        {
            var put = await PutConfigAsync("{\"RetainBuilds\": 3}", await EtagAsync());
            Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
            Assert.Contains("\"operation\":\"deploy\"", await put.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await _client!.PostAsync("clients/close", null)).StatusCode);
            _now += TimeSpan.FromSeconds(11);
            Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("bridge/restart", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await _client!.PostAsync("clients/close", null)).StatusCode);
    }

    [Fact]
    public async Task Changing_the_mode_with_a_vrchat_the_bridge_did_not_launch_closes_nothing_and_writes_nothing()
    {
        var exe = Path.Combine(_root, "VRChat.exe");
        File.WriteAllText(exe, string.Empty);
        await StartAsync(Config(DefaultBridge(
            $",\n    \"AutoLaunchVrchat\": true,\n    \"VrchatExecutable\": {JsonSerializer.Serialize(exe)}")));
        var stranger = new FakeVrchatProcess(4242, DateTime.UtcNow);
        _processes.Processes.Add(stranger);
        var before = File.ReadAllBytes(ConfigPath);

        var response = await PutConfigAsync(
            "{\"VrchatClientMode\": \"Relaunch\", \"VrchatProfiles\": [0, 1]}", await EtagAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("vrchatRunning", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, stranger.CloseCalls);
        Assert.Equal(0, stranger.KillCalls);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public async Task Changing_the_mode_with_no_vrchat_running_is_applied()
    {
        var exe = Path.Combine(_root, "VRChat.exe");
        File.WriteAllText(exe, string.Empty);
        await StartAsync(Config(DefaultBridge(
            $",\n    \"AutoLaunchVrchat\": true,\n    \"VrchatExecutable\": {JsonSerializer.Serialize(exe)}")));

        var response = await PutConfigAsync(
            "{\"VrchatClientMode\": \"Relaunch\", \"VrchatProfiles\": [0, 1]}", await EtagAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var info = await WaitForGenerationAsync(2);
        Assert.Equal("Relaunch", info["activeConfig"]!["VrchatClientMode"]!.GetValue<string>());

        // And back, removing the profiles in the same patch.
        var back = await PutConfigAsync(
            "{\"VrchatClientMode\": \"Reload\", \"VrchatProfiles\": null, \"VrchatClientCount\": 2}", await EtagAsync());
        Assert.Equal(HttpStatusCode.Accepted, back.StatusCode);
        info = await WaitForGenerationAsync(3);
        Assert.Equal("Reload", info["activeConfig"]!["VrchatClientMode"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(ConfigPath))!["Bridge"]!["VrchatProfiles"]);
    }

    [Fact]
    public async Task The_bridge_log_is_returned_masked()
    {
        await StartAsync();
        var logs = Path.Combine(ConfigDirectory, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "bridge-20261030.log"),
            "line one\r\nC:\\Users\\alice\\AppData\\Local\\VRCRemoteTest\r\nUser usr_0123abcd-0000-0000-0000-000000000000\r\n");

        var text = await _client!.GetStringAsync("logs/bridge?lines=2");

        Assert.DoesNotContain("line one", text);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("usr_0123", text);
    }

    [Fact]
    public async Task Stopping_the_process_ends_the_run_with_exit_code_zero()
    {
        await StartAsync();

        _stop.Cancel();

        Assert.Equal(0, await _run!.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    // ---- one client: POST /clients/{slot}/close and /launch (v0.10.0) ----

    private readonly FakeManagedClientStore _record = new();
    private readonly FakeVrchatLauncher _launcher = new();
    private DateTime _slotNow = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Whether the next host's launch context starts with slots 0 and 1 of a batch, as a deploy leaves it.</summary>
    private bool _seedBatch = true;

    private string RelaunchWorld => Path.Combine(Worlds, "vrc-remote-b1.vrcw");

    /// <summary>
    /// A Relaunch host whose clients 100 (slot 0) and 101 (slot 1) the Bridge launched and
    /// recorded, with the real ClientSlotControl over fake processes, record, launcher and clock.
    /// </summary>
    private async Task<(FakeVrchatProcess Slot0, FakeVrchatProcess Slot1)> StartRelaunchAsync(string mode = "Relaunch")
    {
        var exe = Path.Combine(_root, "VRChat.exe");
        File.WriteAllText(exe, string.Empty);
        File.WriteAllText(RelaunchWorld, "world");
        var created0 = _slotNow.AddMinutes(-3).AddTicks(123);
        var created1 = _slotNow.AddMinutes(-3).AddSeconds(4).AddTicks(456);
        _record.Clients.Add(new ManagedClient(100, created0, 1, 0, 0));
        _record.Clients.Add(new ManagedClient(101, created1, 1, 1, 1));
        var slot0 = Process(100, created0);
        var slot1 = Process(101, created1);
        _launcher.Clock = () => _slotNow;
        _launcher.OnLaunch = pid => Process(pid, _launcher.Launched[^1].CreationTimeUtc);

        var profiles = mode == "Relaunch" ? ",\n    \"VrchatProfiles\": [0, 1]" : string.Empty;
        await StartAsync(
            Config(DefaultBridge(
                $",\n    \"AutoLaunchVrchat\": true,\n    \"VrchatExecutable\": {JsonSerializer.Serialize(exe)}" +
                $",\n    \"VrchatClientMode\": \"{mode}\"{profiles}")),
            services =>
            {
                services.RemoveAll<IManagedClientStore>();
                services.AddSingleton<IManagedClientStore>(_record);
                services.RemoveAll<IVrchatLauncher>();
                services.AddSingleton<IVrchatLauncher>(_launcher);
                services.RemoveAll<ClientSlotControl>();
                services.AddSingleton(sp =>
                {
                    var context = sp.GetRequiredService<RelaunchLaunchContext>();
                    if (_seedBatch)
                    {
                        context.BeginBatch(RelaunchWorld, "1234567890");
                        context.AddSlot(0, 0, noVr: true);
                        context.AddSlot(1, 1, noVr: true);
                    }

                    return new ClientSlotControl(
                        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BridgeOptions>>(),
                        sp.GetRequiredService<IVrchatProcessMonitor>(),
                        sp.GetRequiredService<IManagedClientStore>(),
                        sp.GetRequiredService<IVrchatLauncher>(),
                        context,
                        sp.GetRequiredService<VrchatClientRoster>(),
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientSlotControl>.Instance,
                        (wait, _) =>
                        {
                            lock (_launcher)
                            {
                                _slotNow += wait;
                            }

                            return Task.CompletedTask;
                        },
                        () => null,
                        () => _slotNow,
                        File.Exists);
                });
            });
        return (slot0, slot1);
    }

    private FakeVrchatProcess Process(int pid, DateTime created)
    {
        var process = _processes.AddProcess(pid, created);
        process.Clock = () => _slotNow;
        return process;
    }

    [Fact]
    public async Task One_client_is_closed_and_started_again_over_the_LAN_API()
    {
        var (slot0, slot1) = await StartRelaunchAsync();

        var close = await _client!.PostAsync("clients/0/close", null);
        var closed = JsonNode.Parse(await close.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal(100, closed["processId"]!.GetValue<int>());
        Assert.Equal(1, closed["closed"]!.GetValue<int>());
        Assert.True(closed["recordUpdated"]!.GetValue<bool>());
        Assert.False(slot0.IsRunning);

        var launch = await _client.PostAsync("clients/0/launch", null);
        var launched = JsonNode.Parse(await launch.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        var (pid, created) = _launcher.Launched.Single();
        Assert.Equal(pid, launched["processId"]!.GetValue<int>());
        Assert.Equal(0, launched["profile"]!.GetValue<int>());
        Assert.Equal(VrchatClientRoster.GenerationOf(pid, created), launched["clientGeneration"]!.GetValue<long>());
        Assert.Equal(RelaunchWorld, _launcher.Requests.Single().WorldPath);
        Assert.Equal("1234567890", _launcher.Requests.Single().RoomId);

        Assert.True(slot1.IsRunning);
        Assert.Equal(0, slot1.CloseCalls + slot1.KillCalls);
        Assert.Contains(_record.Clients, c => c.ProcessId == pid && c.LaunchOrder == 0);
        Assert.Contains(_record.Clients, c => c.ProcessId == 101 && c.LaunchOrder == 1);
    }

    // ---- preconditions (docs/plans/r1b-contract.md §1) ----

    private async Task<JsonObject> BridgeInfoAsync() =>
        JsonNode.Parse(await (await _client!.GetAsync("bridge")).Content.ReadAsStringAsync())!.AsObject();

    private static JsonObject ExpectFrom(JsonObject info, long clientGeneration, long? revision = null) => new()
    {
        ["bridgeInstanceId"] = info["bridgeInstanceId"]!.GetValue<string>(),
        ["hostGeneration"] = info["hostGeneration"]!.GetValue<int>(),
        ["activeConfigEtag"] = info["activeConfigEtag"]?.GetValue<string>(),
        ["savedConfigEtag"] = info["savedConfigEtag"]?.GetValue<string>(),
        ["operationRevision"] = revision ?? info["operationRevision"]!.GetValue<long>(),
        ["buildId"] = info["currentBatch"]!["buildId"]?.GetValue<string>(),
        ["clientGeneration"] = clientGeneration,
    };

    private Task<HttpResponseMessage> PostExpectAsync(string path, JsonObject expect, string? operationId = null)
    {
        var body = new JsonObject { ["expect"] = expect };
        if (operationId is not null)
        {
            body["operationId"] = operationId;
        }

        return _client!.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    }

    [Fact]
    public async Task A_guarded_close_and_launch_each_move_the_revision_by_one()
    {
        var (slot0, slot1) = await StartRelaunchAsync();
        var capabilities = JsonNode.Parse(await (await _client!.GetAsync("capabilities")).Content.ReadAsStringAsync())!;
        Assert.Contains("slotPreconditions.v1", capabilities["features"]!.AsArray().Select(f => f!.GetValue<string>()));
        var info = await BridgeInfoAsync();
        Assert.Matches("^[0-9a-f]{32}$", info["bridgeInstanceId"]!.GetValue<string>());
        var revision = info["operationRevision"]!.GetValue<long>();
        var generation0 = VrchatClientRoster.GenerationOf(100, slot0.CreationTimeUtc);

        var close = await PostExpectAsync("clients/0/close", ExpectFrom(info, generation0), "run-1-close");
        var closed = JsonNode.Parse(await close.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal(revision + 1, closed["operationRevision"]!.GetValue<long>());
        Assert.Equal(generation0, closed["clientGeneration"]!.GetValue<long>());
        Assert.Equal("run-1-close", closed["operationId"]!.GetValue<string>());
        Assert.Equal(info["bridgeInstanceId"]!.GetValue<string>(), closed["bridgeInstanceId"]!.GetValue<string>());
        Assert.False(slot0.IsRunning);
        // The test host seeds its batch when the control is first used, so it shows from here.
        Assert.Equal(new[] { 0, 1 }, (await BridgeInfoAsync())["currentBatch"]!["launchedSlots"]!.AsArray().Select(s => s!.GetValue<int>()));

        var launch = await PostExpectAsync("clients/0/launch", ExpectFrom(info, generation0, revision + 1), "run-1-launch");
        var launched = JsonNode.Parse(await launch.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        Assert.Equal(revision + 2, launched["operationRevision"]!.GetValue<long>());
        Assert.Single(_launcher.Requests);
        Assert.True(slot1.IsRunning);
    }

    [Fact]
    public async Task A_stale_precondition_is_answered_412_and_does_nothing()
    {
        var (slot0, _) = await StartRelaunchAsync();
        var info = await BridgeInfoAsync();
        var revision = info["operationRevision"]!.GetValue<long>();
        var generation0 = VrchatClientRoster.GenerationOf(100, slot0.CreationTimeUtc);

        var staleRevision = await PostExpectAsync("clients/0/close", ExpectFrom(info, generation0, revision - 1), "op-1");
        var otherClient = await PostExpectAsync("clients/0/close", ExpectFrom(info, generation0 + 1));
        var otherBridge = ExpectFrom(info, generation0);
        otherBridge["bridgeInstanceId"] = new string('0', 32);
        var restarted = await PostExpectAsync("clients/0/close", otherBridge);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleRevision.StatusCode);
        var body = JsonNode.Parse(await staleRevision.Content.ReadAsStringAsync())!;
        Assert.Equal("preconditionFailed", body["error"]!.GetValue<string>());
        Assert.Equal(new[] { "operationRevision" }, body["mismatched"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal(revision, body["current"]!["operationRevision"]!.GetValue<long>());
        Assert.Equal("op-1", body["operationId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.PreconditionFailed, otherClient.StatusCode);
        Assert.Contains("clientGeneration", await otherClient.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.PreconditionFailed, restarted.StatusCode);
        Assert.True(slot0.IsRunning);
        Assert.Equal(0, slot0.CloseCalls + slot0.KillCalls);
        Assert.Equal(revision, (await BridgeInfoAsync())["operationRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_malformed_body_is_refused_400_and_never_read_as_none()
    {
        var (slot0, _) = await StartRelaunchAsync();
        var revision = (await BridgeInfoAsync())["operationRevision"]!.GetValue<long>();
        var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"expect\":")));
        chunked.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var responses = new[]
        {
            await _client!.PostAsync("clients/0/close", new StringContent("{}", Encoding.UTF8, "application/json")),
            await _client.PostAsync("clients/0/close", new StringContent("{\"expect\":{}}", Encoding.UTF8, "text/plain")),
            await _client.PostAsync("clients/0/close", chunked),
            await _client.PostAsync("clients/0/launch", new StringContent(new string(' ', 5000), Encoding.UTF8, "application/json")),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        foreach (var response in responses)
        {
            Assert.Equal("invalidPrecondition", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        }

        Assert.True(slot0.IsRunning);
        Assert.False(_launcher.LaunchCalled);
        Assert.Equal(revision, (await BridgeInfoAsync())["operationRevision"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_close_without_a_body_also_reports_the_revision_and_the_Bridge()
    {
        await StartRelaunchAsync();
        var revision = (await BridgeInfoAsync())["operationRevision"]!.GetValue<long>();

        var close = await _client!.PostAsync("clients/0/close", null);
        var closed = JsonNode.Parse(await close.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Equal(revision + 1, closed["operationRevision"]!.GetValue<long>());
        Assert.True(closed.ContainsKey("operationId"));
        Assert.Null(closed["operationId"]);
        Assert.Matches("^[0-9a-f]{32}$", closed["bridgeInstanceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_the_token_the_single_client_routes_answer_401_and_touch_nothing()
    {
        var (slot0, _) = await StartRelaunchAsync();
        slot0.Exit();

        foreach (var token in new string?[] { null, "not-the-token" })
        {
            foreach (var path in new[] { "clients/0/close", "clients/1/close", "clients/0/launch" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path);
                request.Headers.Authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);

                using var anonymous = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                }) { BaseAddress = _client!.BaseAddress };
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(request)).StatusCode);
            }
        }

        Assert.False(_launcher.LaunchCalled);
        Assert.All(_processes.Processes, p => Assert.Equal(0, p.CloseCalls + p.KillCalls));
        Assert.Equal(0, _record.SaveCalls);
    }

    [Theory]
    [InlineData("-1", HttpStatusCode.BadRequest, "unknownSlot")]
    [InlineData("4", HttpStatusCode.BadRequest, "unknownSlot")]
    [InlineData("01", HttpStatusCode.Conflict, "alreadyRunning")]
    [InlineData("99999999999", HttpStatusCode.NotFound, "")]
    public async Task The_slot_is_an_integer_from_0_to_3(string slot, HttpStatusCode status, string error)
    {
        await StartRelaunchAsync();

        var response = await _client!.PostAsync($"clients/{slot}/launch", null);

        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (error.Length == 0)
        {
            Assert.Equal(string.Empty, body);
        }
        else
        {
            Assert.Equal(error, JsonNode.Parse(body)!["error"]!.GetValue<string>());
            Assert.NotEmpty(JsonNode.Parse(body)!["problems"]!.AsArray());
        }

        Assert.False(_launcher.LaunchCalled);
    }

    [Fact]
    public async Task The_single_client_routes_are_busy_while_another_operation_holds_the_gate()
    {
        var (slot0, _) = await StartRelaunchAsync();
        using (_runtime!.Gate.TryEnter("deploy"))
        {
            foreach (var path in new[] { "clients/0/close", "clients/0/launch" })
            {
                var response = await _client!.PostAsync(path, null);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains("\"operation\":\"deploy\"", await response.Content.ReadAsStringAsync());
            }
        }

        Assert.True(slot0.IsRunning);
        Assert.False(_launcher.LaunchCalled);
    }

    [Fact]
    public async Task In_Reload_mode_the_single_client_routes_refuse()
    {
        var (slot0, _) = await StartRelaunchAsync("Reload");

        var response = await _client!.PostAsync("clients/0/close", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("notRelaunchMode", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        Assert.True(slot0.IsRunning);
    }

    [Fact]
    public async Task A_restarted_host_has_no_launch_to_repeat()
    {
        var (slot0, _) = await StartRelaunchAsync();
        slot0.Exit();
        _seedBatch = false;
        _now += TimeSpan.FromSeconds(11);
        Assert.Equal(HttpStatusCode.Accepted, (await _client!.PostAsync("bridge/restart", null)).StatusCode);
        await WaitForGenerationAsync(2);

        var response = await _client.PostAsync("clients/0/launch", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("noLaunchPlan", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());
        Assert.False(_launcher.LaunchCalled);
    }

    [Fact]
    public async Task Twenty_launches_at_once_start_one_client()
    {
        var (slot0, _) = await StartRelaunchAsync();
        slot0.Exit();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _client!.PostAsync("clients/0/launch", null)));

        Assert.Single(_launcher.Requests);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }
}

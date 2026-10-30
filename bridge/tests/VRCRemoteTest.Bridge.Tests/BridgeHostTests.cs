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

    /// <summary>Set to make the next host fail to build once (a host that cannot start after a change).</summary>
    private volatile bool _failNextBuild;

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

    private async Task StartAsync(string? config = null)
    {
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
                if (_failNextBuild)
                {
                    _failNextBuild = false;
                    throw new InvalidOperationException("this host cannot start (test)");
                }

                services.RemoveAll<IVrchatProcessMonitor>();
                services.AddSingleton<IVrchatProcessMonitor>(_processes);
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
    [InlineData("{\"retainBuilds\": 3}")]
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
        _failNextBuild = true;

        var response = await PutConfigAsync("{\"RetainBuilds\": 4}", await EtagAsync());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var info = await WaitForGenerationAsync(2);
        Assert.Equal("rolledBack", info["lastConfigApply"]!["outcome"]!.GetValue<string>());
        Assert.Contains("this host cannot start", info["lastConfigApply"]!["problems"]![0]!.GetValue<string>());
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Equal(10, info["activeConfig"]!["RetainBuilds"]!.GetValue<int>());
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
}

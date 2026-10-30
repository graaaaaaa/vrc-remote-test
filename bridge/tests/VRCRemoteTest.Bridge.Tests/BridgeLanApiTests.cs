using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Lan;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public sealed class BridgeLanApiTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("vrcrt-lan-api-").FullName;
    private WebApplication? _app;
    private HttpClient? _client;
    private BridgeIdentity? _identity;
    private readonly LogSourceRegistry _sources = new();
    private int _port;

    public async Task InitializeAsync()
    {
        _identity = BridgeIdentity.LoadOrCreate(_root);
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, _port,
            endpoint => endpoint.UseHttps(_identity.Certificate)));
        _app = builder.Build();
        var options = new BridgeOptions { StateDirectory = Path.Combine(_root, "state"), MaxArtifactSizeBytes = 32 };
        BridgeLanApi.Map(_app, options, _identity, _sources);
        await _app.StartAsync();
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant() == _identity.Fingerprint,
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{_port}/api/v1/") };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Requires_token_and_reports_version()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client!.GetAsync("capabilities")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("capabilities")).StatusCode);
        Authenticate();
        var response = await _client.GetAsync("capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"apiVersion\":1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_certificate_fingerprint_is_rejected()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant() == new string('0', 64),
        };
        using var client = new HttpClient(handler) { BaseAddress = _client!.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _identity!.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("capabilities"));
    }

    [Fact]
    public async Task Log_export_refuses_unknown_client_session()
    {
        Authenticate();
        var request = new StringContent("{\"slot\":0,\"clientGeneration\":1,\"observationEpoch\":1}",
            Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Conflict,
            (await _client!.PostAsync("log-exports", request)).StatusCode);
    }

    [Fact]
    public async Task Log_export_masks_full_verified_session_and_rejects_stale_session()
    {
        Authenticate();
        var path = Path.Combine(_root, "output_log.txt");
        await File.WriteAllTextAsync(path, "Authorization: Bearer secret\nclient 192.168.0.99\n");
        _sources.Publish(new VrchatLogReadResult(null!, null!,
            [new SlotLogResult(0, SlotLogStatus.Shown, path, LogSnapshotResult.Success(""))],
            LogSnapshotResult.Empty()),
            new VrchatClients { Clients = [new VrchatClientEntry
                { Slot = 0, ClientGeneration = 7, ObservationEpoch = 9 }] });

        var request = new StringContent("{\"slot\":0,\"clientGeneration\":7,\"observationEpoch\":9}",
            Encoding.UTF8, "application/json");
        var response = await _client!.PostAsync("log-exports", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var exportId = metadata.RootElement.GetProperty("exportId").GetString();
        var body = await _client.GetStringAsync($"log-exports/{exportId}");
        Assert.Contains("<redacted>", body);
        Assert.Contains("<ip>", body);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("192.168.0.99", body);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), metadata.RootElement.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
            metadata.RootElement.GetProperty("sha256").GetString());

        var stale = new StringContent("{\"slot\":0,\"clientGeneration\":6,\"observationEpoch\":9}",
            Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("log-exports", stale)).StatusCode);
    }

    [Fact]
    public async Task Upload_rejects_oversize_and_conflicting_retry()
    {
        Authenticate();
        var tooLarge = await _client!.PutAsync("builds/buildA/artifact", new ByteArrayContent(new byte[33]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        var first = await _client.PutAsync("builds/buildA/artifact", new ByteArrayContent(Encoding.UTF8.GetBytes("world")));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var duplicate = await _client.PutAsync("builds/buildA/artifact", new ByteArrayContent(Encoding.UTF8.GetBytes("world")));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var conflict = await _client.PutAsync("builds/buildA/artifact", new ByteArrayContent(Encoding.UTF8.GetBytes("wrong")));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Commit_checks_hash_and_identical_retry_only()
    {
        Authenticate();
        var bytes = Encoding.UTF8.GetBytes("world");
        await _client!.PutAsync("builds/buildB/artifact", new ByteArrayContent(bytes));
        var manifest = new
        {
            protocolVersion = 1,
            buildId = "buildB",
            fileName = "buildB.vrcw",
            size = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            createdAtUtc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(manifest);
        var first = await _client.PostAsync("builds/buildB/commit", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var retry = await _client.PostAsync("builds/buildB/commit", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var changed = json.Replace("\"size\":5", "\"size\":4");
        var conflict = await _client.PostAsync("builds/buildB/commit", new StringContent(changed, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Too_large_answers_say_what_and_the_limit()
    {
        Authenticate();

        var declared = await _client!.PutAsync("builds/buildB/artifact", new ByteArrayContent(new byte[40]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        var body = JsonDocument.Parse(await declared.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("artifactTooLarge", body.GetProperty("error").GetString());
        Assert.Equal(32, body.GetProperty("limitBytes").GetInt64());
        Assert.Equal(40, body.GetProperty("declaredBytes").GetInt64());

        // Sent without Content-Length (chunked): caught while reading, with no declared size.
        var chunked = new StreamContent(new MemoryStream(new byte[40]));
        chunked.Headers.ContentLength = null;
        using var request = new HttpRequestMessage(HttpMethod.Put, "builds/buildC/artifact") { Content = chunked };
        request.Headers.TransferEncodingChunked = true;
        var streamed = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        body = JsonDocument.Parse(await streamed.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("artifactTooLarge", body.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("declaredBytes").ValueKind);

        var manifest = await _client.PostAsync("builds/buildD/commit", new ByteArrayContent(new byte[64 * 1024 + 1]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, manifest.StatusCode);
        Assert.Contains("manifestTooLarge", await manifest.Content.ReadAsStringAsync());
    }

    private void Authenticate() => _client!.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", _identity!.Token);
}

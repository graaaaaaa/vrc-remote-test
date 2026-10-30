using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Lan;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// A world larger than Kestrel's default request body limit (30,000,000 bytes) but within MaxArtifactSizeBytes is
/// accepted; on v0.7.0 Kestrel answered 413 before the Bridge's own limit was checked (a 47.7 MB world, 2026-10-30).
/// </summary>
public sealed class BridgeLanApiLargeArtifactTests : IAsyncLifetime
{
    private const long KestrelDefaultLimit = 30_000_000;
    private readonly string _root = Directory.CreateTempSubdirectory("vrcrt-lan-large-").FullName;
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var identity = BridgeIdentity.LoadOrCreate(_root);
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, port,
            endpoint => endpoint.UseHttps(identity.Certificate)));
        _app = builder.Build();
        var options = new BridgeOptions
        {
            StateDirectory = Path.Combine(_root, "state"),
            MaxArtifactSizeBytes = KestrelDefaultLimit + 4 * 1024 * 1024,
        };
        BridgeLanApi.Map(_app, options, identity, new LogSourceRegistry());
        await _app.StartAsync();
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant() == identity.Fingerprint,
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}/api/v1/") };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", identity.Token);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Artifact_above_the_server_default_limit_but_within_the_bridge_limit_is_accepted()
    {
        var bytes = new byte[KestrelDefaultLimit + 1024 * 1024];
        Random.Shared.NextBytes(bytes);

        var response = await _client!.PutAsync("builds/buildLarge/artifact", new ByteArrayContent(bytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Artifact_above_the_bridge_limit_is_still_refused()
    {
        var bytes = new byte[KestrelDefaultLimit + 5 * 1024 * 1024];

        // The Bridge answers 413 without reading the body, so the client may see the connection closed mid-upload.
        try
        {
            var response = await _client!.PutAsync("builds/buildTooLarge/artifact", new ByteArrayContent(bytes));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        catch (HttpRequestException)
        {
        }

        Assert.False(File.Exists(Path.Combine(_root, "state", "incoming", "buildTooLarge.vrcw")));
    }
}

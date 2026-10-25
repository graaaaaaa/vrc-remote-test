using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>What one poll of the real reader and observer publishes on the share (client-observation plan F1-F3).</summary>
public class VrchatLogServicePublishTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 17, 6, 0, 0, 100, DateTimeKind.Utc);
    private const string Header = "2026.10.18 13:34:37 Debug      -  ";

    private readonly string _tempDir;
    private readonly string _logDir;
    private readonly string _staging;
    private readonly FakeVrchatProcessMonitor _monitor = new();

    public VrchatLogServicePublishTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-publish-").FullName;
        _logDir = Path.Combine(_tempDir, "VRChat", "VRChat");
        _staging = Path.Combine(_tempDir, "share");
        Directory.CreateDirectory(Path.Combine(_logDir, "Worlds"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private string Share(params string[] parts) => Path.Combine([_staging, .. parts]);

    private static DateTime CreationTimeFromName(string path) =>
        VrchatLogMatcher.ParseNameTime(Path.GetFileName(path)) is { } local
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local)
            : DateTime.MinValue;

    private async Task PollOnceAsync()
    {
        var options = new BridgeOptions
        {
            StateDirectory = _staging,
            VrchatWorldsDirectory = Path.Combine(_logDir, "Worlds"),
        };
        var roster = new VrchatClientRoster(Path.Combine(_tempDir, "state", VrchatClientRoster.FileName));
        var reader = new VrchatLogReader(
            Options.Create(options), _monitor, NullLogger<VrchatLogReader>.Instance, CreationTimeFromName, null, null, roster);
        var observer = new VrchatClientObserver(options, roster, null, CreationTimeFromName, () => new DateTimeOffset(T0.AddSeconds(10)));
        var service = new VrchatLogService(
            Options.Create(options), reader, observer, new VrchatLogNotice(), NullLogger<VrchatLogService>.Instance, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(Share("status", "vrchat-clients.json")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Publishes_each_slots_log_markers_and_the_client_list()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        File.WriteAllText(
            Path.Combine(_logDir, $"output_log_{T0.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt"),
            Header + "[VRCRT] first\nmore\r\n");
        File.WriteAllText(
            Path.Combine(_logDir, $"output_log_{T0.AddSeconds(3).ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt"),
            Header + "second\r\n");

        await PollOnceAsync();

        File.ReadAllText(Share("logs", "vrchat-latest.log")).Should().Be(Header + "[VRCRT] first\n    more");
        File.ReadAllText(Share("logs", "clients", "slot-0.log")).Should().Be(Header + "[VRCRT] first\n    more");
        File.ReadAllText(Share("logs", "clients", "slot-1.log")).Should().Be(Header + "second");
        File.ReadAllText(Share("logs", "clients", "slot-2.log")).Should().Be("No VRChat client is running in slot 2.");
        File.ReadAllText(Share("logs", "clients", "slot-0.markers.log")).Split('\n')[1]
            .Should().Be("1\t" + Header + @"[VRCRT] first\nmore");
        File.Exists(Share("logs", "clients", "slot-2.markers.log")).Should().BeFalse();

        using var clients = JsonDocument.Parse(File.ReadAllText(Share("status", "vrchat-clients.json")));
        clients.RootElement.GetProperty("clients").EnumerateArray().Select(c => c.GetProperty("processId").GetInt32())
            .Should().Equal(100, 200);
    }

    [Fact]
    public async Task A_new_client_in_a_slot_moves_the_previous_markers_to_prev()
    {
        Directory.CreateDirectory(Share("logs", "clients"));
        File.WriteAllText(Share("logs", "clients", "slot-0.markers.log"), "# {\"clientGeneration\":1}\n1\told marker\n");
        _monitor.AddClient(100, T0);
        File.WriteAllText(
            Path.Combine(_logDir, $"output_log_{T0.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt"), Header + "[VRCRT] new\r\n");

        await PollOnceAsync();

        File.ReadAllText(Share("logs", "clients", "slot-0.markers.prev.log")).Should().Contain("old marker");
        File.ReadAllText(Share("logs", "clients", "slot-0.markers.log")).Should().Contain("new").And.NotContain("old marker");
    }
}

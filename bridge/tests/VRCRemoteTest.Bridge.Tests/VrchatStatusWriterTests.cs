using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class VrchatStatusWriterTests : IDisposable
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _tempDir;
    private readonly VrchatStatusWriter _writer = new(NullLogger<VrchatStatusWriter>.Instance);

    public VrchatStatusWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-vrchat-status-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Write_creates_the_status_file_with_expected_content()
    {
        var status = new VrchatStatus
        {
            IsRunning = true,
            WatchWorldsDetected = true,
            ProcessId = 1234,
            StartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        _writer.Write(status, _tempDir);

        var path = Path.Combine(_tempDir, "vrchat-status.json");
        File.Exists(path).Should().BeTrue();

        var parsed = JsonSerializer.Deserialize<VrchatStatus>(File.ReadAllText(path), DeserializeOptions);
        parsed!.IsRunning.Should().BeTrue();
        parsed.WatchWorldsDetected.Should().BeTrue();
        parsed.ProcessId.Should().Be(1234);
    }

    [Fact]
    public void Does_not_leave_tmp_file_behind()
    {
        _writer.Write(new VrchatStatus { UpdatedAtUtc = DateTimeOffset.UtcNow }, _tempDir);

        Directory.GetFiles(_tempDir).Should().ContainSingle()
            .Which.Should().Be(Path.Combine(_tempDir, "vrchat-status.json"));
    }

    [Fact]
    public void Creates_status_directory_if_missing()
    {
        var nestedDir = Path.Combine(_tempDir, "status");

        _writer.Write(new VrchatStatus { UpdatedAtUtc = DateTimeOffset.UtcNow }, nestedDir);

        File.Exists(Path.Combine(nestedDir, "vrchat-status.json")).Should().BeTrue();
    }

    [Fact]
    public void Overwrites_previous_status_on_repeated_writes()
    {
        _writer.Write(new VrchatStatus { IsRunning = false, UpdatedAtUtc = DateTimeOffset.UtcNow }, _tempDir);
        _writer.Write(new VrchatStatus { IsRunning = true, UpdatedAtUtc = DateTimeOffset.UtcNow }, _tempDir);

        var parsed = JsonSerializer.Deserialize<VrchatStatus>(
            File.ReadAllText(Path.Combine(_tempDir, "vrchat-status.json")), DeserializeOptions);
        parsed!.IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Keeps_updating_the_status_file_while_the_Mac_SMB_client_holds_it_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // On Unix, rename(2) replaces an open destination, so nothing is held open in that sense.
        }

        _writer.Write(new VrchatStatus { IsRunning = false, UpdatedAtUtc = DateTimeOffset.UtcNow }, _tempDir);
        var path = Path.Combine(_tempDir, "vrchat-status.json");
        using var macHandle = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);

        _writer.Write(new VrchatStatus { IsRunning = true, UpdatedAtUtc = DateTimeOffset.UtcNow }, _tempDir);

        macHandle.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(macHandle, leaveOpen: true);
        var parsed = JsonSerializer.Deserialize<VrchatStatus>(reader.ReadToEnd(), DeserializeOptions);
        parsed!.IsRunning.Should().BeTrue();
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(path);
    }
}

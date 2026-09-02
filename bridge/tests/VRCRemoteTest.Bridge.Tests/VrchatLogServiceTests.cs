using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Uses the internal poll-interval constructor so these tests complete in well
/// under a second, matching VrchatMonitorServiceTests' established pattern.
/// </summary>
public class VrchatLogServiceTests : IDisposable
{
    private readonly string _tempDir;

    public VrchatLogServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-logservice-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private VrchatLogService CreateService(FakeVrchatLogReader logReader) =>
        new(
            Options.Create(new BridgeOptions { StagingDirectory = _tempDir }),
            logReader,
            NullLogger<VrchatLogService>.Instance,
            TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task Writes_available_snapshots_to_logs_vrchat_latest_log()
    {
        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("hello world") };
        var service = CreateService(logReader);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(
            () => File.Exists(Path.Combine(_tempDir, "logs", "vrchat-latest.log")), TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        var path = Path.Combine(_tempDir, "logs", "vrchat-latest.log");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("hello world");
    }

    [Fact]
    public async Task Does_not_write_when_snapshot_is_unavailable()
    {
        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Empty() };
        var service = CreateService(logReader);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => logReader.ReadSnapshotCallCount >= 3, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        File.Exists(Path.Combine(_tempDir, "logs", "vrchat-latest.log")).Should().BeFalse();
    }

    [Fact]
    public async Task Continues_polling_after_the_log_reader_throws()
    {
        var logReader = new FakeVrchatLogReader
        {
            ThrowOnReadSnapshot = new InvalidOperationException("simulated read failure"),
        };
        var service = CreateService(logReader);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => logReader.ReadSnapshotCallCount >= 3, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        logReader.ReadSnapshotCallCount.Should().BeGreaterThanOrEqualTo(3);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
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

    private VrchatLogService CreateService(FakeVrchatLogReader logReader, ILogger<VrchatLogService>? logger = null) =>
        new(
            Options.Create(new BridgeOptions { StagingDirectory = _tempDir }),
            logReader,
            logger ?? NullLogger<VrchatLogService>.Instance,
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

    /// <summary>
    /// Regression for 2026-10-17: the Mac's SMB client kept the file open (sharing
    /// Read|Write|Delete), File.Move(overwrite: true) failed with
    /// UnauthorizedAccessException on every poll, the published log went stale, and an
    /// ERR with a stack trace was logged every 5s.
    /// </summary>
    [Fact]
    public async Task Keeps_updating_vrchat_latest_log_while_the_Mac_SMB_client_holds_it_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // On Unix, rename(2) replaces an open destination, so nothing is held open in that sense.
        }

        var path = Path.Combine(_tempDir, "logs", "vrchat-latest.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stale snapshot");
        using var macHandle = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);

        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("fresh snapshot") };
        var logger = new ListLogger<VrchatLogService>();
        var service = CreateService(logReader, logger);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => logReader.ReadSnapshotCallCount >= 3, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        File.ReadAllText(path).Should().Be("fresh snapshot");
        logger.Entries.Should().NotBeEmpty()
            .And.NotContain(e => e.Level >= LogLevel.Warning)
            .And.OnlyContain(e => e.Exception == null, "being held open is expected, not an error worth a stack trace");
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

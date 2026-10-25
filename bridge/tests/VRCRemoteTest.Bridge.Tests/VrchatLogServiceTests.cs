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

    private VrchatLogService CreateService(
        FakeVrchatLogReader logReader,
        ILogger<VrchatLogService>? logger = null,
        VrchatLogNotice? notice = null,
        TimeSpan? pollInterval = null) =>
        new(
            Options.Create(new BridgeOptions { StateDirectory = _tempDir }),
            logReader,
            new VrchatClientObserver(
                new BridgeOptions { StateDirectory = _tempDir },
                new VrchatClientRoster(filePath: null),
                managedClients: null,
                _ => DateTime.UtcNow,
                () => DateTimeOffset.UtcNow),
            notice ?? new VrchatLogNotice(),
            logger ?? NullLogger<VrchatLogService>.Instance,
            pollInterval ?? TimeSpan.FromMilliseconds(10));

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
        await WaitUntil(() => logReader.ReadCallCount >= 3, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        File.Exists(Path.Combine(_tempDir, "logs", "vrchat-latest.log")).Should().BeFalse();
    }

    [Fact]
    public async Task Continues_polling_after_the_log_reader_throws()
    {
        var logReader = new FakeVrchatLogReader
        {
            ThrowOnRead = new InvalidOperationException("simulated read failure"),
        };
        var service = CreateService(logReader);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => logReader.ReadCallCount >= 3, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        logReader.ReadCallCount.Should().BeGreaterThanOrEqualTo(3);
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
        await WaitUntil(() => logReader.ReadCallCount >= 3, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        File.ReadAllText(path).Should().Be("fresh snapshot");
        logger.Entries.Should().NotBeEmpty()
            .And.NotContain(e => e.Level >= LogLevel.Warning)
            .And.OnlyContain(e => e.Exception == null, "being held open is expected, not an error worth a stack trace");
    }

    // ---- VrchatLogNotice (Relaunch) ----

    /// <summary>Long enough that only a wake from the notice can explain a publish within a test.</summary>
    private static readonly TimeSpan NoPollDuringTest = TimeSpan.FromHours(1);

    private string PublishedPath => Path.Combine(_tempDir, "logs", "vrchat-latest.log");

    private string? Published() => PublishedFile.TryRead(PublishedPath);

    [Fact]
    public async Task Publishes_the_notice_instead_of_reading_the_log_while_one_is_shown()
    {
        var notice = new VrchatLogNotice();
        notice.Show("relaunching");
        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("closed client's log") };
        var service = CreateService(logReader, notice: notice);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => Published() == "relaunching", TimeSpan.FromSeconds(2));
        await Task.Delay(50); // several 10 ms polls
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Published().Should().Be("relaunching");
        logReader.ReadCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Showing_the_notice_replaces_the_published_log_without_waiting_for_the_next_poll()
    {
        var notice = new VrchatLogNotice();
        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("closed client's log") };
        var service = CreateService(logReader, notice: notice, pollInterval: NoPollDuringTest);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => Published() == "closed client's log", TimeSpan.FromSeconds(2));
        notice.Show("relaunching");
        await WaitUntil(() => Published() == "relaunching", TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Published().Should().Be("relaunching");
        logReader.ReadCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Clearing_the_notice_publishes_the_log_again_without_waiting_for_the_next_poll()
    {
        var notice = new VrchatLogNotice();
        notice.Show("relaunching");
        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("new client's log") };
        var service = CreateService(logReader, notice: notice, pollInterval: NoPollDuringTest);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => Published() == "relaunching", TimeSpan.FromSeconds(2));
        notice.Clear();
        await WaitUntil(() => Published() == "new client's log", TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Published().Should().Be("new client's log");
        logReader.ReadCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Clearing_a_notice_that_is_not_shown_does_not_wake_the_Log_Viewer()
    {
        // Every build clears it when it ends (StagingWatcher), Reload mode included.
        var notice = new VrchatLogNotice();
        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("log") };
        var service = CreateService(logReader, notice: notice, pollInterval: NoPollDuringTest);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => logReader.ReadCallCount == 1, TimeSpan.FromSeconds(2));
        notice.Clear();
        notice.Clear();
        await Task.Delay(100);
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        logReader.ReadCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Showing_and_clearing_the_notice_keep_working_while_the_share_cannot_be_written()
    {
        // A file where the logs directory should be: every log copy fails to publish.
        File.WriteAllText(Path.Combine(_tempDir, "logs"), "not a directory");
        var notice = new VrchatLogNotice();
        var logger = new ListLogger<VrchatLogService>();
        var logReader = new FakeVrchatLogReader { ResultToReturn = LogSnapshotResult.Success("log") };
        var service = CreateService(logReader, logger, notice, NoPollDuringTest);

        int Errors() => logger.Entries.Count(e => e.Level == LogLevel.Error);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => Errors() == 1, TimeSpan.FromSeconds(2));
        var show = () => notice.Show("relaunching");
        show.Should().NotThrow();

        // The notice goes to the four slot logs too, each failing once.
        await WaitUntil(() => Errors() == 5, TimeSpan.FromSeconds(2));
        notice.Clear();
        await WaitUntil(() => logReader.ReadCallCount == 2, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Errors().Should().Be(5, "each file's failure is logged once, not on every poll");
        logReader.ReadCallCount.Should().Be(2, "once before the notice and once after it");
        File.Exists(Path.Combine(_tempDir, "status", "vrchat-clients.json")).Should().BeTrue(
            "the files that can be written still are");
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

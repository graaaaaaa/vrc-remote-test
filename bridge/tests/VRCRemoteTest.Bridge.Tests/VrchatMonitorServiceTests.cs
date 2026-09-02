using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Uses the internal poll-interval constructor (test-only seam) to run many iterations
/// in well under a second, rather than waiting out a real 10s production interval.
/// </summary>
public class VrchatMonitorServiceTests
{
    private static VrchatMonitorService CreateService(
        FakeVrchatProcessMonitor processMonitor, FakeVrchatStatusWriter statusWriter, string stagingDir) =>
        new(
            Options.Create(new BridgeOptions { StagingDirectory = stagingDir }),
            processMonitor,
            statusWriter,
            NullLogger<VrchatMonitorService>.Instance,
            TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task Writes_status_to_the_configured_staging_directorys_status_subfolder()
    {
        var processMonitor = new FakeVrchatProcessMonitor { IsRunning = true, WatchWorldsDetected = true };
        var statusWriter = new FakeVrchatStatusWriter();
        var service = CreateService(processMonitor, statusWriter, @"C:\VRCRemoteTest");

        using var cts = new CancellationTokenSource();
        var runTask = service.StartAsync(cts.Token);
        await WaitUntil(() => statusWriter.WriteCallCount > 0, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        statusWriter.LastStatusDirectory.Should().Be(Path.Combine(@"C:\VRCRemoteTest", "status"));
        statusWriter.LastWrittenStatus!.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Continues_polling_after_the_process_monitor_throws()
    {
        var processMonitor = new FakeVrchatProcessMonitor
        {
            ThrowOnGetStatus = new InvalidOperationException("simulated WMI failure"),
        };
        var statusWriter = new FakeVrchatStatusWriter();
        var service = CreateService(processMonitor, statusWriter, @"C:\VRCRemoteTest");

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await WaitUntil(() => processMonitor.GetStatusCallCount >= 3, TimeSpan.FromSeconds(2));
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // The loop must have survived multiple exceptions rather than the host
        // treating the first one as fatal (Codex plan review Phase 4a, Round 3,
        // confidence 0.99).
        processMonitor.GetStatusCallCount.Should().BeGreaterThanOrEqualTo(3);
        statusWriter.WriteCallCount.Should().Be(0);
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

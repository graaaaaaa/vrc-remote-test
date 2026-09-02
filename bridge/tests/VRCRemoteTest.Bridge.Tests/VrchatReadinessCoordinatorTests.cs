using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Uses the internal (startupSettleDelay, pollInterval) constructor with tiny
/// TimeSpans so these tests complete in well under a second of real wall-clock
/// time, matching VrchatMonitorServiceTests' established pattern.
/// </summary>
public class VrchatReadinessCoordinatorTests
{
    private static readonly TimeSpan TestSettleDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TestPollInterval = TimeSpan.FromMilliseconds(5);

    private static VrchatReadinessCoordinator CreateCoordinator(
        FakeVrchatProcessMonitor processMonitor, FakeVrchatLauncher launcher, int startupTimeoutSeconds = 1) =>
        new(
            Options.Create(new BridgeOptions
            {
                VrchatExecutable = @"C:\VRChat\VRChat.exe",
                VrchatMode = "Desktop",
                VrchatStartupTimeoutSeconds = startupTimeoutSeconds,
            }),
            processMonitor,
            launcher,
            NullLogger<VrchatReadinessCoordinator>.Instance,
            TestSettleDelay,
            TestPollInterval);

    [Fact]
    public async Task Already_running_with_watch_worlds_long_enough_becomes_ready_without_launching()
    {
        var processMonitor = new FakeVrchatProcessMonitor
        {
            IsRunning = true,
            WatchWorldsDetected = true,
            ProcessId = 1234,
            StartTimeUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
        };
        var launcher = new FakeVrchatLauncher();
        var coordinator = CreateCoordinator(processMonitor, launcher);

        var result = await coordinator.EnsureReadyAsync(CancellationToken.None);

        result.IsReady.Should().BeTrue();
        launcher.LaunchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Already_running_without_watch_worlds_fails_without_attempting_to_launch()
    {
        var processMonitor = new FakeVrchatProcessMonitor
        {
            IsRunning = true,
            WatchWorldsDetected = false,
            ProcessId = 1234,
        };
        var launcher = new FakeVrchatLauncher();
        var coordinator = CreateCoordinator(processMonitor, launcher);

        var result = await coordinator.EnsureReadyAsync(CancellationToken.None);

        result.IsReady.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.VrchatWatchWorldsMissing);
        launcher.LaunchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Not_running_launches_and_becomes_ready_once_process_monitor_reports_watch_worlds()
    {
        var processMonitor = new FakeVrchatProcessMonitor { IsRunning = false };
        var launcher = new FakeVrchatLauncher
        {
            ShouldSucceed = true,
        };
        // Simulate VRChat becoming visible to WMI shortly after launch.
        launcher.OnLaunch = () =>
        {
            processMonitor.IsRunning = true;
            processMonitor.WatchWorldsDetected = true;
            processMonitor.ProcessId = 5678;
            processMonitor.StartTimeUtc = DateTimeOffset.UtcNow;
        };
        var coordinator = CreateCoordinator(processMonitor, launcher);

        var result = await coordinator.EnsureReadyAsync(CancellationToken.None);

        result.IsReady.Should().BeTrue();
        launcher.LaunchCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Launch_failure_returns_start_failed()
    {
        var processMonitor = new FakeVrchatProcessMonitor { IsRunning = false };
        var launcher = new FakeVrchatLauncher { ShouldSucceed = false };
        var coordinator = CreateCoordinator(processMonitor, launcher);

        var result = await coordinator.EnsureReadyAsync(CancellationToken.None);

        result.IsReady.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
    }

    [Fact]
    public async Task Never_becoming_ready_times_out()
    {
        var processMonitor = new FakeVrchatProcessMonitor { IsRunning = false };
        var launcher = new FakeVrchatLauncher { ShouldSucceed = true }; // launches, but monitor never reports ready
        var coordinator = CreateCoordinator(processMonitor, launcher, startupTimeoutSeconds: 1);

        var result = await coordinator.EnsureReadyAsync(CancellationToken.None);

        result.IsReady.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCode.VrchatStartTimeout);
    }

    [Fact]
    public async Task Missing_StartTimeUtc_still_reaches_ready_using_first_observed_time()
    {
        var processMonitor = new FakeVrchatProcessMonitor
        {
            IsRunning = true,
            WatchWorldsDetected = true,
            ProcessId = 999,
            StartTimeUtc = null, // simulates VrchatProcessMonitor.TryGetStartTime failure
        };
        var launcher = new FakeVrchatLauncher();
        var coordinator = CreateCoordinator(processMonitor, launcher);

        var result = await coordinator.EnsureReadyAsync(CancellationToken.None);

        result.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task External_cancellation_propagates_as_OperationCanceledException()
    {
        var processMonitor = new FakeVrchatProcessMonitor { IsRunning = false };
        var launcher = new FakeVrchatLauncher { ShouldSucceed = true };
        var coordinator = CreateCoordinator(processMonitor, launcher, startupTimeoutSeconds: 30);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        var act = async () => await coordinator.EnsureReadyAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

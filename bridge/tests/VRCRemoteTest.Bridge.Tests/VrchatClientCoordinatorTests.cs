using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Uses the internal (startupSettleDelay, pollInterval, launchDelay) constructor with
/// tiny TimeSpans and a recording delay, so these tests complete in well under a
/// second of real wall-clock time, matching VrchatMonitorServiceTests' established
/// pattern.
/// </summary>
public class VrchatClientCoordinatorTests
{
    private const string WorldPath = @"C:\Users\dev\AppData\LocalLow\VRChat\VRChat\Worlds\vrc-remote-b1.vrcw";

    private static readonly TimeSpan TestSettleDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TestPollInterval = TimeSpan.FromMilliseconds(5);

    private static readonly DateTime LongAgo = DateTime.UtcNow - TimeSpan.FromMinutes(5);

    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly FakeVrchatLauncher _launcher = new();
    private readonly CapturingLogger<VrchatClientCoordinator> _logger = new();
    private readonly List<string> _events = [];

    private VrchatClientCoordinator CreateCoordinator(
        int? clientCount = null,
        string mode = "Desktop",
        int startupTimeoutSeconds = 1,
        TimeSpan? settleDelay = null,
        Func<TimeSpan, CancellationToken, Task>? launchDelay = null)
    {
        _launcher.OnLaunch ??= pid => _events.Add($"launch {pid}");

        return new VrchatClientCoordinator(
            Options.Create(new BridgeOptions
            {
                VrchatExecutable = @"C:\VRChat\VRChat.exe",
                VrchatMode = mode,
                VrchatClientCount = clientCount,
                VrchatStartupTimeoutSeconds = startupTimeoutSeconds,
            }),
            _monitor,
            _launcher,
            _logger,
            settleDelay ?? TestSettleDelay,
            TestPollInterval,
            launchDelay ?? ((delay, _) =>
            {
                _events.Add($"wait {delay.TotalSeconds}s");
                return Task.CompletedTask;
            }));
    }

    [Fact]
    public async Task No_VRChat_running_is_a_cold_start_that_launches_nothing_before_the_deploy()
    {
        var coordinator = CreateCoordinator(clientCount: 2);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        _launcher.LaunchCalled.Should().BeFalse("the build is installed before any client starts");
    }

    [Fact]
    public async Task Cold_start_launches_N_clients_three_seconds_apart_with_one_shared_room_id()
    {
        var coordinator = CreateCoordinator(clientCount: 3);

        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        _events.Should().Equal("launch 5000", "wait 3s", "launch 5001", "wait 3s", "launch 5002");
        _launcher.Requests.Should().HaveCount(3);
        _launcher.Requests.Select(r => r.RoomId).Distinct().Should().ContainSingle()
            .Which.Should().MatchRegex("^[0-9]{10}$");
        _launcher.Requests.Should().AllSatisfy(r =>
        {
            r.WorldPath.Should().Be(WorldPath);
            r.ExecutablePath.Should().Be(@"C:\VRChat\VRChat.exe");
            r.NoVr.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Each_launch_batch_gets_a_new_room_id()
    {
        var coordinator = CreateCoordinator(clientCount: 1);

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);
        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        // Ten random digits; a collision is a 1 in 10^10 event.
        _launcher.Requests[0].RoomId.Should().NotBe(_launcher.Requests[1].RoomId);
    }

    [Fact]
    public async Task VR_mode_gives_the_HMD_to_the_first_client_only()
    {
        var coordinator = CreateCoordinator(clientCount: 3, mode: "vr");

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        _launcher.Requests.Select(r => r.NoVr).Should().Equal(false, true, true);
    }

    [Fact]
    public async Task A_single_client_launches_once_without_waiting()
    {
        var coordinator = CreateCoordinator();

        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        _events.Should().Equal("launch 5000");
    }

    [Fact]
    public async Task A_failed_launch_stops_the_batch_and_reports_how_many_started()
    {
        _launcher.FailingLaunchIndexes = [1];
        var coordinator = CreateCoordinator(clientCount: 3);

        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain("Launched 1 of 3");
        _launcher.Requests.Should().HaveCount(2, "no client is launched after the first failure");
    }

    [Fact]
    public async Task A_launcher_exception_becomes_a_failed_outcome()
    {
        _launcher.ThrowOnLaunch = new InvalidOperationException("simulated failure");
        var coordinator = CreateCoordinator(clientCount: 2);

        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain("Launched 0 of 2").And.Contain("simulated failure");
    }

    [Fact]
    public async Task Cancellation_during_the_launch_interval_propagates_and_launches_no_further_client()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            clientCount: 3,
            launchDelay: (_, token) =>
            {
                cts.Cancel();
                return Task.Delay(Timeout.Infinite, token);
            });

        var act = async () => await coordinator.LaunchClientsAsync(WorldPath, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Running_watch_worlds_clients_that_settled_long_ago_deploy_without_launching()
    {
        _monitor.AddClient(1234, LongAgo);
        _monitor.AddClient(1235, LongAgo + TimeSpan.FromSeconds(3));
        var coordinator = CreateCoordinator(clientCount: 2);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
        _launcher.LaunchCalled.Should().BeFalse();
        _logger.MessagesAt(LogLevel.Warning).Should().BeEmpty("the configured number of clients is running");
    }

    [Fact]
    public async Task Fewer_clients_than_configured_deploy_without_adding_any_and_warn_once_per_change()
    {
        _monitor.AddClient(1234, LongAgo);
        var coordinator = CreateCoordinator(clientCount: 2);

        var first = await coordinator.PrepareAsync(CancellationToken.None);
        var second = await coordinator.PrepareAsync(CancellationToken.None);

        first.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
        second.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
        _launcher.LaunchCalled.Should().BeFalse("a client started with --url would land in another instance");
        _logger.MessagesAt(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("1 of 2");

        _monitor.AddClient(1236, LongAgo);
        await coordinator.PrepareAsync(CancellationToken.None);
        _monitor.Clients.RemoveAt(0);
        await coordinator.PrepareAsync(CancellationToken.None);

        _logger.MessagesAt(LogLevel.Warning).Should().HaveCount(2, "the mix changed back to 1 of 2 after being normal");
    }

    [Fact]
    public async Task Clients_without_watch_worlds_next_to_one_with_it_still_deploy_with_a_warning()
    {
        _monitor.AddClient(1234, LongAgo);
        _monitor.AddClient(1235, LongAgo, watchWorlds: false);
        var coordinator = CreateCoordinator(clientCount: 1);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
        _logger.MessagesAt(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("without --watch-worlds will not reload");
    }

    [Fact]
    public async Task Only_clients_without_watch_worlds_fail_without_launching_or_restarting()
    {
        _monitor.AddClient(1234, LongAgo, watchWorlds: false);
        var coordinator = CreateCoordinator(clientCount: 2);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.Failed);
        preparation.ErrorCode.Should().Be(ErrorCode.VrchatWatchWorldsMissing);
        _launcher.LaunchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task An_unreadable_process_list_fails_as_unmanaged_instead_of_cold_starting()
    {
        _monitor.EnumerationError = "access denied";
        var coordinator = CreateCoordinator();

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.Failed);
        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        preparation.ErrorMessage.Should().Contain("Close it manually");
    }

    [Fact]
    public async Task A_VRChat_that_could_not_be_checked_blocks_the_cold_start()
    {
        _monitor.UnverifiedCount = 1;
        var coordinator = CreateCoordinator();

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.Failed);
        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
    }

    [Fact]
    public async Task A_VRChat_that_could_not_be_checked_does_not_block_a_deploy_to_running_clients()
    {
        _monitor.AddClient(1234, LongAgo);
        _monitor.UnverifiedCount = 1;
        var coordinator = CreateCoordinator();

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
        _logger.MessagesAt(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("could not be checked");
    }

    [Fact]
    public async Task Waits_until_every_client_is_past_the_settle_delay()
    {
        var settleDelay = TimeSpan.FromMilliseconds(300);
        _monitor.AddClient(1234, LongAgo);
        _monitor.AddClient(1235, DateTime.UtcNow); // just launched, e.g. by a cold start
        var coordinator = CreateCoordinator(clientCount: 2, settleDelay: settleDelay);
        var started = DateTime.UtcNow;

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
        (DateTime.UtcNow - started).Should().BeGreaterThan(settleDelay - TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task A_client_set_that_keeps_changing_never_becomes_ready_and_times_out()
    {
        _monitor.AddClient(1234, LongAgo);
        _monitor.OnGetClients = call => _monitor.Clients.Add(new VrchatClientInfo(2000 + call, LongAgo, true));
        var coordinator = CreateCoordinator(clientCount: 2, startupTimeoutSeconds: 1);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.Failed);
        preparation.ErrorCode.Should().Be(ErrorCode.VrchatStartTimeout);
    }

    [Fact]
    public async Task A_reused_PID_with_another_creation_time_counts_as_a_different_client()
    {
        // Same PIDs on every poll, but one creation time alternates, so the set of
        // (PID, creation time) never repeats and readiness is never reached.
        _monitor.AddClient(1234, LongAgo);
        _monitor.OnGetClients = call =>
            _monitor.Clients[0] = new VrchatClientInfo(1234, LongAgo + TimeSpan.FromSeconds(call % 2), true);
        var coordinator = CreateCoordinator(startupTimeoutSeconds: 1);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatStartTimeout);
    }

    [Fact]
    public async Task A_missing_creation_time_still_reaches_ready_using_the_first_observed_time()
    {
        _monitor.AddClient(999, creationTimeUtc: null);
        var coordinator = CreateCoordinator();

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
    }

    [Fact]
    public async Task An_unreadable_process_list_during_the_wait_fails_as_unmanaged()
    {
        _monitor.AddClient(1234, LongAgo);
        _monitor.OnGetClients = call =>
        {
            if (call == 2)
            {
                _monitor.EnumerationError = "transient failure";
            }
        };
        var coordinator = CreateCoordinator();

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
    }

    [Fact]
    public async Task External_cancellation_during_the_wait_propagates_as_OperationCanceledException()
    {
        _monitor.AddClient(1234, DateTime.UtcNow);
        var coordinator = CreateCoordinator(startupTimeoutSeconds: 30, settleDelay: TimeSpan.FromMinutes(1));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(20));

        var act = async () => await coordinator.PrepareAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

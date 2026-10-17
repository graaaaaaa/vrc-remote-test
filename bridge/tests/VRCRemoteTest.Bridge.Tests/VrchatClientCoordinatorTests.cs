using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Uses the internal (startupSettleDelay, pollInterval, launchDelay, ...) constructor with
/// tiny TimeSpans and a recording delay, so these tests complete in well under a
/// second of real wall-clock time, matching VrchatMonitorServiceTests' established
/// pattern. The launch pacing runs on a fake clock that only the recording delay
/// advances, against a fake log directory in which each launched client creates its
/// output_log a set time after it starts.
/// </summary>
public class VrchatClientCoordinatorTests
{
    private const string WorldPath = @"C:\Users\dev\AppData\LocalLow\VRChat\VRChat\Worlds\vrc-remote-b1.vrcw";

    private static readonly TimeSpan TestSettleDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan TestPollInterval = TimeSpan.FromMilliseconds(5);

    private static readonly DateTime LongAgo = DateTime.UtcNow - TimeSpan.FromMinutes(5);

    /// <summary>The fake clock's start, a tenth into a second like a typical launch.</summary>
    private static readonly DateTime T0 = new(2026, 10, 17, 9, 0, 0, 100, DateTimeKind.Utc);

    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly FakeVrchatLauncher _launcher = new();
    private readonly CapturingLogger<VrchatClientCoordinator> _logger = new();
    private readonly List<string> _events = [];

    private DateTime _now = T0;

    /// <summary>
    /// Seconds from each launch (in launch order) until that client creates its
    /// output_log; null for a client that never does. Clients past the end of the list
    /// take 0.8 s, as on real hardware without load.
    /// </summary>
    private readonly List<double?> _logDelaysSeconds = [];

    /// <summary>Seconds by which a file's name time precedes its creation (under load, 18-04-10 was created at 18:04:11).</summary>
    private double _nameLeadSeconds;

    /// <summary>Files not created by the launched clients: earlier sessions, or a VRChat started by hand.</summary>
    private readonly List<(DateTime NameUtc, DateTime CreatedUtc)> _otherLogs = [];

    private bool _logDirectoryUnreadable;

    private VrchatClientCoordinator CreateCoordinator(
        int? clientCount = null,
        string mode = "Desktop",
        int startupTimeoutSeconds = 1,
        TimeSpan? settleDelay = null,
        Func<TimeSpan, CancellationToken, Task>? launchDelay = null)
    {
        _launcher.OnLaunch ??= pid => _events.Add($"launch {pid}");
        _launcher.Clock = () => _now;

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
                _now += delay;
                return Task.CompletedTask;
            }),
            ListLogFiles,
            () => _now,
            TimeZoneInfo.Utc);
    }

    /// <summary>
    /// The files that exist at the fake clock's time. Clients that name their file in the
    /// same second share it (spike option B): the first one creates it.
    /// </summary>
    private IReadOnlyList<VrchatLogMatcher.Candidate>? ListLogFiles()
    {
        if (_logDirectoryUnreadable)
        {
            return null;
        }

        var files = _otherLogs.Where(f => f.CreatedUtc <= _now).ToList();
        for (var i = 0; i < _launcher.Launched.Count; i++)
        {
            var delay = i < _logDelaysSeconds.Count ? _logDelaysSeconds[i] : 0.8;
            if (delay is not { } seconds)
            {
                continue;
            }

            // The name is the time VRChat starts logging, never before the process start.
            var startedUtc = _launcher.Launched[i].CreationTimeUtc;
            var createdUtc = startedUtc.AddSeconds(seconds);
            if (createdUtc <= _now)
            {
                var namedUtc = createdUtc.AddSeconds(-_nameLeadSeconds);
                files.Add((namedUtc > startedUtc ? namedUtc : startedUtc, createdUtc));
            }
        }

        return files
            .OrderBy(f => f.CreatedUtc)
            .Select(f => LogFile(f.NameUtc, f.CreatedUtc))
            .DistinctBy(f => f.Path)
            .ToList();
    }

    private static VrchatLogMatcher.Candidate LogFile(DateTime nameUtc, DateTime createdUtc)
    {
        var name = $"output_log_{nameUtc:yyyy-MM-dd_HH-mm-ss}.txt";
        return new VrchatLogMatcher.Candidate(name, VrchatLogMatcher.ParseNameTime(name), createdUtc);
    }

    /// <summary>Seconds from T0 at which each client was launched.</summary>
    private IEnumerable<double> LaunchOffsets() =>
        _launcher.Launched.Select(l => (l.CreationTimeUtc - T0).TotalSeconds);

    /// <summary>The launched clients as the process monitor would report them.</summary>
    private List<VrchatClientInfo> LaunchedClients() =>
        _launcher.Launched.Select(l => new VrchatClientInfo(l.ProcessId, l.CreationTimeUtc, true)).ToList();

    /// <summary>The file the Log Viewer would show for <paramref name="client"/> once every file exists.</summary>
    private string? MatchedLog(VrchatClientInfo client, IReadOnlyList<VrchatClientInfo> clients)
    {
        _now += TimeSpan.FromMinutes(1);
        return VrchatLogMatcher.Match(client, clients, ListLogFiles()!, TimeZoneInfo.Utc);
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

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_stop_requested_before_the_first_launch_launches_nothing(int clientCount)
    {
        var coordinator = CreateCoordinator(clientCount: clientCount);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await coordinator.LaunchClientsAsync(WorldPath, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task A_stop_requested_during_the_last_launch_is_not_reported_as_success(int clientCount)
    {
        using var cts = new CancellationTokenSource();
        _launcher.OnLaunch = _ =>
        {
            if (_launcher.Requests.Count == clientCount)
            {
                cts.Cancel();
            }
        };
        var coordinator = CreateCoordinator(clientCount: clientCount);

        var act = async () => await coordinator.LaunchClientsAsync(WorldPath, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.Requests.Should().HaveCount(clientCount, "clients that already started are left running");
    }

    [Fact]
    public async Task A_stop_requested_during_the_interval_launches_no_further_client_even_if_the_wait_returns()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            clientCount: 3,
            launchDelay: (_, _) =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            });

        var act = async () => await coordinator.LaunchClientsAsync(WorldPath, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_load_each_log_exists_by_the_end_of_the_interval_so_clients_start_three_seconds_apart()
    {
        var coordinator = CreateCoordinator(clientCount: 4);

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        LaunchOffsets().Should().Equal(0, 3, 6, 9);
        var clients = LaunchedClients();
        clients.Select(c => MatchedLog(c, clients)).Should().Equal(
            "output_log_2026-10-17_09-00-00.txt",
            "output_log_2026-10-17_09-00-03.txt",
            "output_log_2026-10-17_09-00-06.txt",
            "output_log_2026-10-17_09-00-09.txt");
        _logger.MessagesAt(LogLevel.Information).Count(m => m.Contains("New output_log")).Should().Be(3);
        _logger.MessagesAt(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task Under_heavy_load_each_launch_waits_for_the_previous_clients_log_so_every_client_keeps_its_own()
    {
        // Real hardware at about 100% CPU: logs 11-22 s after the start, and clients
        // started 3 s apart then shared one file (multi-client-spike.md, PR #2, item 12).
        _logDelaysSeconds.AddRange([22, 20, 20]);
        var coordinator = CreateCoordinator(clientCount: 3);

        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();

        // Client 1's file is named 09:00:22, so client 2 starts at 09:00:24.000, the first
        // time whose range (from 09:00:23) leaves that name out; likewise for client 3.
        LaunchOffsets().Should().Equal(0, 23.9, 45.9);
        var clients = LaunchedClients();
        clients.Select(c => MatchedLog(c, clients)).Should().Equal(
            "output_log_2026-10-17_09-00-22.txt",
            "output_log_2026-10-17_09-00-44.txt",
            "output_log_2026-10-17_09-01-06.txt");
        _logger.MessagesAt(LogLevel.Information).Should().Contain(m => m.Contains("Waiting for VRChat client 1/3"));
        _logger.MessagesAt(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_from_the_second_before_the_creation_time_still_keeps_the_logs_apart()
    {
        // Under load, output_log_..._18-04-10.txt was created at 18:04:11.
        _nameLeadSeconds = 1;
        _logDelaysSeconds.AddRange([11, 11]);
        var coordinator = CreateCoordinator(clientCount: 3);

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        var clients = LaunchedClients();
        MatchedLog(clients[0], clients).Should().Be("output_log_2026-10-17_09-00-10.txt");
        LaunchOffsets().Should().Equal(0, 11.9, 23.9);
    }

    [Fact]
    public async Task A_log_named_just_before_the_interval_ends_delays_the_next_launch_until_its_name_is_out_of_range()
    {
        // Created 2.5 s after a start at x.1, the file is named in the second that the
        // next client's range would begin with if it started after exactly 3 s.
        _logDelaysSeconds.Add(2.5);
        var coordinator = CreateCoordinator(clientCount: 2);

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        _events.Should().Equal("launch 5000", "wait 3s", "wait 0.9s", "launch 5001");
        var clients = LaunchedClients();
        MatchedLog(clients[0], clients).Should().Be("output_log_2026-10-17_09-00-02.txt");
    }

    [Fact]
    public async Task Files_from_earlier_sessions_do_not_end_the_wait()
    {
        _otherLogs.Add((T0.AddHours(-1), T0.AddHours(-1)));
        _otherLogs.Add((T0.AddSeconds(-2), T0.AddSeconds(-2)));
        _logDelaysSeconds.Add(10);
        var coordinator = CreateCoordinator(clientCount: 2);

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        LaunchOffsets().Should().Equal(0, 11.9);
    }

    [Fact]
    public async Task A_client_that_never_creates_a_log_holds_the_batch_for_at_most_the_budget()
    {
        _logDelaysSeconds.Add(null);
        var coordinator = CreateCoordinator(clientCount: 4);

        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        LaunchOffsets().Should().Equal(0, 60, 63, 66);
        _logger.MessagesAt(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("VRChat client 1/4 has not created an output_log file within 60s");
    }

    [Fact]
    public async Task Each_launch_batch_has_its_own_wait_budget()
    {
        _logDelaysSeconds.AddRange([null, null, 10, 0.8]);
        var coordinator = CreateCoordinator(clientCount: 2);

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);
        var secondBatchStart = _now;
        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        (_launcher.Launched[3].CreationTimeUtc - secondBatchStart).TotalSeconds.Should().Be(11.9);
    }

    [Fact]
    public async Task An_unreadable_log_directory_falls_back_to_the_fixed_interval_with_a_warning()
    {
        _logDirectoryUnreadable = true;
        var coordinator = CreateCoordinator(clientCount: 3);

        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        _events.Should().Equal("launch 5000", "wait 3s", "launch 5001", "wait 3s", "launch 5002");
        _logger.MessagesAt(LogLevel.Warning).Should().ContainSingle()
            .Which.Should().Contain("Could not list the VRChat log directory");
    }

    [Fact]
    public async Task A_log_directory_that_fails_during_the_wait_keeps_waiting_within_the_budget()
    {
        _logDelaysSeconds.Add(8);
        var failures = 0;
        var coordinator = CreateCoordinator(
            clientCount: 2,
            launchDelay: (delay, _) =>
            {
                _now += delay;
                _logDirectoryUnreadable = failures++ < 3;
                return Task.CompletedTask;
            });

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        LaunchOffsets().Should().Equal(0, 9.9);
    }

    [Fact]
    public async Task A_stop_requested_while_waiting_for_a_log_launches_no_further_client()
    {
        _logDelaysSeconds.Add(null);
        using var cts = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            clientCount: 3,
            launchDelay: (delay, token) =>
            {
                _now += delay;
                if (delay == VrchatClientCoordinator.LogPollInterval)
                {
                    cts.Cancel();
                }

                return token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;
            });

        var act = async () => await coordinator.LaunchClientsAsync(WorldPath, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_log_from_another_VRChat_can_end_the_wait_early_but_is_never_matched_to_the_first_client()
    {
        // A VRChat started by hand 5 s after the first client creates its file at once;
        // the first client's own file is 22 s late.
        var manualStart = T0.AddSeconds(4.9);
        _otherLogs.Add((manualStart.AddSeconds(0.8), manualStart.AddSeconds(0.8)));
        _logDelaysSeconds.AddRange([22, 0.8]);
        var coordinator = CreateCoordinator(clientCount: 2);

        await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        LaunchOffsets().Should().Equal([0, 6.9], "the other VRChat's file ended the wait");
        var launched = LaunchedClients();
        var manual = new VrchatClientInfo(4242, manualStart, WatchWorldsDetected: false);
        List<VrchatClientInfo> clients = [launched[0], manual, launched[1]];
        MatchedLog(launched[0], clients).Should().BeNull("client 1's own file is named after the next client started");
        MatchedLog(manual, clients).Should().Be("output_log_2026-10-17_09-00-05.txt");
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
        var justLaunched = DateTime.UtcNow; // e.g. by a cold start
        _monitor.AddClient(1234, LongAgo);
        _monitor.AddClient(1235, justLaunched);
        var coordinator = CreateCoordinator(clientCount: 2, settleDelay: settleDelay);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.DeployOnly);
        (DateTime.UtcNow - justLaunched).Should().BeGreaterThanOrEqualTo(settleDelay);
    }

    [Fact]
    public async Task A_stable_client_that_has_not_settled_never_becomes_ready()
    {
        // Without the settle check, two identical polls would already be enough.
        _monitor.AddClient(1234, DateTime.UtcNow.AddHours(1));
        var coordinator = CreateCoordinator(startupTimeoutSeconds: 1);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatStartTimeout);
        _monitor.GetClientsCallCount.Should().BeGreaterThan(3, "the same set was observed many times");
    }

    [Fact]
    public async Task An_observation_that_returns_after_the_deadline_is_not_ready()
    {
        _monitor.AddClient(1234, LongAgo);
        _monitor.OnGetClients = call =>
        {
            if (call == 3) // the second poll of the wait, which would otherwise make it ready
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(1200));
            }
        };
        var coordinator = CreateCoordinator(startupTimeoutSeconds: 1);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatStartTimeout);
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
        _monitor.GetClientsCallCount.Should().BeGreaterThan(3, "the set was observed changing, not observed once");
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
        _monitor.GetClientsCallCount.Should().BeGreaterThan(3);
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

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Relaunch mode (multi-client plan section 13.3) over fake processes, a fake record and
/// a fake clock that only the coordinator's own waits advance. The log directory is
/// reported as unreadable, so launches keep the fixed 3 s interval (the pacing itself is
/// covered by VrchatClientCoordinatorTests).
/// </summary>
public class VrchatRelaunchCoordinatorTests
{
    private const string WorldPath = @"C:\Users\dev\AppData\LocalLow\VRChat\VRChat\Worlds\vrc-remote-b1.vrcw";

    private static readonly DateTime T0 = new(2026, 10, 17, 9, 0, 0, 100, DateTimeKind.Utc);

    /// <summary>Created a while ago with a sub-millisecond part, like a real FILETIME.</summary>
    private static readonly DateTime Earlier = T0.AddMinutes(-3).AddTicks(1_234_567);

    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly FakeVrchatLauncher _launcher = new();
    private readonly FakeManagedClientStore _store = new();
    private readonly CapturingLogger<VrchatRelaunchCoordinator> _logger = new();
    private readonly VrchatLogNotice _logNotice = new();
    private DateTime _now = T0;

    private VrchatRelaunchCoordinator CreateCoordinator(
        int[]? profiles = null,
        string mode = "Desktop",
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _launcher.Clock = () => _now;
        _launcher.OnLaunch ??= pid => Running(pid, _launcher.Launched[^1].CreationTimeUtc);

        return new VrchatRelaunchCoordinator(
            Options.Create(new BridgeOptions
            {
                VrchatExecutable = @"C:\VRChat\VRChat.exe",
                VrchatMode = mode,
                AutoLaunchVrchat = true,
                VrchatClientMode = "Relaunch",
                VrchatProfiles = profiles ?? [0, 1],
            }),
            _monitor,
            _launcher,
            _store,
            _logNotice,
            _logger,
            delay ?? ((wait, _) =>
            {
                _now += wait;
                return Task.CompletedTask;
            }),
            () => null,
            () => _now,
            TimeZoneInfo.Utc);
    }

    private FakeVrchatProcess Running(int processId, DateTime creationTimeUtc, int sessionId = 1, string name = "VRChat")
    {
        var process = _monitor.AddProcess(processId, creationTimeUtc, sessionId, name);
        process.Clock = () => _now;
        return process;
    }

    private void Recorded(int processId, DateTime creationTimeUtc, int profile = 0, int launchOrder = 0, int sessionId = 1) =>
        _store.Clients.Add(new ManagedClient(processId, creationTimeUtc, sessionId, profile, launchOrder));

    private IEnumerable<FakeVrchatProcess.Handle> AllHandles => _monitor.Processes.SelectMany(p => p.Handles);

    // ---- PrepareAsync: steps 2-4 ----

    [Fact]
    public async Task With_no_VRChat_and_no_record_it_cold_starts_after_writing_an_empty_record()
    {
        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        _store.Saved.Should().ContainSingle().Which.Should().BeEmpty();
        _monitor.OpenClientsCallCount.Should().Be(2, "it enumerates again after closing");
    }

    [Fact]
    public async Task Recorded_clients_are_closed_through_the_handles_they_were_matched_with()
    {
        var first = Running(1001, Earlier);
        var second = Running(1002, Earlier.AddSeconds(3));
        Recorded(1001, Earlier, profile: 0, launchOrder: 0);
        Recorded(1002, Earlier.AddSeconds(3), profile: 1, launchOrder: 1);

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        first.IsRunning.Should().BeFalse();
        second.IsRunning.Should().BeFalse();
        first.KillCalls.Should().Be(0);
        first.Handles.Should().ContainSingle("closing never reopens the process").Which.Enumeration.Should().Be(1);
        AllHandles.Should().AllSatisfy(h => h.Disposed.Should().BeTrue());
        _store.Clients.Should().BeEmpty();
    }

    [Fact]
    public async Task Relaunch_does_not_wait_for_the_watch_worlds_settle_delay()
    {
        // Clients launched a second ago: Reload mode would wait 30 s before deploying.
        Running(1001, T0.AddSeconds(-1));
        Recorded(1001, T0.AddSeconds(-1));

        await CreateCoordinator().PrepareAsync(CancellationToken.None);

        (_now - T0).Should().Be(TimeSpan.FromSeconds(2), "only the 2 s the client took to close");
    }

    [Fact]
    public async Task A_client_with_the_recorded_PID_but_another_creation_time_is_not_the_Bridges()
    {
        var reused = Running(1001, Earlier.AddTicks(1));
        Recorded(1001, Earlier);

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.Failed);
        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        preparation.ErrorMessage.Should().Contain("1001");
        reused.CloseCalls.Should().Be(0);
        reused.KillCalls.Should().Be(0);
        _store.Saved.Should().BeEmpty("the record is left as it was");
    }

    [Fact]
    public async Task A_client_in_another_session_than_recorded_is_not_the_Bridges()
    {
        var process = Running(1001, Earlier, sessionId: 1);
        Recorded(1001, Earlier, sessionId: 2);

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        process.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_process_not_named_VRChat_is_never_the_Bridges()
    {
        var process = Running(1001, Earlier, name: "VRChatHelper");
        Recorded(1001, Earlier);

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        process.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_VRChat_started_by_hand_stops_the_build_before_any_client_is_closed()
    {
        var managed = Running(1001, Earlier);
        Recorded(1001, Earlier);
        var byHand = Running(2002, Earlier.AddSeconds(30));

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        preparation.ErrorMessage.Should().Contain("2002").And.Contain("Nothing was closed");
        managed.CloseCalls.Should().Be(0);
        byHand.CloseCalls.Should().Be(0);
        AllHandles.Should().AllSatisfy(h => h.Disposed.Should().BeTrue());
    }

    [Fact]
    public async Task A_process_list_that_cannot_be_read_stops_the_build()
    {
        Recorded(1001, Earlier);
        _monitor.OpenEnumerationError = "access denied";

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        _store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_VRChat_that_cannot_be_checked_stops_the_build_before_any_client_is_closed()
    {
        var managed = Running(1001, Earlier);
        Recorded(1001, Earlier);
        _monitor.OpenUnverifiedCount = 1;

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        preparation.ErrorMessage.Should().Contain("could not be checked");
        managed.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_corrupt_record_means_no_running_client_is_the_Bridges()
    {
        var process = Running(1001, Earlier);
        _store.LoadResult = ManagedClientLoadResult.Corrupt("it is not a valid record");

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        preparation.ErrorMessage.Should().Contain("corrupt");
        process.CloseCalls.Should().Be(0);
        _logger.MessagesAt(LogLevel.Warning).Should().Contain(m => m.Contains("not a valid record"));
    }

    [Fact]
    public async Task A_corrupt_record_with_no_VRChat_running_is_replaced_and_the_build_cold_starts()
    {
        _store.LoadResult = ManagedClientLoadResult.Corrupt("it is not a valid record");

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        _store.Saved.Should().ContainSingle().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_that_cannot_be_read_closes_nothing_and_fails_with_UnknownError()
    {
        var process = Running(1001, Earlier);
        Recorded(1001, Earlier);
        _store.LoadResult = ManagedClientLoadResult.Unreadable("Access to the path is denied.");

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.UnknownError);
        preparation.ErrorMessage.Should().Contain("could not be read").And.Contain(_store.FilePath);
        process.CloseCalls.Should().Be(0);
        _monitor.OpenClientsCallCount.Should().Be(0);
        _store.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_client_whose_exit_cannot_be_confirmed_stops_the_build_and_keeps_its_record()
    {
        var closes = Running(1001, Earlier);
        var stuck = Running(1002, Earlier.AddSeconds(3));
        stuck.CloseTakes = null;
        stuck.IgnoresKill = true;
        Recorded(1001, Earlier, profile: 0, launchOrder: 0);
        Recorded(1002, Earlier.AddSeconds(3), profile: 1, launchOrder: 1);

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.Failed);
        preparation.ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        preparation.ErrorMessage.Should().Contain("Could not close 1 of 2").And.Contain("1002");
        stuck.KillCalls.Should().Be(1);
        _store.Clients.Should().ContainSingle().Which.ProcessId.Should().Be(1002);
        _monitor.OpenClientsCallCount.Should().Be(1, "nothing is deployed, so there is nothing to check again");
    }

    [Fact]
    public async Task A_VRChat_that_starts_while_the_clients_close_stops_the_build()
    {
        Running(1001, Earlier);
        Recorded(1001, Earlier);
        _monitor.OnOpenClients = call =>
        {
            if (call == 2)
            {
                Running(3003, _now);
            }
        };

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        preparation.ErrorMessage.Should().Contain("3003").And.Contain("own clients were closed");
        _monitor.Processes.Single(p => p.ProcessId == 3003).CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_stop_request_while_closing_kills_nothing_more_and_keeps_only_the_unconfirmed_records()
    {
        using var cts = new CancellationTokenSource();
        Running(1001, Earlier).CloseTakes = TimeSpan.FromSeconds(1);
        var stuck = Running(1002, Earlier.AddSeconds(3));
        stuck.CloseTakes = null;
        Recorded(1001, Earlier, profile: 0, launchOrder: 0);
        Recorded(1002, Earlier.AddSeconds(3), profile: 1, launchOrder: 1);
        var coordinator = CreateCoordinator(delay: (wait, token) =>
        {
            _now += wait;
            if (_now - T0 >= TimeSpan.FromSeconds(4))
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        });

        var act = async () => await coordinator.PrepareAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stuck.KillCalls.Should().Be(0);
        _store.Clients.Should().ContainSingle().Which.ProcessId.Should().Be(1002);
    }

    [Fact]
    public async Task A_stop_request_after_every_client_closed_still_deploys_nothing()
    {
        using var cts = new CancellationTokenSource();
        var process = Running(1001, Earlier);
        process.CloseTakes = TimeSpan.Zero;
        process.OnCloseMainWindow = cts.Cancel;
        Recorded(1001, Earlier);

        var act = async () => await CreateCoordinator().PrepareAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Clients.Should().BeEmpty();
        _monitor.OpenClientsCallCount.Should().Be(1);
    }

    [Fact]
    public async Task A_record_that_cannot_be_updated_after_closing_stops_the_build()
    {
        Running(1001, Earlier);
        Recorded(1001, Earlier);
        _store.FailingSaves = [0];

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.UnknownError);
        preparation.ErrorMessage.Should().Contain("could not update its record");
    }

    [Fact]
    public async Task Entries_for_clients_that_are_gone_are_dropped()
    {
        Recorded(1001, Earlier); // closed by hand since

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        _store.Clients.Should().BeEmpty();
    }

    // ---- LaunchClientsAsync: step 7 ----

    [Fact]
    public async Task Launches_one_client_per_profile_in_order_with_url_and_profile_but_no_watch_worlds()
    {
        var outcome = await CreateCoordinator(profiles: [3, 0, 7]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        _launcher.Requests.Select(r => r.Profile).Should().Equal(3, 0, 7);
        _launcher.Requests.Should().AllSatisfy(r =>
        {
            r.WatchWorlds.Should().BeFalse();
            r.WorldPath.Should().Be(WorldPath);
            r.NoVr.Should().BeTrue();
        });
        _launcher.Requests.Select(r => r.RoomId).Distinct().Should().ContainSingle();

        var arguments = VrchatLauncher.BuildArguments(_launcher.Requests[0]);
        arguments.Should().NotContain("--watch-worlds");
        arguments[0].Should().StartWith("--url=create?roomId=");
        arguments.Should().EndWith("--profile=3");
    }

    [Fact]
    public async Task Each_client_is_recorded_before_the_next_one_is_launched()
    {
        var recordedAtLaunch = new List<int[]>();
        _launcher.OnLaunch = pid =>
        {
            recordedAtLaunch.Add(_store.Clients.Select(c => c.ProcessId).ToArray());
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };

        await CreateCoordinator(profiles: [0, 1, 2]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        var pids = _launcher.Launched.Select(l => l.ProcessId).ToArray();
        recordedAtLaunch.Should().BeEquivalentTo(
            new[] { Array.Empty<int>(), pids[..1], pids[..2] }, o => o.WithStrictOrdering());
        _store.Saved.Should().HaveCount(3);
        _store.Clients.Should().Equal(_launcher.Launched.Select((l, i) =>
            new ManagedClient(l.ProcessId, l.CreationTimeUtc, SessionId: 1, Profile: i, LaunchOrder: i)));
    }

    [Fact]
    public async Task Clients_start_at_least_three_seconds_apart()
    {
        await CreateCoordinator(profiles: [0, 1, 2]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        _launcher.Launched.Select(l => (l.CreationTimeUtc - T0).TotalSeconds).Should().Equal(0, 3, 6);
    }

    [Fact]
    public async Task A_client_that_cannot_be_recorded_stops_the_batch()
    {
        _store.FailingSaves = [1];

        var outcome = await CreateCoordinator(profiles: [0, 1, 2]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.ErrorCode.Should().Be(ErrorCode.UnknownError);
        outcome.ErrorMessage.Should().Contain("Launched 2 of 3").And.Contain("could not be recorded");
        _launcher.Requests.Should().HaveCount(2);
        _store.Clients.Should().ContainSingle("the first client's record stays");
    }

    [Fact]
    public async Task A_VRChat_that_appears_between_launches_stops_the_batch_without_being_closed()
    {
        _monitor.OnOpenClients = call =>
        {
            if (call == 1)
            {
                Running(4004, _now);
            }
        };

        var outcome = await CreateCoordinator(profiles: [0, 1]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        outcome.ErrorMessage.Should().Contain("Launched 1 of 2").And.Contain("4004");
        _launcher.Requests.Should().ContainSingle();
        _monitor.Processes.Single(p => p.ProcessId == 4004).CloseCalls.Should().Be(0);
        _monitor.Processes.Single(p => p.ProcessId == 4004).KillCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_VRChat_that_cannot_be_checked_between_launches_stops_the_batch()
    {
        _launcher.OnLaunch = pid =>
        {
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
            _monitor.OpenUnverifiedCount = 1;
        };

        var outcome = await CreateCoordinator(profiles: [0, 1]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        _launcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task The_clients_it_launched_itself_never_stop_the_batch()
    {
        var outcome = await CreateCoordinator(profiles: [0, 1, 2, 3]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        _monitor.OpenClientsCallCount.Should().Be(3, "checked before every launch after the first");
        AllHandles.Should().AllSatisfy(h => h.Disposed.Should().BeTrue());
    }

    [Fact]
    public async Task A_failed_launch_reports_how_many_started()
    {
        _launcher.FailingLaunchIndexes = [1];

        var outcome = await CreateCoordinator(profiles: [0, 5]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        outcome.ErrorMessage.Should().Contain("Launched 1 of 2").And.Contain("profile 5");
        _store.Clients.Should().ContainSingle();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_client_whose_creation_time_or_session_cannot_be_read_is_not_recorded_and_stops_the_batch(
        bool noCreationTime, bool noSession)
    {
        _launcher.ReportsNoCreationTime = noCreationTime;
        _launcher.SessionId = noSession ? null : 1;
        _launcher.OnLaunch = _ => { };

        var outcome = await CreateCoordinator(profiles: [0, 1]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        outcome.ErrorMessage.Should().Contain("cannot recognize it");
        _launcher.Requests.Should().ContainSingle();
        _store.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task In_VR_mode_only_the_first_client_uses_the_HMD()
    {
        await CreateCoordinator(profiles: [0, 1, 2], mode: "VR").LaunchClientsAsync(WorldPath, CancellationToken.None);

        _launcher.Requests.Select(r => r.NoVr).Should().Equal(false, true, true);
    }

    [Fact]
    public async Task A_stop_request_between_launches_keeps_the_record_of_the_clients_that_started()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = CreateCoordinator(profiles: [0, 1], delay: (_, token) =>
        {
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        });

        var act = async () => await coordinator.LaunchClientsAsync(WorldPath, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.Requests.Should().ContainSingle();
        _store.Clients.Should().ContainSingle().Which.ProcessId.Should().Be(_launcher.Launched[0].ProcessId);
    }

    [Fact]
    public async Task The_next_deploy_closes_exactly_the_clients_of_the_previous_batch()
    {
        var coordinator = CreateCoordinator(profiles: [0, 1]);
        (await coordinator.PrepareAsync(CancellationToken.None)).Kind.Should().Be(VrchatPreparationKind.ColdStart);
        (await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None)).Succeeded.Should().BeTrue();
        var firstBatch = _monitor.Processes.ToList();

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        firstBatch.Should().HaveCount(2).And.AllSatisfy(p => p.IsRunning.Should().BeFalse());
        _store.Clients.Should().BeEmpty();
    }

    // ---- The Log Viewer's notice (VrchatLogNotice) ----

    [Fact]
    public async Task The_Log_Viewer_shows_the_relaunch_notice_from_closing_until_the_first_new_client_is_recorded()
    {
        var first = Running(1001, Earlier);
        Running(1002, Earlier.AddSeconds(3));
        Recorded(1001, Earlier, profile: 0, launchOrder: 0);
        Recorded(1002, Earlier.AddSeconds(3), profile: 1, launchOrder: 1);
        string? atClose = null;
        first.OnCloseMainWindow = () => atClose = _logNotice.Message;
        var atLaunch = new List<string?>();
        _launcher.OnLaunch = pid =>
        {
            atLaunch.Add(_logNotice.Message);
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };
        var coordinator = CreateCoordinator(profiles: [0, 1]);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);
        var betweenTheCalls = _logNotice.Message;
        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        outcome.Succeeded.Should().BeTrue();
        atClose.Should().Be(VrchatRelaunchCoordinator.RelaunchNotice);
        betweenTheCalls.Should().Be(
            VrchatRelaunchCoordinator.RelaunchNotice, "with no VRChat running, the newest log is the closed clients'");
        atLaunch.Should().Equal(
            new[] { VrchatRelaunchCoordinator.RelaunchNotice, null },
            "the first client is recorded, so the Log Viewer can find it, before the second one starts");
        _logNotice.Message.Should().BeNull();
    }

    [Fact]
    public async Task The_notice_is_shown_when_the_recorded_clients_have_already_exited()
    {
        // As when the Bridge stopped while closing them and recovers the build after its
        // restart: the newest logs are still theirs (Windows verification of PR #19, item 4).
        Recorded(1001, Earlier, profile: 0, launchOrder: 0);
        Recorded(1002, Earlier.AddSeconds(3), profile: 1, launchOrder: 1);
        var atLaunch = new List<string?>();
        _launcher.OnLaunch = pid =>
        {
            atLaunch.Add(_logNotice.Message);
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };
        var coordinator = CreateCoordinator(profiles: [0, 1]);

        var preparation = await coordinator.PrepareAsync(CancellationToken.None);
        var betweenTheCalls = _logNotice.Message;
        var outcome = await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None);

        preparation.Kind.Should().Be(VrchatPreparationKind.ColdStart);
        outcome.Succeeded.Should().BeTrue();
        betweenTheCalls.Should().Be(VrchatRelaunchCoordinator.RelaunchNotice);
        atLaunch.Should().Equal(new[] { VrchatRelaunchCoordinator.RelaunchNotice, null });
        _logNotice.Message.Should().BeNull();
    }

    [Fact]
    public async Task No_notice_is_shown_when_the_record_is_empty()
    {
        // As at a Reload cold start: the newest log stays until the first client is seen.
        var atLaunch = new List<string?>();
        _launcher.OnLaunch = pid =>
        {
            atLaunch.Add(_logNotice.Message);
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };
        var coordinator = CreateCoordinator(profiles: [0, 1]);

        (await coordinator.PrepareAsync(CancellationToken.None)).Kind.Should().Be(VrchatPreparationKind.ColdStart);
        (await coordinator.LaunchClientsAsync(WorldPath, CancellationToken.None)).Succeeded.Should().BeTrue();

        atLaunch.Should().Equal(new string?[] { null, null });
        (await _logNotice.WaitForChangeAsync(TimeSpan.Zero, CancellationToken.None)).Should()
            .BeFalse("the Log Viewer is not woken either");
    }

    [Fact]
    public async Task A_VRChat_the_Bridge_did_not_launch_shows_no_notice()
    {
        Running(1001, Earlier);
        Recorded(1001, Earlier);
        Running(3003, Earlier);

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        (await _logNotice.WaitForChangeAsync(TimeSpan.Zero, CancellationToken.None)).Should()
            .BeFalse("nothing was closed, so the line never flashed");
    }

    [Fact]
    public async Task The_notice_is_cleared_when_a_client_cannot_be_closed()
    {
        var stuck = Running(1001, Earlier);
        stuck.CloseTakes = null;
        stuck.IgnoresKill = true;
        Recorded(1001, Earlier);
        string? atClose = null;
        stuck.OnCloseMainWindow = () => atClose = _logNotice.Message;

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatStartFailed);
        atClose.Should().Be(VrchatRelaunchCoordinator.RelaunchNotice);
        _logNotice.Message.Should().BeNull("the client that is still running has its log shown again");
    }

    [Fact]
    public async Task The_notice_is_cleared_when_a_VRChat_starts_while_the_clients_close()
    {
        Running(1001, Earlier);
        Recorded(1001, Earlier);
        _monitor.OnOpenClients = call =>
        {
            if (call == 2)
            {
                Running(3003, _now);
            }
        };

        var preparation = await CreateCoordinator().PrepareAsync(CancellationToken.None);

        preparation.ErrorCode.Should().Be(ErrorCode.VrchatUnmanagedRunning);
        _logNotice.Message.Should().BeNull();
    }

    [Fact]
    public async Task The_notice_is_cleared_on_a_stop_request_while_closing()
    {
        using var cts = new CancellationTokenSource();
        var process = Running(1001, Earlier);
        process.CloseTakes = TimeSpan.Zero;
        process.OnCloseMainWindow = cts.Cancel;
        Recorded(1001, Earlier);

        var act = async () => await CreateCoordinator().PrepareAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _logNotice.Message.Should().BeNull();
    }

    [Fact]
    public async Task The_notice_is_cleared_when_the_first_client_fails_to_start()
    {
        _logNotice.Show(VrchatRelaunchCoordinator.RelaunchNotice);
        _launcher.ShouldSucceed = false;

        var outcome = await CreateCoordinator(profiles: [0, 1]).LaunchClientsAsync(WorldPath, CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        _logNotice.Message.Should().BeNull("with no client running, the newest log is shown as usual");
    }

    [Fact]
    public async Task The_notice_is_cleared_on_a_stop_request_before_the_first_launch()
    {
        _logNotice.Show(VrchatRelaunchCoordinator.RelaunchNotice);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await CreateCoordinator().LaunchClientsAsync(WorldPath, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.LaunchCalled.Should().BeFalse();
        _logNotice.Message.Should().BeNull();
    }
}

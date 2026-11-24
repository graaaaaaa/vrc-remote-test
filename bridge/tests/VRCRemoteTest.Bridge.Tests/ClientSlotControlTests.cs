using FluentAssertions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Closing one client and starting it again (POST /api/v1/clients/{slot}/close and /launch)
/// over fake processes, a fake record and a fake clock that only the control's own waits
/// advance. The batch it repeats is set up in <see cref="RelaunchLaunchContext"/> as the
/// Relaunch coordinator leaves it: slot 0 on profile 0 with the HMD, slot 1 on profile 1 on
/// the desktop.
/// </summary>
public class ClientSlotControlTests
{
    private const string WorldPath = @"C:\Users\dev\AppData\LocalLow\VRChat\VRChat\Worlds\vrc-remote-b1.vrcw";
    private const string RoomId = "1234567890";

    private static readonly DateTime T0 = new(2026, 11, 24, 9, 0, 0, 100, DateTimeKind.Utc);
    private static readonly DateTime Created0 = T0.AddMinutes(-3).AddTicks(1_234_567);
    private static readonly DateTime Created1 = T0.AddMinutes(-3).AddSeconds(4).AddTicks(7_654_321);

    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly FakeVrchatLauncher _launcher = new();
    private readonly FakeManagedClientStore _store = new();
    private readonly RelaunchLaunchContext _context = new();
    private readonly VrchatClientRoster _roster = new(filePath: null);
    private readonly CapturingLogger<ClientSlotControl> _logger = new();
    private readonly HashSet<string> _existingFiles = new(StringComparer.OrdinalIgnoreCase) { WorldPath };
    private Func<IReadOnlyList<VrchatLogMatcher.Candidate>?> _listLogFiles = () => null;
    private DateTime _now = T0;

    public ClientSlotControlTests()
    {
        _context.BeginBatch(WorldPath, RoomId);
        _context.AddSlot(0, profile: 0, noVr: false);
        _context.AddSlot(1, profile: 1, noVr: true);
        _launcher.Clock = () => _now;
        _launcher.OnLaunch = pid => Running(pid, _launcher.Launched[^1].CreationTimeUtc);
    }

    private ClientSlotControl Create(string clientMode = "Relaunch") => new(
        Options.Create(new BridgeOptions
        {
            VrchatExecutable = @"C:\VRChat\VRChat.exe",
            VrchatMode = "VR",
            AutoLaunchVrchat = true,
            VrchatClientMode = clientMode,
            VrchatProfiles = [0, 1],
        }),
        _monitor,
        _store,
        _launcher,
        _context,
        _roster,
        _logger,
        (wait, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            _now += wait;
            return Task.CompletedTask;
        },
        () => _listLogFiles(),
        () => _now,
        _existingFiles.Contains);

    private FakeVrchatProcess Running(int processId, DateTime creationTimeUtc, int sessionId = 1)
    {
        var process = _monitor.AddProcess(processId, creationTimeUtc, sessionId);
        process.Clock = () => _now;
        return process;
    }

    /// <summary>Two clients the Bridge launched and recorded, both running.</summary>
    private (FakeVrchatProcess Slot0, FakeVrchatProcess Slot1) TwoRunning()
    {
        _store.Clients.Add(new ManagedClient(100, Created0, 1, Profile: 0, LaunchOrder: 0));
        _store.Clients.Add(new ManagedClient(101, Created1, 1, Profile: 1, LaunchOrder: 1));
        return (Running(100, Created0), Running(101, Created1));
    }

    // ---- close ----

    [Fact]
    public async Task Close_closes_only_the_slots_client_and_keeps_the_others_record()
    {
        var (slot0, slot1) = TwoRunning();

        var result = await Create().CloseAsync(0, CancellationToken.None);

        result.Should().BeEquivalentTo(new SlotCloseResult(null, 0, 100, 1, 0, true, []));
        result.Done.Should().BeTrue();
        slot0.IsRunning.Should().BeFalse();
        slot0.CloseCalls.Should().Be(1);
        slot1.IsRunning.Should().BeTrue();
        slot1.CloseCalls.Should().Be(0);
        slot1.KillCalls.Should().Be(0);
        _store.Clients.Should().ContainSingle().Which.ProcessId.Should().Be(101);
        _logger.MessagesAt(Microsoft.Extensions.Logging.LogLevel.Information)
            .Should().Contain(m => m.Contains("Single-client close of slot 0") && m.Contains("PID 100"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public async Task Close_refuses_a_slot_out_of_range(int slot)
    {
        TwoRunning();

        var result = await Create().CloseAsync(slot, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.UnknownSlot);
        _monitor.OpenClientsCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Close_refuses_in_Reload_mode()
    {
        var (slot0, _) = TwoRunning();

        var result = await Create("Reload").CloseAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.NotRelaunchMode);
        slot0.CloseCalls.Should().Be(0);
        _store.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Close_refuses_a_slot_the_record_does_not_list()
    {
        _store.Clients.Add(new ManagedClient(101, Created1, 1, Profile: 1, LaunchOrder: 1));
        var stranger = Running(100, Created0);

        var result = await Create().CloseAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.NotLaunchedByBridge);
        stranger.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Close_refuses_when_the_recorded_client_is_not_running()
    {
        _store.Clients.Add(new ManagedClient(100, Created0, 1, Profile: 0, LaunchOrder: 0));

        var result = await Create().CloseAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.NotRunning);
        _store.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Close_never_closes_a_reused_pid_or_a_process_in_another_session()
    {
        _store.Clients.Add(new ManagedClient(100, Created0, 1, Profile: 0, LaunchOrder: 0));
        var reused = Running(100, Created0.AddTicks(1));

        (await Create().CloseAsync(0, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.NotRunning);
        reused.Exit();
        var otherSession = Running(100, Created0, sessionId: 2);
        (await Create().CloseAsync(0, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.NotRunning);

        reused.CloseCalls.Should().Be(0);
        otherSession.CloseCalls.Should().Be(0);
        otherSession.KillCalls.Should().Be(0);
    }

    [Fact]
    public async Task Close_refuses_while_a_VRChat_cannot_be_checked()
    {
        var (slot0, _) = TwoRunning();
        _monitor.OpenUnverifiedCount = 1;

        (await Create().CloseAsync(0, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.CannotCheck);
        _monitor.OpenUnverifiedCount = 0;
        _monitor.OpenEnumerationError = "access denied";
        (await Create().CloseAsync(0, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.CannotCheck);

        slot0.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Close_refuses_while_the_observation_shows_another_client_in_the_slot()
    {
        var (slot0, _) = TwoRunning();
        _roster.Update(new VrchatClientSnapshot([new VrchatClientInfo(90, T0.AddHours(-1), false)], 0), []);

        var result = await Create().CloseAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.SlotMismatch);
        result.Problems.Single().Should().Contain("PID 90").And.Contain("PID 100");
        slot0.CloseCalls.Should().Be(0);
    }

    [Fact]
    public async Task Close_goes_ahead_when_the_observation_shows_the_same_client()
    {
        var (slot0, _) = TwoRunning();
        _roster.Update(
            new VrchatClientSnapshot([new VrchatClientInfo(100, Created0, false), new VrchatClientInfo(101, Created1, false)], 0),
            _store.Clients);

        (await Create().CloseAsync(0, CancellationToken.None)).Done.Should().BeTrue();
        slot0.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task A_client_that_does_not_exit_keeps_its_record()
    {
        var (slot0, _) = TwoRunning();
        slot0.CloseTakes = null;
        slot0.IgnoresKill = true;

        var result = await Create().CloseAsync(0, CancellationToken.None);

        result.Refusal.Should().BeNull();
        result.Closed.Should().Be(0);
        result.NotConfirmed.Should().Be(1);
        result.RecordUpdated.Should().BeFalse();
        result.Done.Should().BeFalse();
        slot0.KillCalls.Should().Be(1);
        _store.SaveCalls.Should().Be(0);
        _store.Clients.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_record_that_cannot_be_written_after_the_exit_is_reported()
    {
        var (slot0, _) = TwoRunning();
        _store.FailingSaves = [0];

        var result = await Create().CloseAsync(0, CancellationToken.None);

        result.Closed.Should().Be(1);
        result.RecordUpdated.Should().BeFalse();
        result.Done.Should().BeFalse();
        result.Problems.Single().Should().Contain("could not update its record");
        slot0.IsRunning.Should().BeFalse();
    }

    [Theory]
    [InlineData(ManagedClientLoadStatus.Corrupt, SlotRefusal.RecordCorrupt)]
    [InlineData(ManagedClientLoadStatus.Unreadable, SlotRefusal.RecordUnreadable)]
    public async Task A_record_that_is_corrupt_or_unreadable_closes_nothing_and_is_not_rewritten(
        ManagedClientLoadStatus status, SlotRefusal refusal)
    {
        var (slot0, _) = TwoRunning();
        _store.LoadResult = status == ManagedClientLoadStatus.Corrupt
            ? ManagedClientLoadResult.Corrupt("schemaVersion 9 is unknown")
            : ManagedClientLoadResult.Unreadable("access denied");

        (await Create().CloseAsync(0, CancellationToken.None)).Refusal.Should().Be(refusal);
        (await Create().LaunchAsync(0, CancellationToken.None)).Refusal.Should().Be(refusal);

        slot0.CloseCalls.Should().Be(0);
        _store.SaveCalls.Should().Be(0);
        _launcher.LaunchCalled.Should().BeFalse();
    }

    // ---- launch ----

    [Fact]
    public async Task Launch_repeats_the_slots_arguments_and_records_the_new_client_in_its_launch_order()
    {
        var (slot0, slot1) = TwoRunning();
        slot0.Exit();

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        var request = _launcher.Requests.Should().ContainSingle().Subject;
        request.Should().Be(new VrchatLaunchRequest(
            @"C:\VRChat\VRChat.exe", WorldPath, RoomId, NoVr: false, WatchWorlds: false, Profile: 0));
        var (pid, created) = _launcher.Launched.Single();
        result.Should().BeEquivalentTo(new SlotLaunchResult(
            null, 0, 0, pid, VrchatClientRoster.GenerationOf(pid, created), false, []));
        result.Done.Should().BeTrue();
        _store.Clients.Should().BeEquivalentTo(new[]
        {
            new ManagedClient(101, Created1, 1, 1, 1),
            new ManagedClient(pid, created, 1, 0, 0),
        });
        slot1.CloseCalls.Should().Be(0);
        _context.IsSingleLaunch(pid, created).Should().BeTrue();
        _context.IsSingleLaunch(101, Created1).Should().BeFalse();
        _context.LastLaunch!.Utc.Should().Be(created);
        _context.IsLaunching.Should().BeFalse();
    }

    [Fact]
    public async Task A_desktop_slot_is_started_on_the_desktop_while_the_HMD_slot_runs()
    {
        var (_, slot1) = TwoRunning();
        slot1.Exit();

        (await Create().LaunchAsync(1, CancellationToken.None)).Done.Should().BeTrue();

        _launcher.Requests.Single().Should().Match<VrchatLaunchRequest>(r => r.NoVr && r.Profile == 1);
        VrchatLauncher.BuildArguments(_launcher.Requests.Single()).Should()
            .Contain("--no-vr").And.Contain("--profile=1").And.NotContain("--watch-worlds");
    }

    [Fact]
    public async Task The_launching_mark_is_up_from_before_the_start_until_the_client_is_recorded()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        var marks = new List<(bool Launching, int Saves)>();
        _launcher.OnLaunch = pid =>
        {
            marks.Add((_context.IsLaunching, _store.SaveCalls));
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
        };

        await Create().LaunchAsync(0, CancellationToken.None);

        // One save before the launch (the writability check), the client's own one after it.
        marks.Should().Equal((true, 1));
        _store.SaveCalls.Should().Be(2);
        _context.IsLaunching.Should().BeFalse();
    }

    [Fact]
    public async Task Launch_refuses_a_slot_the_current_batch_did_not_start()
    {
        TwoRunning();

        var fresh = new RelaunchLaunchContext();
        var control = new ClientSlotControl(
            Options.Create(new BridgeOptions { VrchatClientMode = "Relaunch", VrchatProfiles = [0, 1], VrchatExecutable = "x" }),
            _monitor, _store, _launcher, fresh, _roster, _logger,
            (_, _) => Task.CompletedTask, () => null, () => _now, _ => true);

        (await control.LaunchAsync(0, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.NoLaunchPlan);
        (await Create().LaunchAsync(2, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.NoLaunchPlan);
        _launcher.LaunchCalled.Should().BeFalse();
        _store.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Launch_refuses_when_the_world_is_gone()
    {
        TwoRunning();
        _existingFiles.Clear();

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.WorldMissing);
        result.Problems.Single().Should().Contain("vrc-remote-b1.vrcw").And.NotContain(@"C:\Users\dev");
        _launcher.LaunchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Launch_refuses_while_the_slots_client_still_runs()
    {
        TwoRunning();

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.AlreadyRunning);
        _launcher.LaunchCalled.Should().BeFalse();
        _store.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Launch_refuses_while_a_VRChat_the_Bridge_did_not_launch_runs()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        var stranger = Running(300, T0.AddMinutes(-1));

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.OthersRunning);
        result.Problems.Single().Should().Contain("PID 300");
        stranger.CloseCalls.Should().Be(0);
        _launcher.LaunchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Launch_refuses_in_Reload_mode_and_when_the_process_list_cannot_be_checked()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();

        (await Create("Reload").LaunchAsync(0, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.NotRelaunchMode);
        _monitor.OpenUnverifiedCount = 1;
        (await Create().LaunchAsync(0, CancellationToken.None)).Refusal.Should().Be(SlotRefusal.CannotCheck);

        _launcher.LaunchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Launch_starts_nothing_when_the_record_cannot_be_written_first()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        _store.FailingSaves = [0];

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.RecordUnwritable);
        _launcher.LaunchCalled.Should().BeFalse();
    }

    [Fact]
    public async Task A_VRChat_started_during_the_wait_stops_the_launch()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        _context.RecordLaunch(T0.AddSeconds(-1), filesBefore: null);
        _monitor.OnOpenClients = call =>
        {
            if (call == 2)
            {
                Running(300, _now);
            }
        };

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        result.Refusal.Should().Be(SlotRefusal.OthersRunning);
        _launcher.LaunchCalled.Should().BeFalse();
        _now.Should().BeOnOrAfter(T0.AddSeconds(2), "the launch interval was waited before the check");
    }

    [Fact]
    public async Task Launch_waits_for_the_previous_launchs_log_before_starting()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        var previous = T0.AddSeconds(-1);
        var old = new VrchatLogMatcher.Candidate(@"C:\logs\output_log_old.txt", null, T0.AddMinutes(-3));
        _context.RecordLaunch(previous, [old.Path]);
        var appearsAt = T0.AddSeconds(9.3);
        _listLogFiles = () => _now >= appearsAt
            ? [old, new VrchatLogMatcher.Candidate(@"C:\logs\output_log_new.txt", null, appearsAt)]
            : [old];

        (await Create().LaunchAsync(0, CancellationToken.None)).Done.Should().BeTrue();

        // Not before the file appeared (09:00:09.4), then out of its matching range: its second + 2 s.
        _launcher.Launched.Single().CreationTimeUtc.Should().Be(new DateTime(2026, 11, 24, 9, 0, 11, DateTimeKind.Utc));
    }

    [Fact]
    public async Task A_previous_launch_long_ago_is_not_waited_for()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        _context.RecordLaunch(T0.AddHours(-2), [@"C:\logs\output_log_old.txt"]);
        _listLogFiles = () => [];

        (await Create().LaunchAsync(0, CancellationToken.None)).Done.Should().BeTrue();

        _launcher.Launched.Single().CreationTimeUtc.Should().Be(T0);
        _logger.MessagesAt(Microsoft.Extensions.Logging.LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_start_is_a_refusal_and_records_nothing()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        _launcher.ShouldSucceed = false;

        var failed = await Create().LaunchAsync(0, CancellationToken.None);
        _launcher.ShouldSucceed = true;
        _launcher.ThrowOnLaunch = new InvalidOperationException(@"C:\Users\dev\VRChat.exe is missing");
        var threw = await Create().LaunchAsync(0, CancellationToken.None);

        failed.Refusal.Should().Be(SlotRefusal.LaunchFailed);
        threw.Refusal.Should().Be(SlotRefusal.LaunchFailed);
        threw.Problems.Single().Should().Contain("is missing");
        _store.Clients.Should().ContainSingle().Which.LaunchOrder.Should().Be(1);
        _context.IsLaunching.Should().BeFalse();
        _context.LastLaunch.Should().BeNull();
    }

    [Fact]
    public async Task A_client_whose_creation_time_cannot_be_read_is_reported_as_not_recorded()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        _launcher.ReportsNoCreationTime = true;

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        result.Refusal.Should().BeNull();
        result.NotRecorded.Should().BeTrue();
        result.Done.Should().BeFalse();
        result.ProcessId.Should().Be(_launcher.Launched.Single().ProcessId);
        result.Problems.Single().Should().Contain("close it on Windows");
        _store.Clients.Should().ContainSingle().Which.LaunchOrder.Should().Be(1);
        _context.LastLaunch.Should().NotBeNull("the next launch is still spaced from this one");
        _context.IsLaunching.Should().BeFalse();
    }

    [Fact]
    public async Task A_client_that_cannot_be_recorded_is_reported_with_its_pid_and_not_closed()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        _store.FailingSaves = [1];

        var result = await Create().LaunchAsync(0, CancellationToken.None);

        var pid = _launcher.Launched.Single().ProcessId;
        result.NotRecorded.Should().BeTrue();
        result.ProcessId.Should().Be(pid);
        _monitor.Processes.Single(p => p.ProcessId == pid).Should().Match<FakeVrchatProcess>(p => p.CloseCalls == 0 && p.KillCalls == 0);
        _context.IsLaunching.Should().BeFalse();
    }

    [Fact]
    public async Task A_stop_request_before_the_start_starts_nothing()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Create().LaunchAsync(0, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _launcher.LaunchCalled.Should().BeFalse();
        _context.IsLaunching.Should().BeFalse();
    }

    [Fact]
    public async Task A_stop_request_after_the_start_still_records_the_client()
    {
        var (slot0, _) = TwoRunning();
        slot0.Exit();
        using var cts = new CancellationTokenSource();
        _launcher.OnLaunch = pid =>
        {
            Running(pid, _launcher.Launched[^1].CreationTimeUtc);
            cts.Cancel();
        };

        var result = await Create().LaunchAsync(0, cts.Token);

        result.Done.Should().BeTrue();
        var pid = _launcher.Launched.Single().ProcessId;
        _store.Clients.Should().Contain(c => c.ProcessId == pid && c.LaunchOrder == 0);
        _monitor.Processes.Single(p => p.ProcessId == pid).KillCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_closed_client_can_be_closed_again_after_its_relaunch()
    {
        var (_, slot1) = TwoRunning();
        var control = Create();

        (await control.CloseAsync(0, CancellationToken.None)).Done.Should().BeTrue();
        (await control.LaunchAsync(0, CancellationToken.None)).Done.Should().BeTrue();
        var relaunched = _monitor.Processes.Single(p => p.ProcessId == _launcher.Launched.Single().ProcessId);
        (await control.CloseAsync(0, CancellationToken.None)).Done.Should().BeTrue();

        relaunched.IsRunning.Should().BeFalse();
        slot1.IsRunning.Should().BeTrue();
        _store.Clients.Should().ContainSingle().Which.ProcessId.Should().Be(101);
    }
}

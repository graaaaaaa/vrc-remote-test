using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Client-observation plan C2: slots that survive exits and Bridge restarts.</summary>
public class VrchatClientRosterTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 18, 4, 34, 37, DateTimeKind.Utc);

    private readonly string _stateDir = Directory.CreateTempSubdirectory("vrc-remote-test-roster-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_stateDir, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_stateDir, VrchatClientRoster.FileName);

    private VrchatClientRoster NewRoster() => new(FilePath);

    private static VrchatClientInfo Client(int pid, DateTime? created) => new(pid, created, WatchWorldsDetected: true);

    private static VrchatClientSnapshot Snapshot(int unverified = 0, params VrchatClientInfo[] clients) =>
        new(clients, unverified);

    private static int? SlotOf(RosterView view, int pid) =>
        view.Slots.FirstOrDefault(s => s.Liveness == RosterLiveness.Running && s.Client?.ProcessId == pid)?.Slot;

    [Fact]
    public void New_clients_take_free_slots_by_creation_time_then_PID()
    {
        var view = NewRoster().Update(
            Snapshot(0, Client(300, T0.AddSeconds(6)), Client(200, T0), Client(100, T0)), []);

        SlotOf(view, 100).Should().Be(0);
        SlotOf(view, 200).Should().Be(1);
        SlotOf(view, 300).Should().Be(2);
        view.NewGenerations.Should().BeEquivalentTo([0, 1, 2]);
    }

    [Fact]
    public void A_Reload_client_left_alone_keeps_its_slot_across_a_Bridge_restart()
    {
        var a = Client(100, T0);
        var b = Client(200, T0.AddSeconds(3));
        NewRoster().Update(Snapshot(0, a, b), []);

        // Slot 0's client exits, then the Bridge restarts.
        var view = NewRoster().Update(Snapshot(0, b), []);

        SlotOf(view, 200).Should().Be(1);
        view.Slots[0].Liveness.Should().Be(RosterLiveness.Exited);
        view.Slots[0].Entry!.ProcessId.Should().Be(100, "the exited client's entry stays until the slot is reused");
        view.NewGenerations.Should().BeEmpty();
    }

    [Fact]
    public void A_slot_is_reused_only_once_its_client_is_confirmed_gone()
    {
        var roster = NewRoster();
        roster.Update(Snapshot(0, Client(100, T0)), []);

        var unsure = roster.Update(Snapshot(1, Client(500, T0.AddMinutes(1))), []);
        unsure.Slots[0].Liveness.Should().Be(RosterLiveness.Unknown);
        SlotOf(unsure, 500).Should().Be(1);

        var gone = roster.Update(Snapshot(0, Client(500, T0.AddMinutes(1)), Client(600, T0.AddMinutes(2))), []);
        SlotOf(gone, 600).Should().Be(0);
        gone.NewGenerations.Should().BeEquivalentTo([0]);
    }

    [Fact]
    public void A_reused_PID_is_a_new_client()
    {
        var roster = NewRoster();
        roster.Update(Snapshot(0, Client(100, T0)), []);

        var view = roster.Update(Snapshot(0, Client(100, T0.AddMinutes(5))), []);

        view.Slots[0].Entry!.CreationTimeUtc.Should().Be(T0.AddMinutes(5));
        view.NewGenerations.Should().BeEquivalentTo([0]);
    }

    [Fact]
    public void A_client_whose_creation_time_is_unknown_gets_no_slot()
    {
        var view = NewRoster().Update(Snapshot(0, Client(100, null), Client(200, T0)), []);

        SlotOf(view, 200).Should().Be(0);
        view.Unassigned.Should().Be(1);
    }

    [Fact]
    public void A_fifth_client_gets_no_slot()
    {
        var clients = Enumerable.Range(1, 5).Select(i => Client(i * 100, T0.AddSeconds(i))).ToArray();

        var view = NewRoster().Update(Snapshot(0, clients), []);

        view.Slots.Should().OnlyContain(s => s.Liveness == RosterLiveness.Running);
        view.Unassigned.Should().Be(1);
    }

    [Fact]
    public void In_Relaunch_mode_a_new_client_takes_the_slot_of_its_launch_order()
    {
        var records = new[] { new ManagedClient(200, T0.AddSeconds(3), 1, Profile: 1, LaunchOrder: 0), new ManagedClient(100, T0, 1, 0, 1) };

        var view = NewRoster().Update(Snapshot(0, Client(100, T0), Client(200, T0.AddSeconds(3))), records);

        SlotOf(view, 200).Should().Be(0);
        SlotOf(view, 100).Should().Be(1);
    }

    [Fact]
    public void A_recorded_client_seen_before_its_record_moves_to_its_launch_order_when_that_slot_is_free()
    {
        var roster = NewRoster();
        roster.Update(Snapshot(0, Client(100, T0)), []);
        var records = new[] { new ManagedClient(100, T0, 1, Profile: 1, LaunchOrder: 2) };

        var view = roster.Update(Snapshot(0, Client(100, T0)), records);

        SlotOf(view, 100).Should().Be(2);
        view.Slots[0].Liveness.Should().Be(RosterLiveness.Empty);
    }

    [Fact]
    public void Changing_the_record_never_moves_a_client_onto_a_running_one()
    {
        var roster = NewRoster();
        roster.Update(Snapshot(0, Client(100, T0), Client(200, T0.AddSeconds(3))), []);
        var records = new[] { new ManagedClient(200, T0.AddSeconds(3), 1, Profile: 0, LaunchOrder: 0) };

        var view = roster.Update(Snapshot(0, Client(100, T0), Client(200, T0.AddSeconds(3))), records);

        SlotOf(view, 100).Should().Be(0);
        SlotOf(view, 200).Should().Be(1);
    }

    [Fact]
    public void Nothing_changes_while_the_process_list_cannot_be_read()
    {
        var roster = NewRoster();
        roster.Update(Snapshot(0, Client(100, T0)), []);

        var view = roster.Update(VrchatClientSnapshot.Failed("denied"), []);

        view.Slots[0].Liveness.Should().Be(RosterLiveness.Unknown);
        view.Slots[0].Entry!.ProcessId.Should().Be(100);
    }

    [Fact]
    public void Epochs_keep_increasing_across_restarts()
    {
        var first = NewRoster();
        var a = first.AllocateEpoch();
        var b = first.AllocateEpoch();

        var c = NewRoster().AllocateEpoch();

        new[] { a, b, c }.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_file_it_did_not_write_starts_empty_with_epochs_above_any_before()
    {
        File.WriteAllText(FilePath, "{\"schemaVersion\": 1, \"lastObservationEpoch\": 3, \"entries\": [{\"slot\": 9}]}");
        var roster = NewRoster();

        var view = roster.Update(Snapshot(0, Client(100, T0)), []);

        SlotOf(view, 100).Should().Be(0);
        roster.AllocateEpoch().Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60);
    }

    [Fact]
    public void The_generation_is_the_same_for_the_same_process_only()
    {
        var entry = new RosterEntry(0, 100, T0);

        entry.ClientGeneration.Should().Be(new RosterEntry(3, 100, T0).ClientGeneration);
        entry.ClientGeneration.Should().NotBe(new RosterEntry(0, 100, T0.AddTicks(1)).ClientGeneration);
        entry.ClientGeneration.Should().BeInRange(1, (1L << 53) - 1);
    }

    [Fact]
    public void An_epoch_that_could_not_be_saved_is_not_handed_out_again_after_a_restart()
    {
        // The state directory is a file: nothing can be saved.
        var blocked = Path.Combine(_stateDir, "blocked");
        File.WriteAllText(blocked, string.Empty);
        var path = Path.Combine(blocked, VrchatClientRoster.FileName);
        var start = DateTimeOffset.UtcNow;

        var before = new VrchatClientRoster(path, utcNow: () => start).AllocateEpoch();
        var after = new VrchatClientRoster(path, utcNow: () => start.AddSeconds(5)).AllocateEpoch();

        after.Should().BeGreaterThan(before);
        before.Should().BeGreaterOrEqualTo(start.ToUnixTimeMilliseconds()).And.BeLessThan(1L << 53);
    }
}

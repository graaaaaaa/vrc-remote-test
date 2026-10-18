using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Client-observation plan F1 and C6, with the lines seen on real hardware
/// (docs/validation/multi-client-spike.md, S5a A1-A6 and S5b B).
/// </summary>
public class VrchatClientStateTrackerTests
{
    private const string Id = "dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383";
    private const string Other = "ce318f3e82d614028e1b7fd1c58e2dc69783d28f221338974d3230b42f743c4a";
    private const string Me = "Alice (usr_0123abcd-0000-4000-8000-0123456789ab)";
    private const string Peer = "Bob (usr_99999999-0000-4000-8000-0123456789ab)";

    private readonly VrchatClientStateTracker _tracker = new();
    private long _seq;

    private void Line(string message, string level = "Debug", bool truncated = false) =>
        _tracker.OnLine(new VrchatLogPhysicalLine(++_seq, $"2026.10.18 13:34:37 {level.PadRight(11)}-  {message}", truncated));

    private void Join(string id = Id, bool master = true)
    {
        Line($"[Behaviour] Destination set: local:{id}:1");
        Line("[Behaviour] Entering Room: Local Test");
        Line($"[Behaviour] Joining local:{id}");
        Line("[Behaviour] Joining or Creating Room: Local Test");
        Line("[Behaviour] Successfully joined room");
        Line(master ? "[Behaviour] I am MASTER" : "[Behaviour] I am *NOT* MASTER");
        Line("[Behaviour] Finished entering world.");
        Line($"[Behaviour] OnPlayerJoined {Me}");
    }

    [Fact]
    public void Follows_a_join_as_S5a_A1_logged_it()
    {
        Line($"User Authenticated: {Me}");
        _tracker.State.Should().Be(VrchatClientLogState.Starting);

        Join();
        Line($"[Behaviour] OnPlayerJoined {Peer}");

        _tracker.State.Should().Be(VrchatClientLogState.Joined);
        _tracker.RequestedInstance.Should().Be($"local:{Id}");
        _tracker.Instance.Should().Be($"local:{Id}");
        _tracker.InErrorWorld.Should().BeFalse();
        _tracker.IsMaster.Should().BeTrue();
        _tracker.Players.Should().Equal("Alice", "Bob");
        _tracker.Authenticated.Should().BeTrue();
        _tracker.JoinAttemptSeq.Should().Be(4);
        _tracker.JoinedSeq.Should().Be(8);
        _tracker.SawDebug.Should().BeTrue();
    }

    [Fact]
    public void Joining_alone_is_not_joined_and_Joining_or_Creating_is_not_a_Joining()
    {
        Line("[Behaviour] Joining or Creating Room: Local Test");
        _tracker.State.Should().Be(VrchatClientLogState.Starting);

        Line($"[Behaviour] Joining local:{Id}");
        Line("[Behaviour] Successfully joined room");

        _tracker.State.Should().Be(VrchatClientLogState.Joining);
        _tracker.Instance.Should().BeNull();
    }

    [Fact]
    public void Only_the_exact_message_finishes_a_join()
    {
        Line($"[Behaviour] Joining local:{Id}");
        Line("[Behaviour] Finished entering world. s5forged1");
        Line("[SPK003] [Behaviour] Finished entering world.");
        Line("[Behaviour] Finished entering world.", level: "Warning");
        _tracker.State.Should().Be(VrchatClientLogState.Joining);

        Line("[Behaviour] Finished entering world.");
        _tracker.State.Should().Be(VrchatClientLogState.Joined);
    }

    [Fact]
    public void Continuation_and_truncated_lines_are_not_used()
    {
        Line($"[Behaviour] Joining local:{Id}");
        _tracker.OnLine(new VrchatLogPhysicalLine(++_seq, "2026.10.18 13:34:37 Debug      -  [SPK003] x\n2026.10.18 13:34:37 Debug      -  [Behaviour] Finished entering world.", false));
        _tracker.OnLine(new VrchatLogPhysicalLine(++_seq, "    2026.10.18 13:34:37 Debug      -  [Behaviour] Finished entering world.", false));
        Line("[Behaviour] Finished entering world.", truncated: true);
        Line("[Behaviour] Finished entering world.\nforged tail");

        _tracker.State.Should().Be(VrchatClientLogState.Joining);
        _tracker.ObservedThroughSeq.Should().Be(_seq);
    }

    [Fact]
    public void A_new_Joining_replaces_the_previous_attempt()
    {
        Join();
        Line($"[Behaviour] OnPlayerJoined {Peer}");

        // S5a A4: --watch-worlds reloaded the world into another instance.
        Line($"[Behaviour] Joining local:{Other}");

        _tracker.State.Should().Be(VrchatClientLogState.Joining);
        _tracker.RequestedInstance.Should().Be($"local:{Other}");
        _tracker.Instance.Should().BeNull();
        _tracker.Players.Should().BeEmpty();
        _tracker.IsMaster.Should().BeNull();
    }

    [Fact]
    public void A_failed_join_stays_joining()
    {
        Line($"[Behaviour] Joining local:{Id}");
        Line("[Behaviour] Failed to join", level: "Error");

        _tracker.State.Should().Be(VrchatClientLogState.Joining);
    }

    [Fact]
    public void Another_players_OnPlayerJoined_alone_does_not_finish_a_join()
    {
        Line($"[Behaviour] Joining local:{Id}");
        Line($"[Behaviour] OnPlayerJoined {Peer}");

        _tracker.State.Should().Be(VrchatClientLogState.Joining);
        _tracker.Players.Should().Equal("Bob");
    }

    [Theory]
    [InlineData("local:s5forged1")]
    [InlineData("wrld_123:456")]
    [InlineData("local:DAB8E405667A4DE729F91623DB09E31F57071EFE70EA4B1EE764BD3563DD5383")]
    public void A_Joining_of_an_unknown_form_is_unknown(string value)
    {
        Join();
        Line($"[Behaviour] Joining {value}");
        Line("[Behaviour] Finished entering world.");

        _tracker.State.Should().Be(VrchatClientLogState.Unknown);
        _tracker.RequestedInstance.Should().BeNull();
        _tracker.Instance.Should().BeNull();
    }

    [Fact]
    public void The_Error_World_is_joined_but_flagged()
    {
        // S5a A5.
        Join(id: "error_4327609248367914");

        _tracker.State.Should().Be(VrchatClientLogState.Joined);
        _tracker.Instance.Should().Be("local:error_4327609248367914");
        _tracker.InErrorWorld.Should().BeTrue();
    }

    [Fact]
    public void Rejoining_goes_back_to_joining_until_the_next_join()
    {
        // S5a A6: Quick Menu > Rejoin World.
        Join();
        Line($"[Behaviour] Rejoining local world: local:{Id}:1");
        Line($"[Behaviour] Destination requested: local:{Id}:1");
        Line("[Behaviour] OnLeftRoom");
        Line($"[Behaviour] OnPlayerLeft {Me}");
        Line("[Behaviour] Finished entering world.");
        _tracker.State.Should().Be(VrchatClientLogState.Joining, "a finish needs a Joining first");
        _tracker.RequestedInstance.Should().Be($"local:{Id}");
        _tracker.Players.Should().BeNull();

        Join();

        _tracker.State.Should().Be(VrchatClientLogState.Joined);
        _tracker.Players.Should().Equal("Alice");
        _tracker.Events.Select(e => e.Kind).Should().ContainInOrder(
            "Joining", "FinishedEnteringWorld", "Rejoining", "OnLeftRoom", "Joining", "FinishedEnteringWorld");
    }

    [Fact]
    public void Master_is_unknown_after_a_switch_until_this_client_is_alone()
    {
        // S5a A2: OnMasterClientSwitched comes before the master's OnPlayerLeft.
        Join(master: false);
        Line($"[Behaviour] OnPlayerJoined {Peer}");
        _tracker.IsMaster.Should().BeFalse();

        Line("[Behaviour] OnMasterClientSwitched");
        _tracker.IsMaster.Should().BeNull();
        Line("[Behaviour] OnPlayerLeftRoom");
        Line($"[Behaviour] OnPlayerLeft {Peer}");

        _tracker.IsMaster.Should().BeTrue();
        _tracker.Players.Should().Equal("Alice");
    }

    [Fact]
    public void Several_clients_on_one_account_keep_their_duplicate_names()
    {
        Join();
        Line($"[Behaviour] OnPlayerJoined {Me}");
        Line($"[Behaviour] OnPlayerJoined Name (with) parens (usr_aaaaaaaa-0000-4000-8000-0123456789ab)");
        Line($"[Behaviour] OnPlayerLeft {Me}");

        _tracker.Players.Should().Equal("Alice", "Name (with) parens");
    }

    [Fact]
    public void Authenticated_is_only_ever_set_true()
    {
        Join();
        _tracker.Authenticated.Should().BeFalse("its absence proves nothing; it is published as null");

        Line("User Authenticated: Alice");
        _tracker.Authenticated.Should().BeFalse("the line needs its usr_ ID");
    }

    [Fact]
    public void Error_lines_alone_are_seen_as_such_and_an_Udon_exception_is_an_event()
    {
        _tracker.OnLine(new VrchatLogPhysicalLine(
            ++_seq,
            "2026.10.18 13:34:37 Error      -  [UdonBehaviour] An exception occurred during Udon execution, this UdonBehaviour will be halted.\nVRC.Udon.VM.UdonVMException: boom",
            false));

        _tracker.SawError.Should().BeTrue();
        _tracker.SawDebug.Should().BeFalse();
        _tracker.Events.Single().Kind.Should().Be("udonException");
    }

    [Theory]
    [InlineData($"local:{Id}:1", $"local:{Id}")]
    [InlineData($"local:{Id}", $"local:{Id}")]
    [InlineData("local:error_42:1", "local:error_42")]
    [InlineData("local:abc:1", null)]
    [InlineData("wrld_x:1", null)]
    public void Instances_are_compared_without_the_trailing_number(string value, string? expected)
    {
        VrchatClientStateTracker.NormalizeInstance(value).Should().Be(expected);
    }

    [Fact]
    public void A_flood_of_joins_gives_up_the_player_list_until_the_next_Joining()
    {
        Join();
        for (var i = 0; i < VrchatClientStateTracker.MaxPlayers + 10; i++)
        {
            Line($"[Behaviour] OnPlayerJoined A (usr_{i:D8}-0000)");
        }

        _tracker.Players.Should().BeNull();
        _tracker.IsMaster.Should().BeNull();

        Join();
        _tracker.Players.Should().Equal("Alice");
        _tracker.IsMaster.Should().BeTrue();
    }

    [Fact]
    public void Names_and_IDs_longer_than_VRChat_writes_are_not_events()
    {
        Join();
        Line($"[Behaviour] OnPlayerJoined {new string('n', VrchatClientStateTracker.MaxNameLength + 1)} (usr_aaaaaaaa)");
        Line($"[Behaviour] OnPlayerJoined Bob (usr_{new string('a', VrchatClientStateTracker.MaxUserIdLength)})");

        _tracker.Players.Should().Equal("Alice");
    }
}

using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;
using static VRCRemoteTest.Bridge.VRChat.VrchatLogOwners;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Pure tests of the rules that tie a VRChat client to the output_log it has open, with
/// the owners Restart Manager would report written out. The cases follow what real
/// hardware showed (docs/validation/multi-client-spike.md, PR #3 verification, G): one
/// PID on each client's own file, none on a closed session's file, several on a mixed one.
/// </summary>
public class VrchatLogOwnersTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 10, 52, 25, 844, DateTimeKind.Utc);

    private static readonly VrchatClientInfo First = new(9960, T0, WatchWorldsDetected: true);
    private static readonly VrchatClientInfo Second = new(22156, T0.AddSeconds(3), WatchWorldsDetected: true);
    private static readonly VrchatClientInfo[] Clients = [First, Second];

    private static Owner Vrchat(VrchatClientInfo client) => new(client.ProcessId, client.CreationTimeUtc, "VRChat");

    private static Owner Process(int pid, string? name, DateTime? started = null) => new(pid, started ?? T0, name);

    private static FileOwners File(string name, params Owner[] owners) => new(name, owners);

    private static Decision Decide(params FileOwners[] files) => VrchatLogOwners.Decide(First, Clients, files);

    [Fact]
    public void The_file_only_the_client_has_open_is_its_log()
    {
        var decision = Decide(
            File("closed-session.txt"),
            File("first.txt", Vrchat(First)),
            File("second.txt", Vrchat(Second)));

        decision.Verdict.Should().Be(Verdict.Owned);
        decision.Path.Should().Be("first.txt");
        decision.Refused.Should().BeEquivalentTo(["second.txt"]);
        decision.Mixed.Should().BeEmpty();
        decision.AnyVrchatOwner.Should().BeTrue();
    }

    [Fact]
    public void Processes_that_are_not_VRChat_do_not_count()
    {
        var decision = Decide(File(
            "first.txt",
            Process(4242, "VRCRemoteTest.Bridge"),
            Vrchat(First),
            Process(3100, "MsMpEng", started: T0.AddDays(-1)),
            Process(5150, "Code")));

        decision.Verdict.Should().Be(Verdict.Owned);
        decision.Path.Should().Be("first.txt");
    }

    [Fact]
    public void A_file_the_client_shares_with_another_client_is_mixed()
    {
        var decision = Decide(File("shared.txt", Vrchat(First), Vrchat(Second)));

        decision.Verdict.Should().Be(Verdict.Shared);
        decision.Path.Should().Be("shared.txt");
        decision.Refused.Should().BeEquivalentTo(["shared.txt"]);
        decision.Mixed.Should().BeEquivalentTo(["shared.txt"]);
    }

    [Theory]
    [InlineData("VRChat")]
    [InlineData("vrchat")]
    public void A_VRChat_outside_the_snapshot_counts_as_another_client(string name)
    {
        // Another session's, one that could not be checked, or one started since the snapshot.
        var decision = Decide(File("shared.txt", Vrchat(First), Process(31337, name)));

        decision.Verdict.Should().Be(Verdict.Shared);
        decision.Mixed.Should().BeEquivalentTo(["shared.txt"]);
    }

    [Fact]
    public void A_snapshot_client_counts_as_VRChat_whatever_name_is_read_for_its_PID()
    {
        var decision = Decide(File("shared.txt", Vrchat(First), Process(Second.ProcessId, "notepad")));

        decision.Verdict.Should().Be(Verdict.Shared);
    }

    [Fact]
    public void A_co_owner_that_cannot_be_identified_settles_nothing()
    {
        var decision = Decide(File("first.txt", Vrchat(First), Process(7777, name: null)));

        decision.Verdict.Should().Be(Verdict.Inconclusive);
        decision.Path.Should().BeNull();
        decision.Refused.Should().BeEmpty();
    }

    [Fact]
    public void Another_VRChat_outweighs_an_owner_that_cannot_be_identified()
    {
        var decision = Decide(File("shared.txt", Vrchat(First), Process(7777, name: null), Vrchat(Second)));

        decision.Verdict.Should().Be(Verdict.Shared);
    }

    [Fact]
    public void Several_files_open_by_the_client_are_not_told_apart()
    {
        var decision = Decide(File("a.txt", Vrchat(First)), File("b.txt", Vrchat(First)));

        decision.Verdict.Should().Be(Verdict.Several);
        decision.Path.Should().BeNull();
    }

    [Fact]
    public void No_file_open_by_the_client_is_not_owned()
    {
        var decision = Decide(File("closed-session.txt"), File("editor.txt", Process(5150, "Code")));

        decision.Verdict.Should().Be(Verdict.NotOwned);
        decision.Refused.Should().BeEmpty();
        decision.AnyVrchatOwner.Should().BeFalse();
    }

    [Fact]
    public void No_files_at_all_is_not_owned()
    {
        Decide().Verdict.Should().Be(Verdict.NotOwned);
    }

    [Fact]
    public void Files_other_clients_have_open_are_refused_while_the_client_has_none()
    {
        var decision = Decide(File("second.txt", Vrchat(Second)), File("mixed.txt", Vrchat(Second), Process(31337, "VRChat")));

        decision.Verdict.Should().Be(Verdict.NotOwned);
        decision.Refused.Should().BeEquivalentTo(["second.txt", "mixed.txt"]);
        decision.Mixed.Should().BeEquivalentTo(["mixed.txt"]);
        decision.AnyVrchatOwner.Should().BeTrue();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(null)]
    public void The_clients_PID_with_another_or_no_start_time_settles_nothing(double? secondsLater)
    {
        // A reused PID, or a start time Restart Manager could not read: maybe the client, maybe not.
        var owner = new Owner(First.ProcessId, secondsLater is { } s ? T0.AddSeconds(s) : null, "VRChat");

        var decision = Decide(File("first.txt", owner));

        decision.Verdict.Should().Be(Verdict.Inconclusive);
        decision.Refused.Should().BeEmpty("the owner may be the client itself");
    }

    [Fact]
    public void An_unconfirmed_PID_elsewhere_keeps_even_a_clear_file_from_being_chosen()
    {
        var decision = Decide(
            File("first.txt", Vrchat(First)),
            File("other.txt", new Owner(First.ProcessId, T0.AddSeconds(1), "VRChat")));

        decision.Verdict.Should().Be(Verdict.Inconclusive);
    }

    [Fact]
    public void The_start_times_match_within_a_microsecond()
    {
        var owner = new Owner(First.ProcessId, T0.AddTicks(9), "VRChat");

        Decide(File("first.txt", owner)).Verdict.Should().Be(Verdict.Owned);
        Decide(File("first.txt", owner with { StartTimeUtc = T0.AddTicks(10) })).Verdict.Should().Be(Verdict.Inconclusive);
    }

    [Fact]
    public void A_client_without_a_creation_time_cannot_be_found_but_refusals_still_hold()
    {
        var client = First with { CreationTimeUtc = null };

        var decision = VrchatLogOwners.Decide(
            client, [client, Second], [File("first.txt", Vrchat(First)), File("second.txt", Vrchat(Second))]);

        decision.Verdict.Should().Be(Verdict.Inconclusive);
        decision.Refused.Should().BeEquivalentTo(["second.txt"]);
    }

    [Fact]
    public void A_process_listed_twice_counts_once()
    {
        Decide(File("first.txt", Vrchat(First), Vrchat(First))).Verdict.Should().Be(Verdict.Owned);
    }

    [Fact]
    public void Paths_compare_without_case()
    {
        var decision = Decide(File(@"C:\Logs\Output_Log_1.txt", Vrchat(Second)));

        decision.Refused.Should().Contain(@"c:\logs\output_log_1.txt");
    }
}

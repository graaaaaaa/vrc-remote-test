using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// VrchatProcessMonitor is the sole class touching Process enumeration and WMI
/// (System.Management). On non-Windows dev machines (macOS), the enumeration tests only
/// verify the cross-platform-safe degradation path -- real WMI --watch-worlds detection
/// and session filtering are confirmed on real Windows hardware during the plan's
/// verification step. The selection and status rules are pure and tested directly.
/// </summary>
public class VrchatProcessMonitorTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GetStatus_does_not_throw_and_reports_not_running_when_vrchat_is_absent()
    {
        var monitor = new VrchatProcessMonitor(NullLogger<VrchatProcessMonitor>.Instance);

        var status = monitor.GetStatus();

        status.IsRunning.Should().BeFalse();
        status.WatchWorldsDetected.Should().BeFalse();
        status.ProcessId.Should().BeNull();
    }

    [Fact]
    public void GetStatus_sets_UpdatedAtUtc_to_a_recent_timestamp()
    {
        var monitor = new VrchatProcessMonitor(NullLogger<VrchatProcessMonitor>.Instance);
        var before = DateTimeOffset.UtcNow;

        var status = monitor.GetStatus();

        status.UpdatedAtUtc.Should().BeOnOrAfter(before);
        status.UpdatedAtUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void GetClients_reports_no_clients_when_vrchat_is_absent()
    {
        var snapshot = new VrchatProcessMonitor(NullLogger<VrchatProcessMonitor>.Instance).GetClients();

        snapshot.Clients.Should().BeEmpty();
        snapshot.UnverifiedCount.Should().Be(0);
        snapshot.EnumerationError.Should().BeNull();
    }

    [Fact]
    public void The_representative_is_the_oldest_client_with_watch_worlds()
    {
        var clients = new[]
        {
            new VrchatClientInfo(30, T0.AddSeconds(6), true),
            new VrchatClientInfo(10, T0, false),
            new VrchatClientInfo(20, T0.AddSeconds(3), true),
        };

        VrchatProcessMonitor.SelectRepresentative(clients)!.ProcessId.Should().Be(20);
    }

    [Fact]
    public void Without_watch_worlds_the_representative_is_the_oldest_client()
    {
        var clients = new[]
        {
            new VrchatClientInfo(30, T0.AddSeconds(6), false),
            new VrchatClientInfo(40, null, false),
            new VrchatClientInfo(20, T0, false),
        };

        VrchatProcessMonitor.SelectRepresentative(clients)!.ProcessId.Should().Be(20);
    }

    [Fact]
    public void Unknown_creation_times_sort_last_and_the_PID_breaks_ties()
    {
        var clients = new[]
        {
            new VrchatClientInfo(50, null, true),
            new VrchatClientInfo(40, null, true),
        };

        VrchatProcessMonitor.SelectRepresentative(clients)!.ProcessId.Should().Be(40);
        VrchatProcessMonitor.SelectRepresentative([.. clients, new VrchatClientInfo(60, T0, true)])!
            .ProcessId.Should().Be(60);
    }

    [Fact]
    public void BuildStatus_keeps_the_single_client_fields_and_writes_the_creation_time_in_UTC()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new VrchatClientSnapshot(
            [new VrchatClientInfo(20, T0, true), new VrchatClientInfo(30, T0.AddSeconds(3), true)]);

        var status = VrchatProcessMonitor.BuildStatus(snapshot, now);

        status.IsRunning.Should().BeTrue();
        status.WatchWorldsDetected.Should().BeTrue();
        status.ProcessId.Should().Be(20);
        status.StartTimeUtc.Should().Be(new DateTimeOffset(T0, TimeSpan.Zero));
        status.StartTimeUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
        status.UpdatedAtUtc.Should().Be(now);
    }

    [Fact]
    public void BuildStatus_reports_an_unverified_VRChat_as_running_without_watch_worlds()
    {
        var status = VrchatProcessMonitor.BuildStatus(new VrchatClientSnapshot([], unverifiedCount: 1), DateTimeOffset.UtcNow);

        status.IsRunning.Should().BeTrue();
        status.WatchWorldsDetected.Should().BeFalse();
        status.ProcessId.Should().BeNull();
    }

    [Fact]
    public void BuildStatus_reports_not_running_when_there_are_no_clients()
    {
        var status = VrchatProcessMonitor.BuildStatus(VrchatClientSnapshot.None, DateTimeOffset.UtcNow);

        status.IsRunning.Should().BeFalse();
        status.StartTimeUtc.Should().BeNull();
    }

    // === One WMI observation per poll (Codex code review rounds 2-3) ===

    private const int Session = 1;

    private static VrchatProcessMonitor.Candidate Candidate(int pid, DateTime? createdUtc) => new(pid, createdUtc);

    /// <summary>CIM_DATETIME for <paramref name="utc"/> at UTC+09:00, as WMI on a JST machine returns it.</summary>
    private static string Cim(DateTime utc)
    {
        var local = utc + TimeSpan.FromHours(9);
        return $"{local:yyyyMMddHHmmss}.{local.Ticks % TimeSpan.TicksPerSecond / 10:D6}+540";
    }

    private static VrchatProcessMonitor.WmiProcessRow Row(
        int pid, DateTime? createdUtc, string? commandLine = "VRChat.exe --watch-worlds --no-vr", int? session = Session) =>
        new(pid, session, createdUtc is { } c ? Cim(c) : null, commandLine);

    [Fact]
    public void A_matching_row_makes_a_client_with_the_native_creation_time()
    {
        var created = T0.AddTicks(3_505_842);

        var observation = VrchatProcessMonitor.Classify(
            [Candidate(10, created)], [Row(10, created)], Session);

        observation.Clients.Should().ContainSingle().Which.Should().Be(new VrchatClientInfo(10, created, true));
        observation.Unverified.Should().Be(0);
        observation.Vanished.Should().Be(0);
    }

    [Fact]
    public void The_command_line_decides_watch_worlds()
    {
        var observation = VrchatProcessMonitor.Classify(
            [Candidate(10, T0)], [Row(10, T0, "VRChat.exe --no-vr")], Session);

        observation.Clients.Should().ContainSingle().Which.WatchWorldsDetected.Should().BeFalse();
    }

    [Fact]
    public void A_failed_query_makes_every_candidate_unverified_not_without_watch_worlds()
    {
        var observation = VrchatProcessMonitor.Classify(
            [Candidate(10, T0), Candidate(11, T0)], rows: null, Session);

        observation.Clients.Should().BeEmpty();
        observation.Unverified.Should().Be(2);
    }

    [Fact]
    public void An_unreadable_command_line_makes_only_that_client_unverified()
    {
        var observation = VrchatProcessMonitor.Classify(
            [Candidate(10, T0), Candidate(11, T0)], [Row(10, T0, commandLine: null), Row(11, T0)], Session);

        observation.Clients.Should().ContainSingle().Which.ProcessId.Should().Be(11);
        observation.Unverified.Should().Be(1);
    }

    [Fact]
    public void A_missing_row_or_another_session_means_the_PID_no_longer_names_the_process()
    {
        var observation = VrchatProcessMonitor.Classify(
            [Candidate(10, T0), Candidate(11, T0)], [Row(11, T0, session: 2)], Session);

        observation.Clients.Should().BeEmpty();
        observation.Vanished.Should().Be(2);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(-9, true)]
    [InlineData(10, false)]
    [InlineData(-10, false)]
    public void A_creation_time_off_by_a_microsecond_or_more_is_another_process(int ticks, bool sameProcess)
    {
        // WMI keeps microseconds; the native time has 100 ns ticks.
        var wmiTime = T0.AddTicks(3_505_840);
        var row = new VrchatProcessMonitor.WmiProcessRow(10, Session, Cim(wmiTime), "VRChat.exe --watch-worlds");

        var observation = VrchatProcessMonitor.Classify([Candidate(10, wmiTime.AddTicks(ticks))], [row], Session);

        observation.Clients.Should().HaveCount(sameProcess ? 1 : 0);
        observation.Vanished.Should().Be(sameProcess ? 0 : 1);
    }

    [Fact]
    public void An_unreadable_WMI_creation_time_makes_a_client_with_a_known_start_time_unverified()
    {
        var row = new VrchatProcessMonitor.WmiProcessRow(10, Session, "not a date", "VRChat.exe --watch-worlds");

        var observation = VrchatProcessMonitor.Classify([Candidate(10, T0)], [row], Session);

        observation.Unverified.Should().Be(1);
    }

    [Fact]
    public void Without_a_native_start_time_the_row_is_accepted_and_the_creation_time_stays_unknown()
    {
        var observation = VrchatProcessMonitor.Classify([Candidate(10, null)], [Row(10, T0)], Session);

        observation.Clients.Should().ContainSingle().Which.CreationTimeUtc.Should().BeNull(
            "WMI's microsecond time would give the client a different identity once StartTime is readable");
    }

    [Fact]
    public void ParseCimDateTimeUtc_uses_the_explicit_offset()
    {
        VrchatProcessMonitor.ParseCimDateTimeUtc("20261017154107.350584+540")
            .Should().Be(new DateTime(2026, 10, 17, 6, 41, 7, DateTimeKind.Utc).AddTicks(3_505_840));
        VrchatProcessMonitor.ParseCimDateTimeUtc("20261017014107.350584-300")
            .Should().Be(new DateTime(2026, 10, 17, 6, 41, 7, DateTimeKind.Utc).AddTicks(3_505_840));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20261017154107.******+540")]
    [InlineData("20261317154107.350584+540")]
    [InlineData("20261017154107.350584+54")]
    [InlineData("20261017154107,350584+540")]
    [InlineData("20261017154107.350584*540")]
    public void ParseCimDateTimeUtc_rejects_anything_else(string? value)
    {
        VrchatProcessMonitor.ParseCimDateTimeUtc(value).Should().BeNull();
    }

    [Fact]
    public void ObserveCandidates_asks_WMI_once_for_every_candidate_in_the_Bridges_session()
    {
        var calls = new List<(int Session, int[] Pids)>();
        var monitor = new VrchatProcessMonitor(
            NullLogger<VrchatProcessMonitor>.Instance,
            currentSessionId: Session,
            queryWmi: (session, pids) =>
            {
                calls.Add((session, pids.ToArray()));
                return [Row(10, T0), Row(11, T0)];
            });

        var observation = monitor.ObserveCandidates([Candidate(10, T0), Candidate(11, T0)]);

        calls.Should().ContainSingle();
        calls[0].Session.Should().Be(Session);
        calls[0].Pids.Should().Equal(10, 11);
        observation.Clients.Should().HaveCount(2);
    }

    [Fact]
    public void ObserveCandidates_does_not_query_WMI_without_candidates()
    {
        var called = false;
        var monitor = new VrchatProcessMonitor(
            NullLogger<VrchatProcessMonitor>.Instance,
            currentSessionId: Session,
            queryWmi: (_, _) =>
            {
                called = true;
                return [];
            });

        monitor.ObserveCandidates([]).Clients.Should().BeEmpty();
        called.Should().BeFalse();
    }

    [Fact]
    public void ObserveCandidates_treats_a_throwing_query_as_unverified()
    {
        var monitor = new VrchatProcessMonitor(
            NullLogger<VrchatProcessMonitor>.Instance,
            currentSessionId: Session,
            queryWmi: (_, _) => throw new InvalidOperationException("simulated WMI timeout"));

        var observation = monitor.ObserveCandidates([Candidate(10, T0)]);

        observation.Clients.Should().BeEmpty();
        observation.Unverified.Should().Be(1);
    }

    [Fact]
    public void BuildStatus_keeps_writing_running_when_WMI_could_not_check_the_clients()
    {
        var status = VrchatProcessMonitor.BuildStatus(new VrchatClientSnapshot([], unverifiedCount: 2), DateTimeOffset.UtcNow);

        status.IsRunning.Should().BeTrue();
        status.WatchWorldsDetected.Should().BeFalse();
    }
}

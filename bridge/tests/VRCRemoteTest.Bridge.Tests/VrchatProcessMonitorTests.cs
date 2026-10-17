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
}

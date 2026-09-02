using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// VrchatProcessMonitor is the sole class touching Process enumeration and WMI
/// (System.Management). On non-Windows dev machines (macOS), this only verifies the
/// cross-platform-safe degradation path -- it never exercises real WMI --watch-worlds
/// detection, which is confirmed on real Windows hardware during the plan's verification
/// step, not by this test.
/// </summary>
public class VrchatProcessMonitorTests
{
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
}

using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Only the pure argument builder is tested here; starting a real process is confirmed
/// on Windows hardware (multi-client plan section 8).
/// </summary>
public class VrchatLauncherTests
{
    private static VrchatLaunchRequest Request(string worldPath = @"C:\Worlds\vrc-remote-b1.vrcw", bool noVr = true) =>
        new(@"C:\VRChat\VRChat.exe", worldPath, "0123456789", noVr);

    [Fact]
    public void Matches_the_SDK_build_and_test_arguments_for_a_desktop_client()
    {
        var arguments = VrchatLauncher.BuildArguments(Request());

        arguments.Should().Equal(
            "--url=create?roomId=0123456789&hidden=true&name=BuildAndRun&url=file:///C%3A%5CWorlds%5Cvrc-remote-b1.vrcw",
            "--watch-worlds",
            "--no-vr",
            "--enable-debug-gui",
            "--enable-sdk-log-levels",
            "--enable-udon-debug-logging");
    }

    [Fact]
    public void Omits_no_vr_for_the_HMD_client()
    {
        VrchatLauncher.BuildArguments(Request(noVr: false)).Should().NotContain("--no-vr");
    }

    [Fact]
    public void Percent_encodes_spaces_ampersands_colons_and_backslashes_in_the_world_path()
    {
        var url = VrchatLauncher.BuildArguments(Request(@"C:\Users\A B\x&y=1\vrc-remote-b1.vrcw"))[0];

        url.Should().EndWith("&url=file:///C%3A%5CUsers%5CA%20B%5Cx%26y%3D1%5Cvrc-remote-b1.vrcw");
        url.Should().NotContain(" ");
        url.Split('&').Should().HaveCount(4, "nothing in the path adds a query parameter");
    }

    [Fact]
    public void Never_passes_a_profile()
    {
        VrchatLauncher.BuildArguments(Request()).Should().NotContain(a => a.StartsWith("--profile", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345&x=1")]
    [InlineData("１２３")]
    public void Rejects_a_room_id_that_is_not_ASCII_digits(string roomId)
    {
        var act = () => VrchatLauncher.BuildArguments(Request() with { RoomId = roomId });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NewRoomId_is_ten_ASCII_digits()
    {
        VrchatLaunchRequest.NewRoomId().Should().MatchRegex("^[0-9]{10}$");
    }
}

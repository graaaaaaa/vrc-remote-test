using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steamapps\\common\\VRChat\\VRChat.exe\" --watch-worlds --no-vr")]
    [InlineData("VRChat.exe --no-vr\t--watch-worlds")]
    [InlineData("VRChat.exe \"--watch-worlds\"")]
    [InlineData("VRChat.exe --url=create?roomId=1&url=file:///C%3A%5Cw.vrcw --watch-worlds")]
    [InlineData("C:\\\"VR Chat\"\\VRChat.exe --watch-worlds")]
    [InlineData("x.exe \"a\"\"\" --watch-worlds")]
    public void Detects_watch_worlds_as_a_whole_argument(string commandLine)
    {
        WindowsCommandLine.ContainsArgument(commandLine, "--watch-worlds").Should().BeTrue();
    }

    [Theory]
    [InlineData("VRChat.exe --no-vr")]
    [InlineData("VRChat.exe --url=create?name=--watch-worlds")]
    [InlineData("VRChat.exe \"C:\\worlds\\x --watch-worlds y.vrcw\"")]
    [InlineData("VRChat.exe --watch-worlds-disabled")]
    [InlineData("VRChat.exe --WATCH-WORLDS")]
    [InlineData("C:\\--watch-worlds\\VRChat.exe --no-vr")]
    [InlineData("\"C:\\a --watch-worlds\\VRChat.exe\" --no-vr")]
    [InlineData("\"C:\\VRChat\\VRChat.exe\"--watch-worlds")]
    [InlineData("x.exe \"a\"\" --watch-worlds")]
    [InlineData("")]
    [InlineData(null)]
    public void Does_not_detect_watch_worlds_inside_another_argument_or_the_program_path(string? commandLine)
    {
        WindowsCommandLine.ContainsArgument(commandLine, "--watch-worlds").Should().BeFalse();
    }

    [Fact]
    public void Splits_with_the_C_runtime_quoting_and_backslash_rules()
    {
        var arguments = WindowsCommandLine.Split(
            "\"C:\\VR Chat\\VRChat.exe\" a\\\\b \"c d\" e\\\"f \"g\\\\\" h\"\"i \"j\"\"k\" l\\\\\\\"m");

        arguments.Should().Equal(
            @"C:\VR Chat\VRChat.exe",
            @"a\\b",
            "c d",
            "e\"f",
            @"g\",
            "hi",
            "j\"k",
            "l\\\"m");
    }

    [Fact]
    public void Quotes_inside_the_program_path_only_toggle_quoting()
    {
        WindowsCommandLine.Split("C:\\\"VR Chat\"\\VRChat.exe --watch-worlds")
            .Should().Equal(@"C:\VR Chat\VRChat.exe", "--watch-worlds");
        WindowsCommandLine.Split("\"C:\\VRChat\\VRChat.exe\"--watch-worlds")
            .Should().Equal(@"C:\VRChat\VRChat.exe--watch-worlds");
    }

    [Fact]
    public void Keeps_an_unterminated_quote_to_the_end()
    {
        WindowsCommandLine.Split("x.exe \"--watch-worlds --no-vr").Should().Equal("x.exe", "--watch-worlds --no-vr");
    }
}

using System.Text;
using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Client-observation plan C6: the header and the line delimiter.</summary>
public class VrchatLogLineTests
{
    [Theory]
    [InlineData("2026.10.18 13:34:37 Debug      -  [Behaviour] Joining local:x", "Debug")]
    [InlineData("2026.10.18 13:34:37 Warning    -  text", "Warning")]
    [InlineData("2026.10.18 13:34:37 Error      -  text", "Error")]
    [InlineData("2026.10.18 13:34:37 Error      -  ", "Error")]
    public void Reads_the_header_of_a_real_line(string line, string level)
    {
        VrchatLogLine.TryParseHeader(line, out var header).Should().BeTrue();
        header.Level.ToString().Should().Be(level);
        header.LocalTime.Should().Be(new DateTime(2026, 10, 18, 13, 34, 37));
    }

    [Theory]
    [InlineData("    2026.10.18 13:34:37 Debug      -  indented")]
    [InlineData("2026.10.18 13:34:37 Debug     -  one space short")]
    [InlineData("2026.10.18 13:34:37 Debug       -  one space more")]
    [InlineData("2026.10.18 13:34:37 Debug      - one space after the dash")]
    [InlineData("2026.13.18 13:34:37 Debug      -  month 13")]
    [InlineData("2026.10.18 13:34:37 Verbose    -  unknown level")]
    [InlineData("２026.10.18 13:34:37 Debug      -  full-width digit")]
    [InlineData("System.Exception: no header")]
    public void Refuses_anything_else(string line)
    {
        VrchatLogLine.TryParseHeader(line, out _).Should().BeFalse();
    }

    [Fact]
    public void Only_a_CRLF_ends_a_line()
    {
        var bytes = Encoding.UTF8.GetBytes("a\nb\r\nc\r\r\nd");

        VrchatLogLine.SplitPhysicalLines(bytes).Should().Equal("a\nb", "c\r", "d");
    }

    [Fact]
    public void A_header_after_a_lone_LF_is_a_continuation()
    {
        const string header = "2026.10.18 13:34:37 Debug      -  ";

        VrchatLogLine.ToDisplayLines(header + "real\n" + header + "forged\n\nend").Should().Equal(
            header + "real", "    " + header + "forged", string.Empty, "    end");
        VrchatLogLine.ToDisplayLines("  at VRC.Udon").Should().Equal("      at VRC.Udon");
    }
}

using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Client-observation plan C5: what is masked before anything reaches the share.</summary>
public class LogRedactorTests
{
    [Theory]
    [InlineData(
        "OnPlayerJoined Name (usr_0123abcd-0000-4000-8000-0123456789ab)",
        "OnPlayerJoined Name (usr_***)")]
    [InlineData("legacy usr_8JoV9XEdpo id", "legacy usr_*** id")]
    [InlineData(
        @"World Updated: C:\Users\alice\AppData\LocalLow\VRChat\VRChat\Worlds\a.vrcw // 1",
        @"World Updated: C:\Users\<user>\AppData\LocalLow\VRChat\VRChat\Worlds\a.vrcw // 1")]
    [InlineData(@"d:/users/Bob Smith/file.txt", @"d:/users/<user>/file.txt")]
    [InlineData("connected to 192.168.10.20:7777", "connected to <ip>:7777")]
    [InlineData("peer fe80::1ff:fe23:4567:890a%3 up", "peer <ip> up")]
    [InlineData("peer 2001:db8:85a3:0:0:8a2e:370:7334 up", "peer <ip> up")]
    [InlineData("Authorization: Bearer abc.def", "Authorization: <redacted>")]
    [InlineData("Cookie: auth=authcookie_1234; twoFactorAuth=xyz", "Cookie: <redacted>")]
    [InlineData("https://api.example/x?auth=secret123&n=1", "https://api.example/x?auth=<redacted>&n=1")]
    [InlineData("{\"token\": \"abc123\"}", "{\"token\": \"<redacted>\"}")]
    [InlineData("value authcookie_0f1e2d3c-aaaa", "value authcookie_<redacted>")]
    [InlineData("https://user:pa55@host/path", "https://<redacted>@host/path")]
    public void Masks_each_kind_of_value(string input, string expected)
    {
        LogRedactor.Redact(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("2026.10.18 13:34:37 Debug      -  [Behaviour] Joining local:dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383")]
    [InlineData("2026.10.18 13:41:39 Debug      -  [Behaviour] Destination set: local:dab8e4:1")]
    [InlineData("User Authenticated: Name")]
    [InlineData("version 1.2.3 and std::string and 13:34:37")]
    [InlineData("[SPK003] PLAYER_LEFT id=1 local=False")]
    [InlineData("999.1.1.1 is not an address")]
    public void Leaves_ordinary_lines_alone(string line)
    {
        LogRedactor.Redact(line).Should().Be(line);
    }
}

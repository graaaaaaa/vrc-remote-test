using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Masks what a VRChat log may carry about the Windows machine and the accounts before
/// anything derived from it is written to the share (client-observation plan C5): VRChat
/// user IDs (usr_…), the user name in Windows profile paths, IPv4 and IPv6 addresses, and
/// authentication values (Authorization and Cookie headers, token-like key=value pairs,
/// VRChat's authcookie_ values, user:password@ in URLs). VRChat's support article on output
/// logs warns that they can contain the PC's user name and IP addresses.
///
/// Callers mask first and cut to their size limits afterwards, so a cut never leaves the
/// unmasked start of a value behind. Display names stay: the share is for a single
/// operator, and docs/setup-windows.md lists them as a residual risk. Neither can this find
/// every secret a world writes with Debug.Log.
///
/// Every pattern is linear and has a match timeout; a line that still times out is
/// replaced as a whole rather than published unmasked.
/// </summary>
internal static class LogRedactor
{
    internal const string Redacted = "<redacted>";
    internal const string UserPlaceholder = "<user>";
    internal const string AddressPlaceholder = "<ip>";
    internal const string UserIdPlaceholder = "usr_***";
    internal const string TimedOut = "<line withheld: masking timed out>";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>The rest of the line after a header that carries credentials.</summary>
    private static readonly Regex CredentialHeader = new(
        @"(?im)\b(authorization|proxy-authorization|cookie|set-cookie|x-auth-token|x-api-key)(\s*[:=]\s*)[^\r\n]*",
        Options,
        MatchTimeout);

    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+", Options, MatchTimeout);

    /// <summary>token=…, "password": "…", ?auth=… and the like; the value runs to a separator.</summary>
    private static readonly Regex SecretValue = new(
        @"(?i)(?<![A-Za-z0-9_-])(""?)(auth|authtoken|auth_token|authcookie|token|access_token|accesstoken|id_token|refresh_token|apikey|api_key|twofactorauth|password|passwd|secret|client_secret)(""?\s*[=:]\s*""?)[^\s""&;,'<>]+",
        Options,
        MatchTimeout);

    /// <summary>The value of VRChat's auth cookie, wherever it appears.</summary>
    private static readonly Regex AuthCookie = new(@"(?i)\bauthcookie_[0-9A-Za-z-]+", Options, MatchTimeout);

    private static readonly Regex UrlUserInfo = new(
        @"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@", Options, MatchTimeout);

    /// <summary>usr_ and a UUID, or the shorter IDs of old accounts.</summary>
    private static readonly Regex UserId = new(@"(?<![A-Za-z0-9])usr_[0-9A-Za-z-]{8,}", Options, MatchTimeout);

    /// <summary>
    /// C:\Users\name\ or C:/Users/name/ on any drive: the name is masked up to the next
    /// separator, so a name with spaces is masked whole (and at the end of a line, so is
    /// whatever follows it).
    /// </summary>
    private static readonly Regex ProfilePath = new(
        @"(?i)(?<![A-Za-z])([A-Z]:[\\/]+Users[\\/]+)[^\\/\r\n\t""'<>|:*?]+", Options, MatchTimeout);

    private static readonly Regex Ipv4 = new(
        @"(?<![0-9.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![0-9])", Options, MatchTimeout);

    /// <summary>A run of hex digits and colons with at least two colons; checked by IPAddress.TryParse.</summary>
    private static readonly Regex Ipv6Candidate = new(
        @"(?<![0-9A-Za-z_:.])(?=[0-9A-Fa-f:]*:[0-9A-Fa-f]*:)[0-9A-Fa-f:]{2,39}(?:%[0-9A-Za-z]{1,16})?(?![0-9A-Za-z_:])",
        Options,
        MatchTimeout);

    public static string Redact(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        try
        {
            text = CredentialHeader.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
            text = Bearer.Replace(text, "Bearer " + Redacted);
            text = SecretValue.Replace(
                text, m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + Redacted);
            text = AuthCookie.Replace(text, "authcookie_" + Redacted);
            text = UrlUserInfo.Replace(text, m => m.Groups[1].Value + Redacted + "@");
            text = UserId.Replace(text, UserIdPlaceholder);
            text = ProfilePath.Replace(text, m => m.Groups[1].Value + UserPlaceholder);
            text = Ipv4.Replace(text, m => IsIpv4(m) ? AddressPlaceholder : m.Value);
            text = Ipv6Candidate.Replace(text, m => IsIpv6(m.Value) ? AddressPlaceholder : m.Value);
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            return TimedOut;
        }
    }

    private static bool IsIpv4(Match match)
    {
        for (var group = 1; group <= 4; group++)
        {
            if (int.Parse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) > 255)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIpv6(string candidate) =>
        IPAddress.TryParse(candidate, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
}

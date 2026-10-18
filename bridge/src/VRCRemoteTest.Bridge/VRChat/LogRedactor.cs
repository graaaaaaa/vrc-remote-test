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
/// Every pattern has a match timeout, and the one that would otherwise backtrack
/// quadratically on long runs (user:password@ in URLs) uses the non-backtracking engine. A
/// line that still times out, or that any pattern fails on, is replaced as a whole rather
/// than published unmasked. Digits are ASCII only: .NET's \d also matches other scripts'
/// digits, which int.Parse would then refuse.
/// </summary>
internal static class LogRedactor
{
    internal const string Redacted = "<redacted>";
    internal const string UserPlaceholder = "<user>";
    internal const string AddressPlaceholder = "<ip>";
    internal const string UserIdPlaceholder = "usr_***";
    internal const string TimedOut = "<line withheld: masking timed out>";
    internal const string Failed = "<line withheld: masking failed>";

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>The rest of the line after a header that carries credentials.</summary>
    private static readonly Regex CredentialHeader = new(
        @"(?im)\b(authorization|proxy-authorization|cookie|set-cookie|x-auth-token|x-api-key)(\s*[:=]\s*)[^\r\n]*",
        Options,
        MatchTimeout);

    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+", Options, MatchTimeout);

    private const string SecretNames =
        "auth|authtoken|auth_token|authcookie|token|access_token|accesstoken|id_token|refresh_token|apikey|api_key|" +
        "twofactorauth|password|passwd|secret|client_secret|cookie|set-cookie";

    /// <summary>
    /// "password": "correct horse", 'token' = 'x': a quoted value is masked to its closing
    /// quote, spaces, escaped quotes (\") and LFs included, or to the end of the text when
    /// the quote is missing (a cut line).
    /// </summary>
    private static readonly Regex QuotedSecretValue = new(
        @"(?i)(?<![A-Za-z0-9_-])([""']?)(" + SecretNames + @")([""']?\s*[=:]\s*)([""'])(?:\\[\s\S]|(?!\4)[^\\\r])*(\4?)",
        Options,
        MatchTimeout);

    /// <summary>token=…, ?auth=… and the like, unquoted; the value runs to a separator.</summary>
    private static readonly Regex SecretValue = new(
        @"(?i)(?<![A-Za-z0-9_-])([""']?)(" + SecretNames + @")([""']?\s*[=:]\s*)(?![""'])[^\s""&;,'<>]+",
        Options,
        MatchTimeout);

    /// <summary>The value of VRChat's auth cookie, wherever it appears.</summary>
    private static readonly Regex AuthCookie = new(@"(?i)\bauthcookie_[0-9A-Za-z-]+", Options, MatchTimeout);

    /// <summary>Non-backtracking: on a long run like "a-a-a-…" the backtracking engine takes quadratic time.</summary>
    private static readonly Regex UrlUserInfo = new(
        @"(?i)\b([a-z][a-z0-9+.-]*://)[^/\s:@]+:[^/\s@]+@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        MatchTimeout);

    /// <summary>usr_ and a UUID, or the shorter IDs of old accounts.</summary>
    private static readonly Regex UserId = new(@"(?<![A-Za-z0-9])usr_[0-9A-Za-z-]{8,}", Options, MatchTimeout);

    /// <summary>
    /// C:\Users\name\ or C:/Users/name/ on any drive: the name is masked up to the next
    /// separator, so a name with spaces is masked whole (and at the end of a line, so is
    /// whatever follows it).
    /// </summary>
    private static readonly Regex ProfilePath = new(
        @"(?i)(?<![A-Za-z])([A-Z]:[\\/]+Users[\\/]+)[^\\/\r\n\t""<>|:*?]+", Options, MatchTimeout);

    private static readonly Regex Ipv4 = new(
        @"(?<![0-9.])([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})\.([0-9]{1,3})(?![0-9])", Options, MatchTimeout);

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
            // Quoted values first: a header's rule stops at a LF that a quoted value may span.
            text = QuotedSecretValue.Replace(
                text,
                m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + m.Groups[4].Value + Redacted + m.Groups[5].Value);
            text = CredentialHeader.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Redacted);
            text = Bearer.Replace(text, "Bearer " + Redacted);
            text = SecretValue.Replace(
                text, m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + Redacted);
            text = AuthCookie.Replace(text, "authcookie_" + Redacted);
            if (text.Contains("://", StringComparison.Ordinal))
            {
                text = UrlUserInfo.Replace(text, m => m.Groups[1].Value + Redacted + "@");
            }
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
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return Failed;
        }
    }

    /// <summary>Whether <paramref name="text"/> is one of the placeholders that replace a whole line.</summary>
    internal static bool IsWithheld(string text) => text is TimedOut or Failed;

    private static bool IsIpv4(Match match)
    {
        for (var group = 1; group <= 4; group++)
        {
            if (!int.TryParse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var octet)
                || octet > 255)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIpv6(string candidate) =>
        IPAddress.TryParse(candidate, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
}

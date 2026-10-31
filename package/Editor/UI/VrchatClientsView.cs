using System;
using System.Globalization;
using System.Linq;

namespace VRCRemoteTest
{
    /// <summary>
    /// The text of the window's "VRChat Clients" section, kept out of IMGUI for the tests.
    /// Everything that comes from the Bridge is untrusted and sanitized here.
    /// </summary>
    internal static class VrchatClientsView
    {
        /// <summary>Order matches <see cref="ModeOf"/>.</summary>
        internal static readonly string[] ModeLabels =
        {
            "Reload – one account (--watch-worlds)",
            "Relaunch – one account per profile",
        };

        /// <summary>Several clients take longer to close and start than the 60 s default (multi-client spike: about 21 s for 4 to enter).</summary>
        internal const int RecommendedTimeoutSeconds = 180;

        internal static int ModeIndex(VrchatClientSettings settings) => settings != null && settings.IsRelaunch ? 1 : 0;

        internal static string ModeOf(int index) =>
            index == 1 ? VrchatClientSettings.RelaunchMode : VrchatClientSettings.ReloadMode;

        /// <summary>
        /// What the fields describe, or null with <paramref name="problem"/> set when the
        /// profile list cannot be sent.
        /// </summary>
        internal static VrchatClientSettings Desired(int modeIndex, int count, string profilesText, out string problem)
        {
            problem = null;
            if (modeIndex != 1)
            {
                return VrchatClientSettings.Reload(Math.Min(Math.Max(1, count), VrchatClientSettings.MaxClients));
            }

            return VrchatClientSettings.TryParseProfiles(profilesText, out var profiles, out problem)
                ? VrchatClientSettings.Relaunch(profiles)
                : null;
        }

        /// <summary>"Tester (01234567) · seen 2026-11-01 10:02", or how to get the profile logged in.</summary>
        internal static string DescribeProfile(int profile, BridgeProfiles profiles, TimeZoneInfo zone = null)
        {
            var account = profiles?.Profiles?.FirstOrDefault(p => p != null && p.Profile == profile);
            if (account == null)
            {
                return profiles == null
                    ? "account unknown"
                    : "no login seen yet: log in to this profile on Windows once (start-vrchat-dev.ps1 -Profiles "
                      + profile.ToString(CultureInfo.InvariantCulture) + ")";
            }

            var name = string.IsNullOrWhiteSpace(account.DisplayName) ? "unknown name" : BridgeMessages.Sanitize(account.DisplayName, 64);
            var digest = string.IsNullOrEmpty(account.IdDigest) ? string.Empty : " (" + ClientLogView.ShortDigest(account.IdDigest) + ")";
            var seen = account.AuthenticatedAtUtc is DateTimeOffset at
                ? " · seen " + TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            return name + digest + seen;
        }

        /// <summary>True when applying <paramref name="desired"/> makes the Bridge close the clients it launched.</summary>
        internal static bool ClosesClients(VrchatClientSettings active, VrchatClientSettings desired) =>
            active != null && desired != null && active.Mode != desired.Mode;

        internal static bool NeedsLongerTimeout(VrchatClientSettings desired, int resultTimeoutSeconds) =>
            desired != null && desired.Count >= 3 && resultTimeoutSeconds < RecommendedTimeoutSeconds;

        /// <summary>
        /// A Reload Bridge does not add or remove clients while some run; null when that does
        /// not matter for this change.
        /// </summary>
        internal static string ReloadCountNote(VrchatClientSettings active, VrchatClientSettings desired) =>
            active != null && desired != null && !active.IsRelaunch && !desired.IsRelaunch && active.Count != desired.Count
                ? "Running Reload clients are kept as they are. Close them (button below) so that the next deploy launches "
                  + desired.Count + "."
                : null;
    }
}

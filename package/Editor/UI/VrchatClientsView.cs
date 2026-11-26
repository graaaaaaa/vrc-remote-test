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
            "Reload (one account)",
            "Relaunch (one per profile)",
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

        /// <summary>
        /// "Tester (01234567) · seen 2026-11-01 10:02", or that the Bridge has not seen the account:
        /// it learns it only from clients it launched, so a login made with start-vrchat-dev.ps1
        /// shows after the first deploy that launches the profile.
        /// </summary>
        internal static string DescribeProfile(int profile, BridgeProfiles profiles, TimeZoneInfo zone = null)
        {
            var account = profiles?.Profiles?.FirstOrDefault(p => p != null && p.Profile == profile);
            if (account == null)
            {
                return profiles == null
                    ? "account unknown"
                    : "account not seen yet: shown once the Bridge launches this profile. Log in to it on Windows "
                      + "once first (start-vrchat-dev.ps1 -Profiles " + profile.ToString(CultureInfo.InvariantCulture) + ")";
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

        /// <summary>Like scripts/vrcrt.sh: a client list older than this is not acted on.</summary>
        internal static readonly TimeSpan SlotListFreshFor = TimeSpan.FromSeconds(15);

        private static readonly Version SlotControlVersion = new Version(0, 10, 0);

        /// <summary>Whether a slot's Close and Launch buttons may be pressed, and why not.</summary>
        internal readonly struct SlotActions
        {
            internal SlotActions(bool canClose, bool canLaunch, string blocked, string launchOff = null)
            {
                CanClose = canClose;
                CanLaunch = canLaunch;
                Blocked = blocked;
                LaunchOff = launchOff;
            }

            internal bool CanClose { get; }
            internal bool CanLaunch { get; }

            /// <summary>Why neither can be pressed now; null when one can.</summary>
            internal string Blocked { get; }

            /// <summary>Why only Launch is off for this slot (it is not one the configuration starts); else null.</summary>
            internal string LaunchOff { get; }
        }

        /// <summary>
        /// Close needs a running client, Launch one that has exited; both need Relaunch, a Bridge
        /// from v0.10.0 (an unknown version is allowed: an older one answers 404), a client list
        /// read in the last 15 s, and no other build or Bridge change. Launch is also off for a slot
        /// past the configured profiles: the Bridge starts only the slots of its last deploy and
        /// would refuse (NoLaunchPlan).
        /// </summary>
        internal static SlotActions ActionsFor(VrchatClientEntry entry, VrchatClientSettings active, string bridgeVersion,
            DateTimeOffset listUpdatedAtUtc, DateTimeOffset nowUtc, bool busy)
        {
            string blocked = null;
            if (busy)
            {
                blocked = "another build or Bridge change is running";
            }
            else if (active == null || !active.IsRelaunch)
            {
                blocked = "one client at a time needs VrchatClientMode Relaunch";
            }
            else if (Version.TryParse(RemoteTestWindow.CoreVersion(bridgeVersion) ?? string.Empty, out var version)
                     && version < SlotControlVersion)
            {
                blocked = "the Bridge is older than v0.10.0";
            }
            else if (entry == null || nowUtc - listUpdatedAtUtc > SlotListFreshFor || nowUtc < listUpdatedAtUtc - SlotListFreshFor)
            {
                blocked = "the client list is not current";
            }
            else if (entry.Running == null)
            {
                blocked = "the Bridge cannot tell now whether it runs";
            }

            if (blocked != null)
            {
                return new SlotActions(false, false, blocked);
            }

            var configured = entry.Slot >= 0 && entry.Slot < active.Profiles.Count;
            return new SlotActions(entry.Running == true, entry.Running == false && configured, null,
                configured ? null : "slot " + entry.Slot + " is not one of the " + active.Profiles.Count + " configured profiles");
        }

        /// <summary>"PID 1234 · profile 0 · joined · local:wrld_… · logged in · gen 1234 · epoch 5", sanitized.</summary>
        internal static string DescribeSlot(VrchatClientEntry entry)
        {
            if (entry == null)
            {
                return "no client";
            }

            var parts = new System.Collections.Generic.List<string>
            {
                "PID " + entry.ProcessId.ToString(CultureInfo.InvariantCulture),
                entry.Profile is int profile ? "profile " + profile.ToString(CultureInfo.InvariantCulture) : "profile ?",
                entry.Running == true ? Short(entry.State, 16) ?? "running" : entry.Running == false ? "exited" : "unknown",
            };
            if (entry.Running == true && !string.IsNullOrEmpty(entry.Instance))
            {
                parts.Add(Short(entry.Instance, 28));
            }

            if (entry.InErrorWorld == true)
            {
                parts.Add("ERROR WORLD");
            }

            if (entry.Running == true)
            {
                parts.Add(entry.LoginState == "authenticated" ? "logged in"
                    : entry.LoginState == "likelyLoggedOut" ? "LIKELY LOGGED OUT"
                    : Short(entry.LoginState, 16) ?? "login unknown");
            }

            if (entry.CatchingUp)
            {
                parts.Add("catching up");
            }

            parts.Add("gen " + entry.ClientGeneration.ToString(CultureInfo.InvariantCulture));
            if (entry.ObservationEpoch is long epoch)
            {
                parts.Add("epoch " + epoch.ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(" · ", parts);
        }

        internal static string ConfirmClose(VrchatClientEntry entry) =>
            $"Close slot {entry.Slot} ({DescribeSlot(entry)})?\n\n"
            + "The Bridge closes the client it has recorded in this slot when the request arrives. If that client "
            + "changed after this list was read, the request is not stopped: this Bridge has no check for it. "
            + "The other clients keep running.";

        internal static string ConfirmLaunch(VrchatClientEntry entry) =>
            $"Start slot {entry.Slot} again ({DescribeSlot(entry)})?\n\n"
            + "The Bridge starts it with the world, instance, profile and VR setting it used for this slot in the "
            + "last deploy. If the answer is lost, whether it started is unknown: check the list before trying again.";

        /// <summary>
        /// The confirmation for a Bridge that checks the request (slotPreconditions.v1) when
        /// <paramref name="guarded"/>; otherwise the R1a text, unchanged.
        /// </summary>
        internal static string ConfirmClose(VrchatClientEntry entry, bool guarded) =>
            !guarded
                ? ConfirmClose(entry)
                : $"Close slot {entry.Slot} ({DescribeSlot(entry)})?\n\n"
                  + "The request carries this client's generation and the Bridge's state as this list showed them. "
                  + "If the client or the Bridge changed after the list was read, the Bridge does nothing, and the list "
                  + "has to be read again. The other clients keep running.";

        /// <inheritdoc cref="ConfirmClose(VrchatClientEntry, bool)"/>
        internal static string ConfirmLaunch(VrchatClientEntry entry, bool guarded) =>
            !guarded
                ? ConfirmLaunch(entry)
                : $"Start slot {entry.Slot} again ({DescribeSlot(entry)})?\n\n"
                  + "The Bridge starts it with the world, instance, profile and VR setting it used for this slot in the "
                  + "last deploy. If the client or the Bridge changed after this list was read, the Bridge does nothing, "
                  + "and the list has to be read again. If the answer is lost, whether it started is unknown: check the "
                  + "list before trying again.";

        private static string Short(string text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var clean = BridgeMessages.Sanitize(text, 200);
            return clean.Length <= max ? clean : clean.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// A Reload Bridge does not add or remove clients while some run; null when that does
        /// not matter for this change.
        /// </summary>
        /// <param name="clientsRunning">Whether the Bridge's clients list shows a running client; null when unknown.</param>
        internal static string ReloadCountNote(VrchatClientSettings active, VrchatClientSettings desired,
            bool? clientsRunning = null) =>
            active != null && desired != null && !active.IsRelaunch && !desired.IsRelaunch && active.Count != desired.Count
            && clientsRunning != false
                ? "Running Reload clients are kept as they are. Close them (button below) so that the next deploy launches "
                  + desired.Count + "."
                : null;
    }
}

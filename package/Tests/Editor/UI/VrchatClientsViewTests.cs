using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>The text of the window's "VRChat Clients" section, without IMGUI.</summary>
    public class VrchatClientsViewTests
    {
        private static readonly BridgeProfiles Profiles = new BridgeProfiles
        {
            Profiles = new List<BridgeProfileAccount>
            {
                new BridgeProfileAccount
                {
                    Profile = 1,
                    DisplayName = "Second‮",
                    IdDigest = "0123456789abcdef",
                    AuthenticatedAtUtc = new DateTimeOffset(2026, 11, 1, 1, 2, 3, TimeSpan.Zero),
                },
            },
        };

        [Test]
        public void The_fields_describe_Reload_or_Relaunch()
        {
            Assert.AreEqual("Reload · 3 clients", VrchatClientsView.Desired(0, 3, "ignored", out var problem).Describe());
            Assert.IsNull(problem);

            Assert.AreEqual("Relaunch · profiles 0, 1, 2, 3",
                VrchatClientsView.Desired(1, 1, "0,1,2,3", out problem).Describe());
            Assert.IsNull(problem);
        }

        [Test]
        public void Profiles_that_cannot_be_sent_give_no_settings_and_a_reason()
        {
            Assert.IsNull(VrchatClientsView.Desired(1, 1, "0,0", out var problem));
            StringAssert.Contains("repeated: 0", problem);
        }

        [Test]
        public void A_profile_shows_its_last_account()
        {
            var utc = TimeZoneInfo.Utc;

            Assert.AreEqual("Second  (01234567) · seen 2026-11-01 01:02",
                VrchatClientsView.DescribeProfile(1, Profiles, utc), "the right-to-left override is removed");
        }

        [Test]
        public void A_profile_never_seen_says_how_to_log_it_in()
        {
            StringAssert.Contains("start-vrchat-dev.ps1 -Profiles 3", VrchatClientsView.DescribeProfile(3, Profiles));
            StringAssert.Contains("once the Bridge launches this profile", VrchatClientsView.DescribeProfile(3, Profiles),
                "a login made by hand is not seen until the Bridge launches the profile");
            Assert.AreEqual("account unknown", VrchatClientsView.DescribeProfile(3, null));
        }

        [Test]
        public void Only_a_mode_change_closes_the_Bridge_clients()
        {
            var relaunch2 = VrchatClientSettings.Relaunch(new[] { 0, 1 });
            var relaunch4 = VrchatClientSettings.Relaunch(new[] { 0, 1, 2, 3 });
            var reload2 = VrchatClientSettings.Reload(2);

            Assert.IsFalse(VrchatClientsView.ClosesClients(relaunch2, relaunch4));
            Assert.IsTrue(VrchatClientsView.ClosesClients(relaunch2, reload2));
            Assert.IsFalse(VrchatClientsView.ClosesClients(null, reload2));
        }

        [Test]
        public void Three_or_more_clients_want_a_longer_result_timeout()
        {
            Assert.IsTrue(VrchatClientsView.NeedsLongerTimeout(VrchatClientSettings.Relaunch(new[] { 0, 1, 2 }), 60));
            Assert.IsFalse(VrchatClientsView.NeedsLongerTimeout(VrchatClientSettings.Relaunch(new[] { 0, 1, 2 }), 180));
            Assert.IsFalse(VrchatClientsView.NeedsLongerTimeout(VrchatClientSettings.Reload(2), 60));
        }

        [Test]
        public void A_new_Reload_count_needs_the_running_clients_closed()
        {
            StringAssert.Contains("launches 4",
                VrchatClientsView.ReloadCountNote(VrchatClientSettings.Reload(2), VrchatClientSettings.Reload(4)));
            Assert.IsNull(VrchatClientsView.ReloadCountNote(VrchatClientSettings.Reload(2), VrchatClientSettings.Reload(2)));
            Assert.IsNull(VrchatClientsView.ReloadCountNote(
                VrchatClientSettings.Relaunch(new[] { 0, 1 }), VrchatClientSettings.Reload(4)),
                "a mode change closes them anyway");
        }
    }
}

using Newtonsoft.Json;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>The window's read-only Bridge section (v0.8.0), without IMGUI.</summary>
    public class BridgeSectionTests
    {
        [Test]
        public void Build_metadata_and_a_leading_v_are_not_part_of_the_version()
        {
            Assert.AreEqual("0.8.0", RemoteTestWindow.CoreVersion("0.8.0+3f2a9c1"));
            Assert.AreEqual("0.8.0", RemoteTestWindow.CoreVersion("v0.8.0"));
            Assert.AreEqual("0.8.0-rc.1", RemoteTestWindow.CoreVersion(" 0.8.0-rc.1+abc "));
            Assert.IsNull(RemoteTestWindow.CoreVersion(null));
            Assert.IsNull(RemoteTestWindow.CoreVersion("+abc"));
        }

        [Test]
        public void Versions_differ_only_when_both_are_known_and_not_equal()
        {
            Assert.IsFalse(RemoteTestWindow.VersionsDiffer("0.8.0", "0.8.0+3f2a9c1"));
            Assert.IsTrue(RemoteTestWindow.VersionsDiffer("0.8.0", "0.7.1+3f2a9c1"));
            Assert.IsFalse(RemoteTestWindow.VersionsDiffer(string.Empty, "0.7.1"), "package version unknown");
            Assert.IsFalse(RemoteTestWindow.VersionsDiffer("0.8.0", null), "Bridge before 0.8.0");
        }

        [Test]
        public void Parses_bridge_info()
        {
            const string json = "{\"version\":\"0.8.0+abc\",\"exeSha256\":\"00\",\"hostGeneration\":3,"
                + "\"hostStartedAtUtc\":\"2026-10-30T01:02:03.1234567+00:00\",\"activeConfigEtag\":\"aa\",\"savedConfigEtag\":\"bb\","
                + "\"activeConfig\":{\"LogMarkerPrefixes\":[\"[SPK003]\"]},"
                + "\"paths\":{\"vrchatWorldsDirectory\":{\"display\":\"C:\\\\Users\\\\*\\\\w\",\"exists\":true}},"
                + "\"lastConfigApply\":{\"operationId\":\"op\",\"requestedEtag\":\"bb\",\"outcome\":\"rolledBack\","
                + "\"problems\":[\"VrchatClientCount must be 1 to 4\"],\"atUtc\":\"2026-10-30T01:02:04Z\"},"
                + "\"busy\":null,\"restartPending\":false,"
                + "\"configNotes\":[\"AutoLaunchVrchat is false: a deploy installs the world but launches no VRChat.\"],"
                + "\"stateExportsBytes\":0,\"freeDiskBytes\":-1}";

            var info = BridgeMessages.Parse<BridgeInfo>(json);

            Assert.AreEqual("0.8.0+abc", info.Version);
            Assert.AreEqual(3, info.HostGeneration);
            Assert.AreEqual("rolledBack", info.LastConfigApply.Outcome);
            CollectionAssert.AreEqual(new[] { "VrchatClientCount must be 1 to 4" }, info.LastConfigApply.Problems);
            Assert.AreEqual(1, info.ConfigNotes.Count);
            Assert.IsNull(info.Busy);
        }

        [Test]
        public void Describes_a_clients_login_and_account()
        {
            var client = JsonConvert.DeserializeObject<VrchatClientEntry>(
                "{\"slot\":1,\"profile\":2,\"loginState\":\"authenticated\","
                + "\"account\":{\"displayName\":\"Tester\\u001b[31m\",\"idDigest\":\"0123456789ab\","
                + "\"authenticatedAtUtc\":\"2026-10-30T01:02:03Z\"}}");

            Assert.AreEqual("profile 2 · authenticated · Tester [31m (01234567)", ClientLogView.DescribeLogin(client));
        }

        [Test]
        public void A_client_without_an_account_shows_its_login_state_alone()
        {
            Assert.AreEqual("likelyLoggedOut",
                ClientLogView.DescribeLogin(new VrchatClientEntry { LoginState = "likelyLoggedOut" }));
        }

        [Test]
        public void A_client_from_a_Bridge_before_0_8_has_no_login_line()
        {
            var client = JsonConvert.DeserializeObject<VrchatClientEntry>("{\"slot\":0,\"authenticated\":true}");

            Assert.IsNull(client.LoginState);
            Assert.IsNull(client.Account);
            Assert.IsNull(ClientLogView.DescribeLogin(client));
        }
    }
}

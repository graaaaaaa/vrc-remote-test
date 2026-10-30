using System.Text;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>The Bridge's error bodies (v0.8.0) as the upload and the management calls read them, without HTTP.</summary>
    public class BridgeMessagesTests
    {
        private const string ArtifactTooLarge =
            "{\"error\":\"artifactTooLarge\",\"limitBytes\":52428800,\"declaredBytes\":50017280}";

        [Test]
        public void A_413_with_a_body_names_the_limit_and_the_setting()
        {
            var message = BridgeMessages.TooLargeUploadMessage(413, ArtifactTooLarge);

            Assert.AreEqual(
                "Bridge rejected artifact upload (HTTP 413): the Bridge accepts at most 52428800 bytes (50.0 MiB) "
                + "(MaxArtifactSizeBytes); this upload was 50017280 bytes (47.7 MiB).",
                message);
        }

        [Test]
        public void A_413_without_a_declared_size_still_names_the_limit()
        {
            var message = BridgeMessages.TooLargeUploadMessage(413,
                "{\"error\":\"artifactTooLarge\",\"limitBytes\":1048576,\"declaredBytes\":null}");

            StringAssert.Contains("at most 1048576 bytes (1.0 MiB) (MaxArtifactSizeBytes)", message);
            StringAssert.DoesNotContain("this upload was", message);
        }

        [Test]
        public void Another_limit_does_not_name_MaxArtifactSizeBytes()
        {
            var text = BridgeMessages.DescribeTooLarge("{\"error\":\"manifestTooLarge\",\"limitBytes\":65536,\"declaredBytes\":70000}");

            StringAssert.StartsWith("the Bridge accepts at most 65536 bytes", text);
            StringAssert.DoesNotContain("MaxArtifactSizeBytes", text);
        }

        [Test]
        public void Without_a_usable_body_the_caller_keeps_its_own_message()
        {
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413, null), "a Bridge before 0.8.0 sent no body");
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413, string.Empty));
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413, "Payload Too Large"));
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413, "{\"error\":\"artifactTooLarge\",\"limitBytes\":\"big\"}"));
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413, "[1,2]"));
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(500, ArtifactTooLarge), "only a 413 is about size");
        }

        [Test]
        public void Decode_reads_at_most_4_KiB()
        {
            Assert.IsNull(BridgeMessages.Decode(null));
            Assert.IsNull(BridgeMessages.Decode(new byte[0]));
            Assert.AreEqual(ArtifactTooLarge, BridgeMessages.Decode(Encoding.UTF8.GetBytes(ArtifactTooLarge)));
            Assert.AreEqual(BridgeMessages.MaxErrorBodyBytes, BridgeMessages.Decode(new byte[10000]).Length);
        }

        [Test]
        public void Reads_the_error_member_of_a_management_answer()
        {
            var busy = new BridgeAdminResponse(true, 409, "{\"error\":\"busy\",\"operation\":\"deploy\"}", null);

            Assert.AreEqual("busy", busy.Error);
            Assert.IsFalse(busy.IsSuccess);
            Assert.IsNull(new BridgeAdminResponse(true, 404, null, null).Error);
            Assert.IsNull(new BridgeAdminResponse(true, 200, "line one\nline two\n", null).Error, "GET /logs/bridge is text");
        }

        [Test]
        public void A_transport_without_the_management_API_says_so()
        {
            var response = BridgeAdminResponse.NotSupported("FakeRemoteTransport");

            Assert.IsFalse(response.Supported);
            Assert.IsFalse(response.IsSuccess);
            Assert.AreEqual(0, response.StatusCode);
            StringAssert.Contains("Bridge v0.8.0", response.Message);
        }

        [Test]
        public void Parses_a_successful_answer_and_ignores_a_malformed_one()
        {
            var ok = new BridgeAdminResponse(true, 200, "{\"version\":\"0.8.0\",\"hostGeneration\":2}", null);
            var broken = new BridgeAdminResponse(true, 200, "{\"version\":", null);

            Assert.IsTrue(ok.IsSuccess);
            Assert.AreEqual(2, ok.As<BridgeInfo>().HostGeneration);
            Assert.IsNull(broken.As<BridgeInfo>());
        }

        [Test]
        public void Sanitize_replaces_controls_and_cuts_long_text()
        {
            Assert.AreEqual("a b c", BridgeMessages.Sanitize("a\tb\nc", 10));
            Assert.AreEqual("abc...", BridgeMessages.Sanitize("abcdef", 3));
            Assert.AreEqual("abc", BridgeMessages.Sanitize("abc", 3));
        }
    }
}

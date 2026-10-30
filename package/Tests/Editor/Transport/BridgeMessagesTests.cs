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
        public void BoundedBytes_keeps_at_most_its_capacity_and_does_not_read_a_cut_body()
        {
            var empty = new BoundedBytes(BridgeMessages.MaxErrorBodyBytes);
            empty.Append(null, 0, 10);
            Assert.IsNull(empty.Text);

            var small = new BoundedBytes(BridgeMessages.MaxErrorBodyBytes);
            var bytes = Encoding.UTF8.GetBytes(ArtifactTooLarge);
            small.Append(bytes, 0, 10);
            small.Append(bytes, 10, bytes.Length - 10);
            Assert.AreEqual(ArtifactTooLarge, small.Text);
            Assert.IsFalse(small.Overflowed);

            // What the upload's download handler and ReadBoundedAsync keep of a long answer:
            // never more than the capacity, and nothing to parse once it overflowed.
            var large = new BoundedBytes(BridgeMessages.MaxErrorBodyBytes);
            for (var i = 0; i < 100; i++)
            {
                large.Append(new byte[1000], 0, 1000);
            }

            Assert.AreEqual(BridgeMessages.MaxErrorBodyBytes, large.Count);
            Assert.IsTrue(large.Overflowed);
            Assert.IsNull(large.Text);
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413, large.Text));
        }

        [Test]
        public void Byte_counts_outside_Int64_or_below_zero_are_unknown()
        {
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413,
                "{\"error\":\"artifactTooLarge\",\"limitBytes\":9223372036854775808}"), "above Int64.MaxValue");
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413,
                "{\"error\":\"artifactTooLarge\",\"limitBytes\":-1}"));
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413,
                "{\"error\":\"artifactTooLarge\",\"limitBytes\":1.5}"));
            Assert.IsNull(BridgeMessages.TooLargeUploadMessage(413,
                "{\"error\":\"artifactTooLarge\",\"limitBytes\":true}"));

            var message = BridgeMessages.TooLargeUploadMessage(413,
                "{\"error\":\"artifactTooLarge\",\"limitBytes\":9223372036854775807,\"declaredBytes\":99999999999999999999}");
            StringAssert.Contains("at most 9223372036854775807 bytes", message);
            StringAssert.DoesNotContain("this upload was", message, "an out-of-range declared size is left out");

            Assert.AreEqual(0L, BridgeMessages.ByteCount(new Newtonsoft.Json.Linq.JValue(0L)));
            Assert.IsNull(BridgeMessages.ByteCount(null));
            Assert.IsNull(BridgeMessages.ByteCount(new Newtonsoft.Json.Linq.JValue(-5L)));
            Assert.IsNull(BridgeMessages.ByteCount(new Newtonsoft.Json.Linq.JValue(ulong.MaxValue)));
        }

        [Test]
        public void A_management_answer_with_out_of_range_numbers_is_not_a_crash()
        {
            var huge = new BridgeAdminResponse(true, 200, "{\"version\":\"0.8.0\",\"hostGeneration\":99999999999999999999}", null);

            Assert.IsNull(huge.As<BridgeInfo>());
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
        public void A_commit_refused_while_the_Bridge_restarts_says_to_deploy_again()
        {
            var message = BridgeMessages.CommitRejectedMessage(409, "{\"error\":\"busy\",\"operation\":\"restart\"}");

            Assert.AreEqual(
                "Bridge rejected build commit (HTTP 409): the Bridge is restarting. "
                + "Deploy again once it is back (Deploy Last Build).",
                message);
        }

        [Test]
        public void A_commit_refused_by_another_operation_names_it()
        {
            StringAssert.Contains("the Bridge is busy (config)",
                BridgeMessages.CommitRejectedMessage(409, "{\"error\":\"busy\",\"operation\":\"config\"}"));
            StringAssert.Contains("the Bridge is busy.",
                BridgeMessages.CommitRejectedMessage(409, "{\"error\":\"busy\",\"operation\":null}"));
        }

        [Test]
        public void Any_other_refused_commit_keeps_its_body_cut_and_cleaned()
        {
            Assert.AreEqual("Bridge rejected build commit (HTTP 400): Invalid manifest.",
                BridgeMessages.CommitRejectedMessage(400, "Invalid manifest."));
            Assert.AreEqual("Bridge rejected build commit (HTTP 409).",
                BridgeMessages.CommitRejectedMessage(409, null), "no body, or one over the read limit");
            Assert.AreEqual("Bridge rejected build commit (HTTP 400): a b",
                BridgeMessages.CommitRejectedMessage(400, "a" + (char)0x1b + "b"));
            StringAssert.EndsWith("...", BridgeMessages.CommitRejectedMessage(400, new string('x', 600)));
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

            // Built from character codes: a raw U+2028 ends a C# string literal, and the others are
            // invisible in review.
            var tricky = "a" + (char)0x202E + "b" + (char)0x2066 + "c" + (char)0x200F + "d" + (char)0x85 + "e"
                + (char)0x2028 + "f";
            Assert.AreEqual("a b c d e f", BridgeMessages.Sanitize(tricky, 20),
                "bidi overrides and isolates, marks, C1 controls and separators");
            Assert.AreEqual("x y", BridgeMessages.Sanitize("x" + (char)0x200B + "y", 10), "zero-width space is a format character");
            Assert.AreEqual("abc...", BridgeMessages.Sanitize("abcdef", 3));
            Assert.AreEqual("abc", BridgeMessages.Sanitize("abc", 3));
        }
    }
}

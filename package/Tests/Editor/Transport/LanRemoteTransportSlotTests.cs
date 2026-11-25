using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>
    /// What LanRemoteTransport puts on the wire for one client's close or launch, through a
    /// handler that records the request instead of a network (docs/plans/r1b-contract.md §1.3).
    /// </summary>
    public class LanRemoteTransportSlotTests
    {
        // Made-up values of the right shape; not a real pairing.
        private const string Url = "https://192.0.2.10:8443/";
        private static readonly string Fingerprint = new string('a', 64);
        private static readonly string Token = new string('b', 64);

        private sealed class Recorded
        {
            public HttpMethod Method;
            public Uri Uri;
            public byte[] Body;
            public string ContentType;
            public string Charset;
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public readonly List<Recorded> Requests = new List<Recorded>();
            public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var recorded = new Recorded { Method = request.Method, Uri = request.RequestUri };
                if (request.Content != null)
                {
                    recorded.Body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    recorded.ContentType = request.Content.Headers.ContentType?.MediaType;
                    recorded.Charset = request.Content.Headers.ContentType?.CharSet;
                }

                lock (Requests)
                {
                    Requests.Add(recorded);
                }

                return Respond(request);
            }
        }

        private static SlotPrecondition Sample() =>
            new SlotPrecondition("0123456789abcdef0123456789abcdef", 3, "a1", null, 17, "b-1", 1234);

        [Test]
        public void Without_a_precondition_close_and_launch_send_no_body()
        {
            var handler = new RecordingHandler();
            using (var transport = new LanRemoteTransport(Url, Fingerprint, Token, handler))
            {
                transport.CloseClientAsync(1).GetAwaiter().GetResult();
                transport.LaunchClientAsync(2).GetAwaiter().GetResult();
                transport.CloseClientAsync(3, null, null).GetAwaiter().GetResult();
                transport.LaunchClientAsync(0, null, null).GetAwaiter().GetResult();
            }

            Assert.AreEqual(4, handler.Requests.Count);
            var paths = new[] { "/api/v1/clients/1/close", "/api/v1/clients/2/launch", "/api/v1/clients/3/close", "/api/v1/clients/0/launch" };
            for (var i = 0; i < paths.Length; i++)
            {
                Assert.AreEqual(HttpMethod.Post, handler.Requests[i].Method);
                Assert.AreEqual(paths[i], handler.Requests[i].Uri.AbsolutePath);
                Assert.IsNull(handler.Requests[i].Body, "no content at all, as before v0.12.0");
            }
        }

        [Test]
        public void With_a_precondition_the_exact_body_goes_as_json()
        {
            var handler = new RecordingHandler();
            using (var transport = new LanRemoteTransport(Url, Fingerprint, Token, handler))
            {
                transport.CloseClientAsync(1, Sample(), "run-1_close").GetAwaiter().GetResult();
                transport.LaunchClientAsync(1, Sample(), null).GetAwaiter().GetResult();
            }

            var close = handler.Requests[0];
            Assert.AreEqual("/api/v1/clients/1/close", close.Uri.AbsolutePath);
            Assert.AreEqual("application/json", close.ContentType);
            Assert.AreEqual("utf-8", close.Charset);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(
                "{\"expect\":{\"bridgeInstanceId\":\"0123456789abcdef0123456789abcdef\",\"hostGeneration\":3,"
                + "\"activeConfigEtag\":\"a1\",\"savedConfigEtag\":null,\"operationRevision\":17,\"buildId\":\"b-1\","
                + "\"clientGeneration\":1234},\"operationId\":\"run-1_close\"}"), close.Body);

            var launch = handler.Requests[1];
            Assert.AreEqual("/api/v1/clients/1/launch", launch.Uri.AbsolutePath);
            Assert.AreEqual("application/json", launch.ContentType);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Sample().ToRequestJson()), launch.Body);
        }

        [Test]
        public void An_operation_id_alone_or_an_invalid_one_is_not_sent()
        {
            var handler = new RecordingHandler();
            using (var transport = new LanRemoteTransport(Url, Fingerprint, Token, handler))
            {
                Assert.Throws<ArgumentException>(() => transport.CloseClientAsync(1, null, "op").GetAwaiter().GetResult());
                Assert.Throws<ArgumentException>(() => transport.LaunchClientAsync(1, Sample(), "a b").GetAwaiter().GetResult());
            }

            Assert.IsEmpty(handler.Requests);
        }

        [Test]
        public void A_412_comes_back_as_an_answer()
        {
            var handler = new RecordingHandler
            {
                Respond = _ => new HttpResponseMessage((HttpStatusCode)412)
                {
                    Content = new StringContent("{\"error\":\"preconditionFailed\",\"mismatched\":[\"clientGeneration\"]}"),
                },
            };
            using (var transport = new LanRemoteTransport(Url, Fingerprint, Token, handler))
            {
                var response = transport.CloseClientAsync(1, Sample(), null).GetAwaiter().GetResult();
                Assert.AreEqual(412, response.StatusCode);
                Assert.AreEqual("preconditionFailed", response.Error);
                Assert.IsFalse(SlotOperationReport.ReadClose(1, response).Applied);
            }

            Assert.AreEqual(1, handler.Requests.Count, "not retried");
        }

        [Test]
        public void Capabilities_report_the_feature_and_an_unreadable_answer_is_null()
        {
            var handler = new RecordingHandler
            {
                Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"apiVersion\":1,\"buildProtocolVersion\":1,\"maxClients\":4,"
                                                + "\"features\":[\"slotPreconditions.v1\"]}"),
                },
            };
            using (var transport = new LanRemoteTransport(Url, Fingerprint, Token, handler))
            {
                var capabilities = transport.GetCapabilitiesAsync().GetAwaiter().GetResult();
                Assert.IsTrue(capabilities.SupportsSlotPreconditions);
                Assert.AreEqual(4, capabilities.MaxClients);
                Assert.AreEqual("/api/v1/capabilities", handler.Requests[0].Uri.AbsolutePath);

                handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
                Assert.IsNull(transport.GetCapabilitiesAsync().GetAwaiter().GetResult());

                handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"apiVersion\":1}") };
                Assert.IsFalse(transport.GetCapabilitiesAsync().GetAwaiter().GetResult().SupportsSlotPreconditions);
            }
        }

        [Test]
        public void The_polled_bridge_answer_says_when_it_was_asked_for()
        {
            var handler = new RecordingHandler
            {
                Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"version\":\"0.12.0\",\"hostGeneration\":1,"
                                                + "\"bridgeInstanceId\":\"0123456789abcdef0123456789abcdef\",\"operationRevision\":4}"),
                },
            };
            var before = DateTimeOffset.UtcNow;
            using (var transport = new LanRemoteTransport(Url, Fingerprint, Token, handler))
            {
                // The first poll starts the read; this handler may answer before it returns.
                var info = transport.PollBridgeInfo();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (info == null && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(20);
                    info = transport.PollBridgeInfo();
                }

                Assert.IsNotNull(info);
                Assert.AreEqual(4, info.OperationRevision);
                Assert.IsNotNull(info.ReadAtUtc);
                Assert.That(info.ReadAtUtc.Value, Is.InRange(before.AddSeconds(-1), DateTimeOffset.UtcNow));
            }
        }
    }
}

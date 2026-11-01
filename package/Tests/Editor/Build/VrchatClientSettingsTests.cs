using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>
    /// The VRChat client settings the window writes into the Bridge's config.json, and the
    /// coordinator's PUT /config round trip against a fake Bridge. Blocking on the tasks, as
    /// RemoteBuildCoordinatorTests does.
    /// </summary>
    public class VrchatClientSettingsTests
    {
        // --- parsing and the patch ---

        [Test]
        public void Profiles_parse_with_commas_or_spaces_in_the_order_given()
        {
            Assert.IsTrue(VrchatClientSettings.TryParseProfiles("0, 1,2  3", out var profiles, out var problem), problem);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, profiles);

            Assert.IsTrue(VrchatClientSettings.TryParseProfiles("3 1", out profiles, out _));
            CollectionAssert.AreEqual(new[] { 3, 1 }, profiles);
        }

        [TestCase("", "List 1 to 4")]
        [TestCase("0,1,2,3,4", "List 1 to 4")]
        [TestCase("0,100", "0 to 99")]
        [TestCase("1,1", "repeated: 1")]
        [TestCase("0,-1", "is not a profile number")]
        [TestCase("0,1e2", "is not a profile number")]
        [TestCase("0,x", "is not a profile number")]
        public void Profiles_outside_the_Bridge_rules_are_refused(string text, string expected)
        {
            Assert.IsFalse(VrchatClientSettings.TryParseProfiles(text, out var profiles, out var problem));
            Assert.IsNull(profiles);
            StringAssert.Contains(expected, problem);
        }

        [Test]
        public void A_Relaunch_patch_sets_the_profiles_and_removes_the_count()
        {
            var patch = JObject.Parse(VrchatClientSettings.Relaunch(new[] { 0, 1, 2, 3 }).ToMergePatch());

            Assert.AreEqual("Relaunch", (string)patch["VrchatClientMode"]);
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, patch["VrchatProfiles"].ToObject<int[]>());
            Assert.AreEqual(JTokenType.Null, patch["VrchatClientCount"].Type);
            Assert.AreEqual(3, patch.Count, "only the three client settings");
        }

        [Test]
        public void A_Reload_patch_sets_the_count_and_removes_the_profiles()
        {
            var patch = JObject.Parse(VrchatClientSettings.Reload(2).ToMergePatch());

            Assert.AreEqual("Reload", (string)patch["VrchatClientMode"]);
            Assert.AreEqual(2, (int)patch["VrchatClientCount"]);
            Assert.AreEqual(JTokenType.Null, patch["VrchatProfiles"].Type);
        }

        [Test]
        public void Settings_outside_the_limits_cannot_be_made()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => VrchatClientSettings.Reload(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => VrchatClientSettings.Reload(5));
            Assert.Throws<ArgumentException>(() => VrchatClientSettings.Relaunch(new[] { 0, 0 }));
        }

        [Test]
        public void Reads_the_active_configuration_of_a_Bridge()
        {
            var relaunch = VrchatClientSettings.FromActiveConfig(JObject.Parse(
                "{\"VrchatClientMode\":\"relaunch\",\"VrchatProfiles\":[0,1],\"VrchatClientCount\":null}"));
            Assert.AreEqual("Relaunch · profiles 0, 1", relaunch.Describe());

            var reload = VrchatClientSettings.FromActiveConfig(JObject.Parse(
                "{\"VrchatClientMode\":\"Reload\",\"VrchatProfiles\":null,\"VrchatClientCount\":null}"));
            Assert.AreEqual("Reload · 1 client", reload.Describe(), "an omitted count is one client, as on the Bridge");

            Assert.AreEqual("Reload · 3 clients", VrchatClientSettings.FromActiveConfig(
                JObject.Parse("{\"VrchatClientMode\":\"Reload\",\"VrchatClientCount\":3}")).Describe());
        }

        [TestCase("{}")]
        [TestCase("{\"VrchatClientMode\":5}")]
        [TestCase("{\"VrchatClientMode\":\"Other\"}")]
        [TestCase("{\"VrchatClientMode\":\"Relaunch\"}")]
        [TestCase("{\"VrchatClientMode\":\"Relaunch\",\"VrchatProfiles\":[0,0]}")]
        [TestCase("{\"VrchatClientMode\":\"Relaunch\",\"VrchatProfiles\":[0,\"1\"]}")]
        [TestCase("{\"VrchatClientMode\":\"Relaunch\",\"VrchatProfiles\":[0,1,2,3,4]}")]
        [TestCase("{\"VrchatClientMode\":\"Reload\",\"VrchatClientCount\":9}")]
        [TestCase("{\"VrchatClientMode\":\"Reload\",\"VrchatClientCount\":1.5}")]
        public void A_configuration_the_Bridge_would_not_run_reads_as_unknown(string json)
        {
            Assert.IsNull(VrchatClientSettings.FromActiveConfig(JObject.Parse(json)));
        }

        [Test]
        public void Bridge_info_carries_its_active_configuration()
        {
            var info = BridgeMessages.Parse<BridgeInfo>(
                "{\"hostGeneration\":2,\"activeConfig\":{\"VrchatClientMode\":\"Relaunch\",\"VrchatProfiles\":[0,1]}}");

            Assert.AreEqual("Relaunch · profiles 0, 1", VrchatClientSettings.FromActiveConfig(info.ActiveConfig).Describe());
        }

        // --- applying through the coordinator ---

        private const string Op = "0123456789abcdef0123456789abcdef";

        private static string Info(long generation, string operationId = null, string outcome = null, string problems = "[]") =>
            "{\"hostGeneration\":" + generation
            + ",\"activeConfig\":{\"VrchatClientMode\":\"Relaunch\",\"VrchatProfiles\":[0,1]}"
            + (operationId == null
                ? ",\"lastConfigApply\":null"
                : ",\"lastConfigApply\":{\"operationId\":\"" + operationId + "\",\"outcome\":\"" + outcome
                  + "\",\"problems\":" + problems + "}")
            + ",\"configNotes\":[\"Note from the Bridge\"]}";

        private static (RemoteBuildCoordinator Coordinator, FakeRemoteTransport Transport, List<TimeSpan> Delays) Bridge()
        {
            var transport = new FakeRemoteTransport
            {
                ConfigResponse = FakeRemoteTransport.Json(200,
                    "{\"etag\":\"abc123\",\"values\":{},\"active\":{\"VrchatClientMode\":\"Relaunch\",\"VrchatProfiles\":[0,1]}}"),
                PutConfigResponse = FakeRemoteTransport.Json(202,
                    "{\"operationId\":\"" + Op + "\",\"requestedEtag\":\"def\",\"restart\":\"scheduled\"}"),
            };
            var delays = new List<TimeSpan>();
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport)
            {
                // Short real deadlines; a poll only yields, so a test that never sees its
                // outcome ends after ClientSettingsApplyTimeout.
                ClientSettingsReadTimeout = TimeSpan.FromMilliseconds(200),
                ClientSettingsWriteTimeout = TimeSpan.FromMilliseconds(200),
                ClientSettingsApplyTimeout = TimeSpan.FromMilliseconds(300),
                ClientSettingsPollRequestTimeout = TimeSpan.FromMilliseconds(50),
                Delay = async (delay, token) =>
                {
                    delays.Add(delay);
                    await Task.Delay(5, token).ConfigureAwait(false);
                },
            };
            return (coordinator, transport, delays);
        }

        [Test]
        public void Applying_writes_the_patch_with_the_etag_and_waits_for_the_outcome()
        {
            var (coordinator, transport, delays) = Bridge();
            transport.BridgeInfoResponses.Enqueue(BridgeAdminResponse.Unreachable("restarting"));
            transport.BridgeInfoResponses.Enqueue(FakeRemoteTransport.Json(200, Info(5)));
            transport.BridgeInfoResponses.Enqueue(FakeRemoteTransport.Json(200, Info(6, Op, "pending")));
            transport.BridgeInfoResponses.Enqueue(FakeRemoteTransport.Json(200, Info(6, Op, "applied")));
            var settings = VrchatClientSettings.Relaunch(new[] { 0, 1, 2, 3 });
            var steps = new List<string>();

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(settings, "Relaunch", new ImmediateProgress(steps))
                .GetAwaiter().GetResult();

            Assert.IsTrue(outcome.Applied, outcome.Message);
            StringAssert.Contains("Relaunch · profiles 0, 1, 2, 3", outcome.Message);
            CollectionAssert.AreEqual(new[] { "Note from the Bridge" }, outcome.Details);
            Assert.AreEqual("abc123", transport.LastPutEtag);
            Assert.AreEqual(settings.ToMergePatch(), transport.LastPutPatch);
            Assert.AreEqual(4, delays.Count, "four polls after the write");
            Assert.IsFalse(coordinator.IsRunning, "the lock is released");
            StringAssert.Contains("waiting for the Bridge to restart", steps[steps.Count - 1]);
        }

        [Test]
        public void The_outcome_is_taken_from_a_new_process_whose_host_generation_started_again()
        {
            var (coordinator, transport, _) = Bridge();
            transport.BridgeInfoResponses.Enqueue(FakeRemoteTransport.Json(200, Info(1, Op, "applied")));

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(1))
                .GetAwaiter().GetResult();

            Assert.IsTrue(outcome.Applied, outcome.Message);
        }

        [TestCase("rolledBack", "restored its previous config.json")]
        [TestCase("failed", "reported that the change failed")]
        [TestCase("somethingNew", "does not know (somethingNew)")]
        public void An_outcome_other_than_applied_says_what_the_Bridge_reported(string result, string expected)
        {
            var (coordinator, transport, _) = Bridge();
            transport.BridgeInfoResponses.Enqueue(FakeRemoteTransport.Json(200,
                Info(2, Op, result, "[\"The new host could not start\\u001b[31m\"]")));

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(2))
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains(expected, outcome.Message);
            StringAssert.DoesNotContain("previous settings", outcome.Message);
            CollectionAssert.AreEqual(new[] { "The new host could not start [31m" }, outcome.Details, "sanitized");
        }

        [Test]
        public void An_answer_for_another_operation_is_not_taken_as_this_one()
        {
            var (coordinator, transport, _) = Bridge();
            transport.BridgeInfoResponses.Enqueue(FakeRemoteTransport.Json(200,
                Info(2, "ffffffffffffffffffffffffffffffff", "applied")));

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(1))
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("did not report the change within", outcome.Message);
            StringAssert.Contains("may still be applied", outcome.Message);
        }

        [Test]
        public void A_Bridge_that_stops_answering_ends_the_wait_at_the_deadline_and_frees_the_lock()
        {
            var (coordinator, transport, _) = Bridge();
            transport.BridgeInfoHandler = FakeRemoteTransport.NeverAnswers;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(1))
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("did not report the change", outcome.Message);
            Assert.Less(clock.Elapsed.TotalSeconds, 5, "the 0.3 s wait, not the transport's 2 minutes per call");
            Assert.IsFalse(coordinator.IsRunning);
            Assert.Greater(transport.BridgeInfoCalls, 1, "a poll that times out is tried again");
        }

        [Test]
        public void A_change_sent_without_an_answer_may_have_been_applied()
        {
            var (coordinator, transport, delays) = Bridge();
            transport.PutConfigHandler = FakeRemoteTransport.NeverAnswers;

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(1))
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("did not answer within", outcome.Message);
            StringAssert.Contains("may have been applied", outcome.Message);
            Assert.AreEqual(0, delays.Count);
        }

        [Test]
        public void Stopping_the_wait_frees_the_lock_and_says_the_change_may_still_be_applied()
        {
            var (coordinator, transport, _) = Bridge();
            coordinator.ClientSettingsApplyTimeout = TimeSpan.FromMinutes(5);
            transport.BridgeInfoHandler = FakeRemoteTransport.NeverAnswers;
            using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(1), null, null, stop.Token)
                    .GetAwaiter().GetResult();

                Assert.IsFalse(outcome.Applied);
                StringAssert.Contains("Stopped waiting", outcome.Message);
                Assert.IsFalse(coordinator.IsRunning);
            }
        }

        [Test]
        public void Nothing_is_written_when_the_Bridge_mode_is_no_longer_the_confirmed_one()
        {
            var (coordinator, transport, _) = Bridge();

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(2), "Reload")
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("mode is now Relaunch, not Reload", outcome.Message);
            Assert.AreEqual(0, transport.PutConfigCalls);
        }

        [TestCase(409, "{\"error\":\"vrchatRunning\",\"problems\":[\"VrchatClientMode was not changed: 1 other VRChat\"],\"closed\":0}",
            "another VRChat is running")]
        [TestCase(409, "{\"error\":\"busy\",\"operation\":\"deploy\"}", "busy (deploy)")]
        [TestCase(412, "{\"error\":\"etagMismatch\",\"etag\":\"zzz\"}", "changed on Windows")]
        [TestCase(422, "{\"error\":\"invalidConfig\",\"problems\":[\"VrchatProfiles must not repeat a profile\"]}",
            "refused these settings")]
        [TestCase(404, "", "no management API")]
        public void A_refused_change_says_why_and_waits_for_nothing(int status, string body, string expected)
        {
            var (coordinator, transport, delays) = Bridge();
            transport.PutConfigResponse = FakeRemoteTransport.Json(status, body);

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Relaunch(new[] { 0, 1, 2, 3 }), "Relaunch")
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains(expected, outcome.Message);
            Assert.AreEqual(0, delays.Count);
            if (body.Contains("problems"))
            {
                Assert.AreEqual(1, outcome.Details.Count);
            }
        }

        [Test]
        public void A_Bridge_without_the_management_API_is_reported_before_anything_is_written()
        {
            var transport = new FakeRemoteTransport();
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport);

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(1))
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("Bridge v0.8.0 or later", outcome.Message);
            Assert.AreEqual(0, transport.PutConfigCalls);
        }

        [Test]
        public void An_etag_with_unsafe_characters_is_not_sent_back()
        {
            var (coordinator, transport, _) = Bridge();
            transport.ConfigResponse = FakeRemoteTransport.Json(200, "{\"etag\":\"abc\\r\\nX-Evil: 1\"}");

            var outcome = coordinator.ApplyVrchatClientSettingsAsync(VrchatClientSettings.Reload(1))
                .GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual(0, transport.PutConfigCalls);
        }

        [Test]
        public void Closing_reports_what_the_Bridge_closed()
        {
            var (coordinator, transport, _) = Bridge();
            transport.CloseResponse = FakeRemoteTransport.Json(200,
                "{\"closed\":2,\"notConfirmed\":0,\"othersRunning\":1,\"problem\":null}");

            var outcome = coordinator.CloseVrchatClientsAsync().GetAwaiter().GetResult();

            Assert.IsTrue(outcome.Applied, outcome.Message);
            StringAssert.Contains("Closed 2", outcome.Message);
            StringAssert.Contains("1 other VRChat", outcome.Message);
        }

        [Test]
        public void A_client_that_did_not_close_is_a_failure()
        {
            var (coordinator, transport, _) = Bridge();
            transport.CloseResponse = FakeRemoteTransport.Json(200,
                "{\"closed\":1,\"notConfirmed\":1,\"othersRunning\":0,\"problem\":null}");

            var outcome = coordinator.CloseVrchatClientsAsync().GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("did not confirm closing", outcome.Message);
        }

        [Test]
        public void A_problem_reported_with_a_successful_close_is_shown()
        {
            var (coordinator, transport, _) = Bridge();
            transport.CloseResponse = FakeRemoteTransport.Json(200,
                "{\"closed\":2,\"notConfirmed\":0,\"othersRunning\":0,"
                + "\"problem\":\"The Bridge could not update its record of the clients it launched.\"}");

            var outcome = coordinator.CloseVrchatClientsAsync().GetAwaiter().GetResult();

            Assert.IsTrue(outcome.Applied, outcome.Message);
            CollectionAssert.AreEqual(
                new[] { "The Bridge could not update its record of the clients it launched." }, outcome.Details);
        }

        [Test]
        public void Profiles_are_read_from_the_Bridge()
        {
            var (coordinator, transport, _) = Bridge();
            transport.ProfilesResponse = FakeRemoteTransport.Json(200,
                "{\"configuredProfiles\":[0,1],\"profiles\":[{\"profile\":1,\"displayName\":\"Second\",\"idDigest\":\"0123456789ab\","
                + "\"authenticatedAtUtc\":\"2026-11-01T01:02:03Z\",\"slot\":1,\"clientGeneration\":4}],\"warnings\":[]}");

            var profiles = coordinator.GetVrchatProfilesAsync().GetAwaiter().GetResult();

            Assert.AreEqual(1, profiles.Profiles.Count);
            Assert.AreEqual("Second", profiles.Profiles[0].DisplayName);
            CollectionAssert.AreEqual(new[] { 0, 1 }, profiles.ConfiguredProfiles);
        }

        /// <summary>Progress&lt;T&gt; posts to the synchronization context; the tests want the reports at once.</summary>
        private sealed class ImmediateProgress : IProgress<string>
        {
            private readonly List<string> _steps;

            public ImmediateProgress(List<string> steps)
            {
                _steps = steps;
            }

            public void Report(string value) => _steps.Add(value);
        }
    }
}

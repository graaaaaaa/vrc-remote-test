using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>
    /// R1b (docs/plans/r1b-contract.md §1, §4): the precondition of a close or launch, the fields
    /// the Bridge adds, reading 412/400, and whether the window sends a checked request.
    /// </summary>
    public class SlotPreconditionTests
    {
        private const string InstanceId = "0123456789abcdef0123456789abcdef";

        private static BridgeAdminResponse Json(int status, string body) => FakeRemoteTransport.Json(status, body);

        private static SlotPrecondition Sample(string active = "a1", string saved = null, string buildId = null) =>
            new SlotPrecondition(InstanceId, 3, active, saved, 17, buildId, 1234);

        // === The precondition: validation and JSON ===

        [Test]
        public void The_body_has_the_contracts_keys_in_order_with_nulls_written_out()
        {
            Assert.AreEqual(
                "{\"expect\":{\"bridgeInstanceId\":\"0123456789abcdef0123456789abcdef\",\"hostGeneration\":3,"
                + "\"activeConfigEtag\":\"a1\",\"savedConfigEtag\":null,\"operationRevision\":17,\"buildId\":null,"
                + "\"clientGeneration\":1234}}",
                Sample().ToRequestJson());

            Assert.AreEqual(
                "{\"expect\":{\"bridgeInstanceId\":\"0123456789abcdef0123456789abcdef\",\"hostGeneration\":3,"
                + "\"activeConfigEtag\":null,\"savedConfigEtag\":\"s\\\"2\",\"operationRevision\":17,"
                + "\"buildId\":\"20261126T010203-ab_C\",\"clientGeneration\":1234},\"operationId\":\"run-1_close\"}",
                Sample(active: null, saved: "s\"2", buildId: "20261126T010203-ab_C").ToRequestJson("run-1_close"));
        }

        [Test]
        public void The_largest_values_are_written_as_integers()
        {
            var json = new SlotPrecondition(InstanceId, int.MaxValue, null, null, long.MaxValue, null, long.MaxValue)
                .ToRequestJson();
            StringAssert.Contains("\"hostGeneration\":2147483647,", json);
            StringAssert.Contains("\"operationRevision\":9223372036854775807,", json);
            StringAssert.Contains("\"clientGeneration\":9223372036854775807}", json);
        }

        [TestCase("0123456789ABCDEF0123456789abcdef", 3, 17, null, 1, "bridgeInstanceId")]
        [TestCase("0123456789abcdef0123456789abcde", 3, 17, null, 1, "bridgeInstanceId")]
        [TestCase("0123456789abcdef0123456789abcdeg", 3, 17, null, 1, "bridgeInstanceId")]
        [TestCase(null, 3, 17, null, 1, "bridgeInstanceId")]
        [TestCase(InstanceId, 0, 17, null, 1, "hostGeneration")]
        [TestCase(InstanceId, 2147483648L, 17, null, 1, "hostGeneration")]
        [TestCase(InstanceId, 3, -1, null, 1, "operationRevision")]
        [TestCase(InstanceId, 3, 17, "", 1, "buildId")]
        [TestCase(InstanceId, 3, 17, "a/b", 1, "buildId")]
        [TestCase(InstanceId, 3, 17, "ab\n", 1, "buildId")]
        [TestCase(InstanceId, 3, 17, null, 0, "clientGeneration")]
        public void Values_the_Bridge_would_refuse_are_not_built(string instanceId, long host, long revision,
            string buildId, long generation, string field)
        {
            var problems = SlotPrecondition.Validate(instanceId, host, "e", "e", revision, buildId, generation);
            Assert.AreEqual(1, problems.Count, string.Join(" | ", problems));
            StringAssert.Contains(field, problems[0]);
            Assert.Throws<ArgumentException>(() =>
                new SlotPrecondition(instanceId, host, "e", "e", revision, buildId, generation));
        }

        [Test]
        public void An_etag_is_one_to_128_characters_or_null()
        {
            Assert.IsEmpty(SlotPrecondition.Validate(InstanceId, 1, null, new string('x', 128), 0, new string('b', 128), 1));
            StringAssert.Contains("activeConfigEtag",
                SlotPrecondition.Validate(InstanceId, 1, new string('x', 129), null, 0, null, 1)[0]);
            StringAssert.Contains("savedConfigEtag", SlotPrecondition.Validate(InstanceId, 1, null, "", 0, null, 1)[0]);
            StringAssert.Contains("buildId", SlotPrecondition.Validate(InstanceId, 1, null, null, 0, new string('b', 129), 1)[0]);
        }

        [TestCase("a", true)]
        [TestCase("run_20261126-close", true)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("a b", false)]
        [TestCase("é", false)]
        [TestCase("x\n", false)]
        public void Operation_ids(string id, bool valid)
        {
            Assert.AreEqual(valid, SlotPrecondition.IsValidOperationId(id));
            if (!valid && id != null)
            {
                Assert.Throws<ArgumentException>(() => Sample().ToRequestJson(id));
            }
        }

        [Test]
        public void A_64_character_operation_id_is_the_longest()
        {
            Assert.IsTrue(SlotPrecondition.IsValidOperationId(new string('a', 64)));
            Assert.IsFalse(SlotPrecondition.IsValidOperationId(new string('a', 65)));
        }

        // === GET /bridge and /capabilities ===

        private const string NewBridge =
            "{\"version\":\"0.12.0\",\"hostGeneration\":3,\"activeConfigEtag\":\"a1\",\"savedConfigEtag\":\"s1\","
            + "\"bridgeInstanceId\":\"" + InstanceId + "\",\"operationRevision\":17,"
            + "\"currentBatch\":{\"buildId\":\"20261126T010203-abc\",\"launchedSlots\":[0,1]}}";

        [Test]
        public void The_new_bridge_fields_are_read_and_are_null_from_an_older_Bridge()
        {
            var info = BridgeMessages.Parse<BridgeInfo>(NewBridge);
            Assert.AreEqual(InstanceId, info.BridgeInstanceId);
            Assert.AreEqual(17, info.OperationRevision);
            Assert.AreEqual("20261126T010203-abc", info.CurrentBatch.BuildId);
            CollectionAssert.AreEqual(new[] { 0, 1 }, info.CurrentBatch.LaunchedSlots);
            Assert.IsNull(info.ReadAtUtc, "not part of the wire format");

            var old = BridgeMessages.Parse<BridgeInfo>("{\"version\":\"0.11.0\",\"hostGeneration\":2}");
            Assert.IsNull(old.BridgeInstanceId);
            Assert.IsNull(old.OperationRevision);
            Assert.IsNull(old.CurrentBatch);

            var noPlan = BridgeMessages.Parse<BridgeInfo>(
                "{\"hostGeneration\":2,\"bridgeInstanceId\":\"" + InstanceId + "\",\"operationRevision\":0,\"currentBatch\":null}");
            Assert.IsNull(noPlan.CurrentBatch);
            Assert.AreEqual(0, noPlan.OperationRevision);
        }

        [TestCase("{\"apiVersion\":1,\"features\":[\"slotPreconditions.v1\"]}", true)]
        [TestCase("{\"apiVersion\":1,\"features\":[1,{},\"slotPreconditions.v1\"]}", true)]
        [TestCase("{\"apiVersion\":1}", false)]
        [TestCase("{\"apiVersion\":1,\"features\":\"slotPreconditions.v1\"}", false)]
        [TestCase("{\"apiVersion\":1,\"features\":{\"slotPreconditions.v1\":true}}", false)]
        [TestCase("{\"apiVersion\":1,\"features\":[\"slotPreconditions.v2\",\"SlotPreconditions.v1\"]}", false)]
        [TestCase("{\"apiVersion\":1,\"features\":null}", false)]
        public void The_feature_counts_only_as_a_string_in_a_features_array(string json, bool supported)
        {
            var capabilities = BridgeCapabilities.Parse(json);
            Assert.IsNotNull(capabilities);
            Assert.AreEqual(supported, capabilities.SupportsSlotPreconditions);
            Assert.IsNotNull(capabilities.Features);
            Assert.AreEqual(1, capabilities.ApiVersion);
        }

        [Test]
        public void Capabilities_that_are_not_an_object_are_null()
        {
            Assert.IsNull(BridgeCapabilities.Parse("[1]"));
            Assert.IsNull(BridgeCapabilities.Parse("not json"));
            Assert.IsNull(BridgeCapabilities.Parse(null));
        }

        [Test]
        public void A_precondition_is_built_from_the_row_and_bridge()
        {
            var precondition = SlotPrecondition.From(BridgeMessages.Parse<BridgeInfo>(NewBridge), 1234, out var problem);
            Assert.IsNull(problem);
            Assert.AreEqual(
                "{\"expect\":{\"bridgeInstanceId\":\"" + InstanceId + "\",\"hostGeneration\":3,\"activeConfigEtag\":\"a1\","
                + "\"savedConfigEtag\":\"s1\",\"operationRevision\":17,\"buildId\":\"20261126T010203-abc\","
                + "\"clientGeneration\":1234}}",
                precondition.ToRequestJson());
        }

        [Test]
        public void A_bridge_answer_without_the_new_fields_gives_no_precondition()
        {
            Assert.IsNull(SlotPrecondition.From(BridgeMessages.Parse<BridgeInfo>("{\"hostGeneration\":2}"), 5, out var problem));
            StringAssert.Contains("bridgeInstanceId", problem);
            StringAssert.Contains("operationRevision", problem);

            Assert.IsNull(SlotPrecondition.From(null, 5, out problem));
            StringAssert.Contains("could not be read", problem);

            var badBuild = BridgeMessages.Parse<BridgeInfo>(NewBridge.Replace("20261126T010203-abc", "x/‮"));
            Assert.IsNull(SlotPrecondition.From(badBuild, 5, out problem));
            StringAssert.Contains("buildId", problem);
            // Ordinal: Mono's culture-aware search skips format characters such as U+202E.
            Assert.IsFalse(problem.Contains("‮") || problem.Contains("x/"), "the Bridge's text is not echoed");
        }

        // === Reading the answers ===

        [Test]
        public void A_412_says_the_list_changed_and_names_what_differs()
        {
            var outcome = SlotOperationReport.ReadClose(1, Json(412,
                "{\"error\":\"preconditionFailed\",\"mismatched\":[\"operationRevision\",\"clientGeneration\",\"evil‮\"],"
                + "\"current\":{\"bridgeInstanceId\":\"" + InstanceId + "\",\"hostGeneration\":3,\"activeConfigEtag\":\"a\","
                + "\"savedConfigEtag\":\"a\",\"operationRevision\":18,\"buildId\":null,\"clientGeneration\":null},"
                + "\"operationId\":null}"));

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("changed after the client list was read", outcome.Message);
            StringAssert.Contains("operationRevision, clientGeneration", outcome.Message);
            StringAssert.Contains("Nothing was done", outcome.Message);
            StringAssert.DoesNotContain("evil", outcome.Message);

            var launch = SlotOperationReport.ReadLaunch(0, Json(412, "{\"error\":\"preconditionFailed\",\"mismatched\":\"x\"}"));
            Assert.IsFalse(launch.Applied);
            StringAssert.Contains("did not launch slot 0", launch.Message);
            StringAssert.Contains("changed after the client list was read", launch.Message);
        }

        [Test]
        public void A_412_that_is_not_preconditionFailed_is_unknown()
        {
            var outcome = SlotOperationReport.ReadClose(1, Json(412, "{\"error\":\"other\"}"));
            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("unknown", outcome.Message);
        }

        [Test]
        public void A_400_invalidPrecondition_is_not_applied_and_carries_the_problems()
        {
            var outcome = SlotOperationReport.ReadLaunch(2, Json(400,
                "{\"error\":\"invalidPrecondition\",\"problems\":[\"expect.buildId is missing.\",\"bad\u0007\"]}"));

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("invalidPrecondition", outcome.Message);
            StringAssert.Contains("Nothing was done", outcome.Message);
            Assert.AreEqual(2, outcome.Details.Count);
            Assert.AreEqual("expect.buildId is missing.", outcome.Details[0]);
            StringAssert.DoesNotContain("\u0007", outcome.Details[1]);
        }

        [Test]
        public void Todays_answers_with_the_added_fields_are_read_as_before()
        {
            const string extra = ",\"operationRevision\":18,\"bridgeInstanceId\":\"" + InstanceId + "\",\"operationId\":null";

            var closed = SlotOperationReport.ReadClose(1, Json(200,
                "{\"slot\":1,\"processId\":4321,\"closed\":1,\"notConfirmed\":0,\"recordUpdated\":true,\"problems\":[],"
                + "\"clientGeneration\":77" + extra + "}"));
            Assert.IsTrue(closed.Applied);
            StringAssert.Contains("Closed slot 1 (PID 4321)", closed.Message);

            var notConfirmed = SlotOperationReport.ReadClose(1, Json(409,
                "{\"slot\":1,\"processId\":4321,\"closed\":0,\"notConfirmed\":1,\"recordUpdated\":false,\"problems\":[\"x\"],"
                + "\"clientGeneration\":77" + extra.Replace("null", "\"op-1\"") + "}"));
            Assert.IsFalse(notConfirmed.Applied);
            StringAssert.Contains("may still be running", notConfirmed.Message);

            var launched = SlotOperationReport.ReadLaunch(0, Json(200,
                "{\"slot\":0,\"profile\":1,\"processId\":999,\"clientGeneration\":123456" + extra + "}"));
            Assert.IsTrue(launched.Applied);
            StringAssert.Contains("generation 123456", launched.Message);

            var notRecorded = SlotOperationReport.ReadLaunch(0, Json(500,
                "{\"error\":\"launchedNotRecorded\",\"slot\":0,\"launched\":true,\"recorded\":false,\"processId\":77,"
                + "\"problems\":[\"x\"]" + extra + "}"));
            Assert.IsFalse(notRecorded.Applied);
            StringAssert.Contains("Close it on Windows", notRecorded.Message);

            var busy = SlotOperationReport.ReadClose(0, Json(409, "{\"error\":\"busy\",\"operation\":\"deploy\"}"));
            StringAssert.Contains("busy", busy.Message);
        }

        // === Coordinator ===

        [Test]
        public void The_coordinator_sends_the_precondition_or_no_body()
        {
            var transport = new FakeRemoteTransport
            {
                CloseClientResponse = Json(200,
                    "{\"slot\":1,\"processId\":4,\"closed\":1,\"notConfirmed\":0,\"recordUpdated\":true,\"problems\":[]}"),
                LaunchClientResponse = Json(200, "{\"slot\":1,\"profile\":0,\"processId\":5,\"clientGeneration\":9}"),
            };
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport);

            Assert.IsTrue(coordinator.CloseVrchatClientAsync(1).GetAwaiter().GetResult().Applied);
            Assert.IsTrue(coordinator.CloseVrchatClientAsync(1, null).GetAwaiter().GetResult().Applied);
            Assert.IsTrue(coordinator.CloseVrchatClientAsync(1, Sample(), "op_1").GetAwaiter().GetResult().Applied);
            Assert.IsTrue(coordinator.LaunchVrchatClientAsync(1, Sample()).GetAwaiter().GetResult().Applied);

            CollectionAssert.AreEqual(new[] { "close 1", "close 1", "close 1", "launch 1" }, transport.SlotRequests);
            Assert.IsNull(transport.SlotBodies[0]);
            Assert.IsNull(transport.SlotBodies[1]);
            Assert.AreEqual(Sample().ToRequestJson("op_1"), transport.SlotBodies[2]);
            Assert.AreEqual(Sample().ToRequestJson(), transport.SlotBodies[3]);
        }

        [Test]
        public void An_operation_id_that_cannot_be_sent_sends_nothing()
        {
            var transport = new FakeRemoteTransport();
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport);

            Assert.IsFalse(coordinator.CloseVrchatClientAsync(1, Sample(), "a b").GetAwaiter().GetResult().Applied);
            Assert.IsFalse(coordinator.LaunchVrchatClientAsync(1, null, "op").GetAwaiter().GetResult().Applied);
            Assert.IsEmpty(transport.SlotRequests);
            Assert.IsFalse(coordinator.IsRunning);
        }

        [Test]
        public void A_412_from_the_coordinator_is_not_retried()
        {
            var transport = new FakeRemoteTransport
            {
                CloseClientResponse = Json(412, "{\"error\":\"preconditionFailed\",\"mismatched\":[\"clientGeneration\"]}"),
            };
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport);

            var outcome = coordinator.CloseVrchatClientAsync(1, Sample()).GetAwaiter().GetResult();
            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual(1, transport.SlotRequests.Count);
        }

        // === Guarded or not (the window's choice) ===

        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 26, 1, 0, 0, TimeSpan.Zero);

        private static BridgeInfo Bridge(TimeSpan age, string json = NewBridge)
        {
            var info = BridgeMessages.Parse<BridgeInfo>(json);
            info.ReadAtUtc = Now - age;
            return info;
        }

        private static BridgeCapabilities Capabilities(bool feature) =>
            BridgeCapabilities.Parse(feature
                ? "{\"apiVersion\":1,\"features\":[\"slotPreconditions.v1\"]}"
                : "{\"apiVersion\":1}");

        [Test]
        public void Without_the_feature_the_request_is_unguarded_as_in_R1a()
        {
            var decision = SlotGuard.Decide(false, false, Bridge(TimeSpan.Zero), 7, Now);
            Assert.AreEqual(SlotGuardKind.Unguarded, decision.Kind);
            Assert.IsNull(decision.Precondition);

            // Also with no /bridge at all: nothing about it is needed.
            Assert.AreEqual(SlotGuardKind.Unguarded, SlotGuard.Decide(false, false, null, 7, Now).Kind);
        }

        [Test]
        public void With_the_feature_a_fresh_bridge_and_the_row_make_the_precondition()
        {
            var decision = SlotGuard.Decide(true, true, Bridge(TimeSpan.FromSeconds(14)), 7, Now);
            Assert.AreEqual(SlotGuardKind.Guarded, decision.Kind);
            Assert.AreEqual(7, decision.Precondition.ClientGeneration);
            Assert.AreEqual(17, decision.Precondition.OperationRevision);
            Assert.AreEqual("20261126T010203-abc", decision.Precondition.BuildId);

            Assert.AreEqual(SlotGuardKind.Guarded, SlotGuard.Decide(true, false, Bridge(TimeSpan.Zero), 7, Now).Kind,
                "a Bridge that checks gets the check even when the confirmation did not promise it");
        }

        [Test]
        public void With_the_feature_an_old_or_undated_bridge_is_read_again()
        {
            Assert.AreEqual(SlotGuardKind.NeedsFreshBridge,
                SlotGuard.Decide(true, true, Bridge(TimeSpan.FromSeconds(16)), 7, Now).Kind);
            Assert.AreEqual(SlotGuardKind.NeedsFreshBridge,
                SlotGuard.Decide(true, true, Bridge(TimeSpan.FromSeconds(-16)), 7, Now).Kind, "a clock step back");
            Assert.AreEqual(SlotGuardKind.NeedsFreshBridge, SlotGuard.Decide(true, true, null, 7, Now).Kind);

            var undated = Bridge(TimeSpan.Zero);
            undated.ReadAtUtc = null;
            Assert.AreEqual(SlotGuardKind.NeedsFreshBridge, SlotGuard.Decide(true, true, undated, 7, Now).Kind);
        }

        [Test]
        public void With_the_feature_a_bridge_without_the_values_is_refused()
        {
            var decision = SlotGuard.Decide(true, true, Bridge(TimeSpan.Zero, "{\"hostGeneration\":2}"), 7, Now);
            Assert.AreEqual(SlotGuardKind.Refused, decision.Kind);
            StringAssert.Contains("bridgeInstanceId", decision.Problem);
            StringAssert.Contains("nothing was sent", decision.Problem);
        }

        [Test]
        public void A_checked_request_is_not_sent_unchecked_when_the_feature_is_gone()
        {
            var decision = SlotGuard.Decide(false, true, Bridge(TimeSpan.Zero), 7, Now);
            Assert.AreEqual(SlotGuardKind.Refused, decision.Kind);
            StringAssert.Contains("nothing was sent", decision.Problem);
        }

        private static RemoteBuildCoordinator Coordinator(FakeRemoteTransport transport) =>
            new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport) { UtcNow = () => Now };

        [Test]
        public void Resolving_reads_the_capabilities_and_uses_a_fresh_shown_bridge_as_it_is()
        {
            var transport = new FakeRemoteTransport { CapabilitiesToReturn = Capabilities(true) };
            var decision = Coordinator(transport).ResolveSlotGuardAsync(true, 7, Bridge(TimeSpan.FromSeconds(5)))
                .GetAwaiter().GetResult();

            Assert.AreEqual(SlotGuardKind.Guarded, decision.Kind);
            Assert.AreEqual(1, transport.CapabilitiesCalls);
            Assert.AreEqual(0, transport.BridgeInfoCalls, "a /bridge answer under 15 s old is used as shown");
            Assert.IsEmpty(transport.SlotRequests);
        }

        [Test]
        public void Resolving_reads_a_stale_bridge_again_first()
        {
            var transport = new FakeRemoteTransport { CapabilitiesToReturn = Capabilities(true) };
            transport.BridgeInfoResponses.Enqueue(Json(200, NewBridge.Replace("\"operationRevision\":17", "\"operationRevision\":20")));

            var decision = Coordinator(transport).ResolveSlotGuardAsync(true, 7, Bridge(TimeSpan.FromSeconds(30)))
                .GetAwaiter().GetResult();

            Assert.AreEqual(1, transport.BridgeInfoCalls);
            Assert.AreEqual(SlotGuardKind.Guarded, decision.Kind);
            Assert.AreEqual(20, decision.Precondition.OperationRevision, "the values of the new read");
        }

        [Test]
        public void Resolving_refuses_when_a_stale_bridge_cannot_be_read_again()
        {
            var transport = new FakeRemoteTransport { CapabilitiesToReturn = Capabilities(true) };
            transport.BridgeInfoResponses.Enqueue(BridgeAdminResponse.Unreachable("down"));

            var decision = Coordinator(transport).ResolveSlotGuardAsync(true, 7, Bridge(TimeSpan.FromSeconds(30)))
                .GetAwaiter().GetResult();

            Assert.AreEqual(SlotGuardKind.Refused, decision.Kind);
            StringAssert.Contains("could not be read again", decision.Problem);
        }

        [Test]
        public void Resolving_without_the_feature_or_capabilities_is_unguarded_and_reads_no_bridge()
        {
            foreach (var capabilities in new[] { Capabilities(false), null })
            {
                var transport = new FakeRemoteTransport { CapabilitiesToReturn = capabilities };
                var decision = Coordinator(transport).ResolveSlotGuardAsync(false, 7, null).GetAwaiter().GetResult();
                Assert.AreEqual(SlotGuardKind.Unguarded, decision.Kind);
                Assert.AreEqual(0, transport.BridgeInfoCalls);
            }
        }

        // === The confirmation ===

        private static VrchatClientEntry Entry() =>
            new VrchatClientEntry { Slot = 1, ProcessId = 10, Profile = 1, Running = true, State = "joined", ClientGeneration = 7 };

        [Test]
        public void The_confirmation_says_what_the_Bridge_checks_only_when_it_does()
        {
            var entry = Entry();
            Assert.AreEqual(VrchatClientsView.ConfirmClose(entry), VrchatClientsView.ConfirmClose(entry, false));
            Assert.AreEqual(VrchatClientsView.ConfirmLaunch(entry), VrchatClientsView.ConfirmLaunch(entry, false));

            foreach (var text in new List<string> { VrchatClientsView.ConfirmClose(entry, true), VrchatClientsView.ConfirmLaunch(entry, true) })
            {
                StringAssert.Contains("changed after", text);
                StringAssert.Contains("the Bridge does nothing", text);
                StringAssert.DoesNotContain("no check", text);
                StringAssert.Contains("gen 7", text);
            }
        }
    }
}

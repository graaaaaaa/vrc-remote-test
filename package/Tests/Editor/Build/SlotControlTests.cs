using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    /// <summary>Closing and launching one client from Unity: reading the Bridge's answers, the lock, and the buttons.</summary>
    public class SlotControlTests
    {
        private static BridgeAdminResponse Json(int status, string body) => FakeRemoteTransport.Json(status, body);

        // === Reading answers (the same rules as scripts/vrcrt.sh) ===

        [Test]
        public void A_close_report_with_the_record_updated_is_done()
        {
            var outcome = SlotOperationReport.ReadClose(1, Json(200,
                "{\"slot\":1,\"processId\":4321,\"closed\":1,\"notConfirmed\":0,\"recordUpdated\":true,\"problems\":[]}"));

            Assert.IsTrue(outcome.Applied);
            StringAssert.Contains("Closed slot 1 (PID 4321)", outcome.Message);
        }

        [Test]
        public void A_close_that_did_not_confirm_is_not_done()
        {
            var outcome = SlotOperationReport.ReadClose(1, Json(409,
                "{\"slot\":1,\"processId\":4321,\"closed\":0,\"notConfirmed\":1,\"recordUpdated\":false,\"problems\":[\"still running\"]}"));

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("may still be running", outcome.Message);
            Assert.AreEqual(1, outcome.Details.Count);
        }

        [TestCase("{\"slot\":2,\"processId\":4321,\"closed\":1,\"notConfirmed\":0,\"recordUpdated\":true,\"problems\":[]}")]
        [TestCase("{\"slot\":1,\"processId\":4321,\"closed\":1,\"notConfirmed\":1,\"recordUpdated\":true,\"problems\":[]}")]
        [TestCase("{\"slot\":1,\"processId\":4321,\"closed\":1,\"notConfirmed\":0,\"recordUpdated\":false,\"problems\":[]}")]
        [TestCase("{\"slot\":1,\"processId\":\"4321\",\"closed\":1,\"notConfirmed\":0,\"recordUpdated\":true,\"problems\":[]}")]
        [TestCase("{\"slot\":1,\"processId\":4321,\"closed\":1,\"notConfirmed\":0,\"recordUpdated\":true,\"problems\":[1]}")]
        [TestCase("not json")]
        public void A_200_that_is_not_a_close_report_is_unknown(string body)
        {
            var outcome = SlotOperationReport.ReadClose(1, Json(200, body));

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("unknown", outcome.Message);
        }

        [Test]
        public void No_answer_leaves_it_unknown_for_both()
        {
            var none = BridgeAdminResponse.Unreachable("timed out");
            StringAssert.Contains("unknown", SlotOperationReport.ReadClose(0, none).Message);
            StringAssert.Contains("before launching again", SlotOperationReport.ReadLaunch(0, none).Message);
        }

        [Test]
        public void Refusals_old_Bridges_and_busy_are_explained()
        {
            var refused = SlotOperationReport.ReadLaunch(0, Json(409, "{\"error\":\"noLaunchPlan\",\"problems\":[\"deploy first\"]}"));
            StringAssert.Contains("noLaunchPlan", refused.Message);
            Assert.AreEqual("deploy first", refused.Details[0]);

            StringAssert.Contains("older than v0.10.0", SlotOperationReport.ReadClose(0, Json(404, "")).Message);
            StringAssert.Contains("busy", SlotOperationReport.ReadClose(0, Json(409, "{\"error\":\"busy\",\"operation\":\"deploy\"}")).Message);
            StringAssert.Contains("not one it sends", SlotOperationReport.ReadClose(0, Json(418, "{}")).Message);
        }

        [TestCase("{\"error\":{},\"problems\":[]}")]
        [TestCase("{\"error\":[\"launchedNotRecorded\"],\"problems\":[]}")]
        public void An_error_member_that_is_not_a_string_is_unknown_not_an_exception(string body)
        {
            VrchatClientSettingsOutcome outcome = null;
            Assert.DoesNotThrow(() => outcome = SlotOperationReport.ReadLaunch(0, Json(500, body)));
            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("unknown", outcome.Message);
        }

        [Test]
        public void A_launch_report_names_the_new_generation()
        {
            var outcome = SlotOperationReport.ReadLaunch(0, Json(200,
                "{\"slot\":0,\"profile\":1,\"processId\":999,\"clientGeneration\":123456}"));

            Assert.IsTrue(outcome.Applied);
            StringAssert.Contains("generation 123456", outcome.Message);
            Assert.IsFalse(SlotOperationReport.ReadLaunch(0, Json(200, "{\"slot\":0,\"profile\":1,\"processId\":999}")).Applied);
        }

        [Test]
        public void Started_but_not_recorded_says_to_close_it_on_Windows()
        {
            var outcome = SlotOperationReport.ReadLaunch(0, Json(500,
                "{\"error\":\"launchedNotRecorded\",\"slot\":0,\"launched\":true,\"recorded\":false,\"processId\":77,\"problems\":[\"x\"]}"));

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("Close it on Windows", outcome.Message);
            StringAssert.Contains("PID 77", outcome.Message);
        }

        // === Coordinator ===

        [Test]
        public void Close_and_launch_go_to_the_slot_and_are_refused_while_a_build_runs()
        {
            var artifact = Path.Combine(Path.GetTempPath(), "vrcrt-slot-" + Guid.NewGuid() + ".vrcw");
            File.WriteAllText(artifact, "x");
            try
            {
                var hold = new TaskCompletionSource<string>();
                var transport = new FakeRemoteTransport
                {
                    LaunchClientResponse = Json(200, "{\"slot\":2,\"profile\":0,\"processId\":5,\"clientGeneration\":9}"),
                };
                var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter { BuildTaskOverride = hold.Task }, transport);

                Assert.IsTrue(coordinator.LaunchVrchatClientAsync(2).GetAwaiter().GetResult().Applied);
                CollectionAssert.AreEqual(new[] { "launch 2" }, transport.SlotRequests);

                var build = coordinator.ExecuteRemoteBuildAsync();
                var busy = coordinator.CloseVrchatClientAsync(1).GetAwaiter().GetResult();
                StringAssert.Contains("in progress", busy.Message);
                Assert.AreEqual(1, transport.SlotRequests.Count, "nothing sent while a build runs");

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error,
                    new System.Text.RegularExpressions.Regex("SDK_BUILD_FAILED"));
                hold.SetException(new RemoteBuildException(ErrorCode.SdkBuildFailed, "stop"));
                build.GetAwaiter().GetResult();
            }
            finally
            {
                File.Delete(artifact);
            }
        }

        [Test]
        public void A_slot_out_of_range_is_refused_without_a_request()
        {
            var transport = new FakeRemoteTransport();
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport);

            Assert.IsFalse(coordinator.CloseVrchatClientAsync(4).GetAwaiter().GetResult().Applied);
            Assert.IsFalse(coordinator.LaunchVrchatClientAsync(-1).GetAwaiter().GetResult().Applied);
            Assert.IsEmpty(transport.SlotRequests);
        }

        [Test]
        public void A_Bridge_that_never_answers_leaves_it_unknown_and_frees_the_lock()
        {
            var transport = new FakeRemoteTransport { SlotHandler = FakeRemoteTransport.NeverAnswers };
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), transport)
            {
                SlotLaunchTimeout = TimeSpan.FromMilliseconds(50),
            };

            var outcome = coordinator.LaunchVrchatClientAsync(0).GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Applied);
            StringAssert.Contains("unknown", outcome.Message);
            Assert.IsFalse(coordinator.IsRunning);
        }

        // === Buttons ===

        private static readonly VrchatClientSettings Relaunch = VrchatClientSettings.Relaunch(new[] { 0, 1 });

        private static VrchatClientEntry Entry(bool? running) =>
            new VrchatClientEntry { Slot = 1, ProcessId = 10, Profile = 1, Running = running, State = "joined", ClientGeneration = 7 };

        [Test]
        public void Close_is_for_a_running_client_and_Launch_for_an_exited_one()
        {
            var now = DateTimeOffset.UtcNow;
            var running = VrchatClientsView.ActionsFor(Entry(true), Relaunch, "0.11.0", now, now, busy: false);
            Assert.IsTrue(running.CanClose);
            Assert.IsFalse(running.CanLaunch);

            var exited = VrchatClientsView.ActionsFor(Entry(false), Relaunch, "0.11.0", now, now, busy: false);
            Assert.IsFalse(exited.CanClose);
            Assert.IsTrue(exited.CanLaunch);
            Assert.IsNull(exited.LaunchOff);
        }

        [Test]
        public void Launch_is_off_for_a_slot_past_the_configured_profiles()
        {
            var now = DateTimeOffset.UtcNow;
            var leftOver = new VrchatClientEntry { Slot = 2, ProcessId = 11, Running = false, State = "left", ClientGeneration = 8 };
            var actions = VrchatClientsView.ActionsFor(leftOver, Relaunch, "0.11.0", now, now, busy: false);
            Assert.IsFalse(actions.CanLaunch);
            Assert.IsFalse(actions.CanClose);
            Assert.IsNull(actions.Blocked, "only this slot's Launch is off, not the whole table");
            StringAssert.Contains("slot 2", actions.LaunchOff);

            var running = new VrchatClientEntry { Slot = 2, ProcessId = 11, Running = true, State = "joined", ClientGeneration = 8 };
            Assert.IsTrue(VrchatClientsView.ActionsFor(running, Relaunch, "0.11.0", now, now, busy: false).CanClose,
                "a running client of such a slot can still be closed");
        }

        [Test]
        public void Both_are_off_without_Relaunch_a_current_list_a_new_enough_Bridge_or_while_busy()
        {
            var now = DateTimeOffset.UtcNow;
            Assert.IsNotNull(VrchatClientsView.ActionsFor(Entry(true), VrchatClientSettings.Reload(2), "0.11.0", now, now, false).Blocked);
            Assert.IsNotNull(VrchatClientsView.ActionsFor(Entry(true), Relaunch, "0.11.0", now - TimeSpan.FromSeconds(30), now, false).Blocked);
            Assert.IsNotNull(VrchatClientsView.ActionsFor(Entry(true), Relaunch, "0.9.2", now, now, false).Blocked);
            Assert.IsNotNull(VrchatClientsView.ActionsFor(Entry(true), Relaunch, "0.11.0", now, now, true).Blocked);
            Assert.IsNotNull(VrchatClientsView.ActionsFor(Entry(null), Relaunch, "0.11.0", now, now, false).Blocked);
            Assert.IsNull(VrchatClientsView.ActionsFor(Entry(true), Relaunch, null, now, now, false).Blocked,
                "an unknown version is allowed; an older Bridge answers 404");
        }

        [Test]
        public void The_row_and_the_confirmation_show_what_will_be_acted_on()
        {
            var entry = Entry(true);
            entry.InErrorWorld = true;
            entry.LoginState = "likelyLoggedOut";
            entry.Instance = "local:" + new string('a', 100) + "\u0007";

            var row = VrchatClientsView.DescribeSlot(entry);
            StringAssert.Contains("PID 10", row);
            StringAssert.Contains("ERROR WORLD", row);
            StringAssert.Contains("LIKELY LOGGED OUT", row);
            StringAssert.DoesNotContain("\u0007", row);
            StringAssert.Contains("gen 7", row);
            StringAssert.Contains("no check", VrchatClientsView.ConfirmClose(entry));
        }
    }
}

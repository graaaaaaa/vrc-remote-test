using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class DeployResultClassifierTests
    {
        private const string BuildId = "20261125T101500123Z-0a1b2c3d";
        private const string Sha = "8f2a5f6802f9dc5307740870312b2df9a99104960df3a9df66166d54665d4d7c";
        private const string OtherSha = "0000000000000000000000000000000000000000000000000000000000000000";

        private static BuildResult Result(string status, LaunchReport launch, string sha256 = Sha, string buildId = BuildId) =>
            new BuildResult
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                Status = status,
                Sha256 = sha256,
                Launch = launch,
            };

        private static LaunchReport Launch(string outcome, bool attempted, int? requested = null, int? started = null) =>
            new LaunchReport { Outcome = outcome, Attempted = attempted, Requested = requested, Started = started };

        private static RemoteBuildOutcome Deployed(BuildResult result) =>
            With(RemoteBuildOutcome.Success(BuildId, result));

        private static RemoteBuildOutcome Failed(BuildResult result) =>
            With(RemoteBuildOutcome.Failure(BuildId, result?.ErrorCode ?? ErrorCode.DeployFailed, "failed", result));

        private static RemoteBuildOutcome With(RemoteBuildOutcome outcome)
        {
            outcome.Stage = "result";
            outcome.ArtifactSha256 = Sha;
            return outcome;
        }

        private static void AssertClass(RemoteBuildOutcome outcome, string deploy, string launch, bool stops)
        {
            var c = DeployResultClassifier.Classify(outcome);
            Assert.AreEqual(deploy, c.Deploy, "deploy");
            Assert.AreEqual(launch, c.Launch, "launch");
            Assert.AreEqual(stops, c.StopReason != null, "stop: " + c.StopReason);
        }

        [Test]
        public void Deployed_and_every_requested_client_started_passes()
        {
            AssertClass(Deployed(Result("deployed", Launch("relaunched", true, 2, 2))), "PASS", "PASS", false);
            AssertClass(Deployed(Result("deployed", Launch("launched", true, 1, 1))), "PASS", "PASS", false);
        }

        [Test]
        public void Reload_reusing_running_clients_continues_without_a_launch_verdict()
        {
            AssertClass(Deployed(Result("deployed", Launch("reusedRunning", false))), "PASS", "notApplicable", false);
        }

        [Test]
        public void AutoLaunch_off_is_deployed_but_stops_a_procedure()
        {
            AssertClass(Deployed(Result("deployed", Launch("disabled", false))), "PASS", "notApplicable", true);
        }

        [Test]
        public void A_real_partial_start_fails_the_launch()
        {
            AssertClass(Failed(Result("failed", Launch("partial", true, 2, 1))), "FAIL", "FAIL", true);
        }

        [Test]
        public void Failed_launch_and_unreached_launch_fail()
        {
            AssertClass(Failed(Result("failed", Launch("failed", true, 2, 0))), "FAIL", "FAIL", true);
            AssertClass(Failed(Result("failed", Launch("notReached", false))), "FAIL", "notReached", true);
            AssertClass(Failed(Result("failed", null)), "FAIL", "notReached", true);
        }

        [TestCase("partial", true, 2, 1)]
        [TestCase("failed", true, 2, 0)]
        [TestCase("notReached", false, null, null)]
        public void Deployed_with_a_failed_launch_is_a_contradiction(string outcome, bool attempted, int? requested, int? started)
        {
            AssertClass(Deployed(Result("deployed", Launch(outcome, attempted, requested, started))), "UNKNOWN", "UNKNOWN", true);
        }

        [Test]
        public void A_failed_launch_without_a_started_count_is_unknown()
        {
            AssertClass(Failed(Result("failed", Launch("failed", true, 2, null))), "UNKNOWN", "UNKNOWN", true);
        }

        [Test]
        public void Counts_that_disagree_with_the_outcome_are_unknown()
        {
            AssertClass(Deployed(Result("deployed", Launch("launched", true, 2, 1))), "UNKNOWN", "UNKNOWN", true);
            AssertClass(Failed(Result("failed", Launch("partial", true, 2, 2))), "UNKNOWN", "UNKNOWN", true);
            AssertClass(Failed(Result("failed", Launch("partial", true, 2, 0))), "UNKNOWN", "UNKNOWN", true);
            AssertClass(Failed(Result("failed", Launch("launched", true, 2, 2))), "UNKNOWN", "UNKNOWN", true);
        }

        [Test]
        public void A_missing_launch_report_or_other_hash_or_build_id_is_unknown()
        {
            AssertClass(Deployed(Result("deployed", null)), "UNKNOWN", "UNKNOWN", true);
            AssertClass(Deployed(Result("deployed", Launch("relaunched", true, 2, 2), OtherSha)), "UNKNOWN", "UNKNOWN", true);
            AssertClass(Deployed(Result("deployed", Launch("relaunched", true, 2, 2), null)), "UNKNOWN", "UNKNOWN", true);
            AssertClass(Deployed(Result("deployed", Launch("relaunched", true, 2, 2), Sha, "20261125T101500123Z-ffffffff")),
                "UNKNOWN", "UNKNOWN", true);
        }

        [Test]
        public void Failure_before_sending_fails_and_after_sending_is_unknown()
        {
            var sdk = RemoteBuildOutcome.Failure(null, ErrorCode.SdkBuildFailed, "SDK failed.");
            sdk.Stage = "sdkBuild";
            AssertClass(sdk, "FAIL", "notReached", true);

            var timeout = RemoteBuildOutcome.Failure(BuildId, ErrorCode.ResultTimeout, "timed out");
            timeout.Stage = "result";
            AssertClass(timeout, "UNKNOWN", "notReached", true);

            // The Bridge refused the artifact (e.g. HTTP 413) or the commit: nothing was deployed.
            var refused = RemoteBuildOutcome.Failure(BuildId, ErrorCode.UploadFailed, "HTTP 413");
            refused.Stage = "upload";
            AssertClass(refused, "FAIL", "notReached", true);

            // Any other error during the upload may have come after the commit was sent.
            var lost = RemoteBuildOutcome.Failure(BuildId, ErrorCode.UnknownError, "connection reset");
            lost.Stage = "upload";
            AssertClass(lost, "UNKNOWN", "notReached", true);
        }

        [Test]
        public void Cancellation_is_canceled_and_says_whether_it_was_after_sending()
        {
            var before = RemoteBuildOutcome.Cancellation(null, "Build cancelled.");
            before.Stage = "sdkBuild";
            var c = DeployResultClassifier.Classify(before);
            Assert.AreEqual("CANCELED", c.Deploy);
            Assert.AreEqual("canceled", c.StopReason);

            var after = RemoteBuildOutcome.Cancellation(BuildId, "Deploy cancelled.");
            after.Stage = "upload";
            Assert.AreEqual("canceledAfterSending", DeployResultClassifier.Classify(after).StopReason);
        }
    }
}

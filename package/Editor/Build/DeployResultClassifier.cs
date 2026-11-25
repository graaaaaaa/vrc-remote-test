using System;

namespace VRCRemoteTest
{
    /// <summary>
    /// Turns one attempt's outcome into the deploy and launch verdicts of the deploy result. The
    /// only place this classification is made: the runner validates and uses it, never redoes it.
    ///
    /// The Bridge reports a partial start as status failed with launch.outcome partial
    /// (StagingWatcher.LaunchAfterDeployAsync), so deployed with partial, failed or notReached is
    /// a combination it never writes: those, counts that disagree with the outcome, a hash or
    /// build ID that differs, and a missing launch report are UNKNOWN, not PASS or FAIL.
    /// </summary>
    internal static class DeployResultClassifier
    {
        internal const string Pass = "PASS";
        internal const string Fail = "FAIL";
        internal const string Unknown = "UNKNOWN";
        internal const string Canceled = "CANCELED";
        internal const string NotApplicable = "notApplicable";
        internal const string NotReached = "notReached";

        internal static DeployResultClassification Classify(RemoteBuildOutcome outcome)
        {
            if (outcome == null)
            {
                return Make(Unknown, Unknown, "noOutcome");
            }

            if (outcome.Canceled)
            {
                // Cancelling after the upload started is not a withdrawal: the Bridge may still deploy.
                return Make(Canceled, NotReached, AfterSending(outcome.Stage) ? "canceledAfterSending" : "canceled");
            }

            var result = outcome.Result;
            if (result == null)
            {
                if (outcome.Succeeded)
                {
                    return Make(Unknown, Unknown, "noBridgeResult");
                }

                // Before the upload nothing reached the Bridge. During it, the transport raises
                // these codes only for a refusal the Bridge answered or before the commit was
                // sent, so nothing was deployed; any other error there (the commit sent and no
                // answer), and every failure while waiting for the result, leaves the deploy unknown.
                if (!AfterSending(outcome.Stage)
                    || (outcome.Stage == "upload" && Array.IndexOf(RefusedBeforeDeploy, outcome.ErrorCode) >= 0))
                {
                    return Make(Fail, NotReached, outcome.ErrorCode ?? "failed");
                }

                return Make(Unknown, NotReached, outcome.ErrorCode ?? "noBridgeResult");
            }

            if (outcome.BuildId == null || result.BuildId != outcome.BuildId)
            {
                return Make(Unknown, Unknown, "buildIdMismatch");
            }

            if (result.Sha256 != null && outcome.ArtifactSha256 != null
                && !string.Equals(result.Sha256, outcome.ArtifactSha256, StringComparison.Ordinal))
            {
                return Make(Unknown, Unknown, "sha256Mismatch");
            }

            var launch = result.Launch;
            if (result.Status == "deployed")
            {
                if (result.Sha256 == null)
                {
                    return Make(Unknown, Unknown, "sha256Missing");
                }

                if (launch == null)
                {
                    return Make(Unknown, Unknown, "launchMissing");
                }

                switch (launch.Outcome)
                {
                    case "launched":
                    case "relaunched":
                        return launch.Attempted && launch.Requested is int requested && requested >= 1
                            && launch.Started == requested
                            ? Make(Pass, Pass, null)
                            : Make(Unknown, Unknown, "launchCountsContradict");
                    case "reusedRunning":
                        return launch.Attempted
                            ? Make(Unknown, Unknown, "launchContradicts")
                            : Make(Pass, NotApplicable, null);
                    case LaunchReport.Disabled:
                        return Make(Pass, NotApplicable, "autoLaunchDisabled");
                    default:
                        // partial, failed, notReached or an outcome this package does not know.
                        return Make(Unknown, Unknown, "deployedButLaunch:" + Bounded(launch.Outcome));
                }
            }

            if (result.Status == "failed")
            {
                if (launch == null)
                {
                    return Make(Fail, NotReached, result.ErrorCode ?? "deployFailed");
                }

                switch (launch.Outcome)
                {
                    case "partial":
                        return launch.Attempted && launch.Requested is int requested && launch.Started is int started
                            && started > 0 && started < requested
                            ? Make(Fail, Fail, "partialLaunch")
                            : Make(Unknown, Unknown, "launchCountsContradict");
                    case "failed":
                        // A missing count is unknown (the Bridge omits it after an unexpected error), not zero.
                        return launch.Attempted && launch.Started == 0
                            ? Make(Fail, Fail, result.ErrorCode ?? "launchFailed")
                            : Make(Unknown, Unknown, "launchCountsContradict");
                    case "notReached":
                        return Make(Fail, NotReached, result.ErrorCode ?? "deployFailed");
                    case LaunchReport.Disabled:
                    case "reusedRunning":
                        return Make(Fail, NotApplicable, result.ErrorCode ?? "deployFailed");
                    default:
                        // launched or relaunched with a failed deploy, or an unknown outcome.
                        return Make(Unknown, Unknown, "failedButLaunch:" + Bounded(launch.Outcome));
                }
            }

            return Make(Unknown, Unknown, "unknownStatus");
        }

        private static readonly string[] RefusedBeforeDeploy =
        {
            ErrorCode.UploadFailed, ErrorCode.BridgeUnavailable, ErrorCode.SizeExceeded, ErrorCode.ProtocolVersionMismatch,
        };

        private static bool AfterSending(string stage) => stage == "upload" || stage == "result" || stage == null;

        private static string Bounded(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "none";
            }

            var cleaned = BridgeMessages.Sanitize(value, 40);
            return string.IsNullOrEmpty(cleaned) ? "none" : cleaned;
        }

        /// <param name="stopReason">Null only when a procedure may continue.</param>
        private static DeployResultClassification Make(string deploy, string launch, string stopReason) =>
            new DeployResultClassification { Deploy = deploy, Launch = launch, StopReason = stopReason };
    }
}

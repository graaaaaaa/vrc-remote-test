using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRCRemoteTest.Tests
{
    /// <summary>The attempt facts the coordinator records for the deploy result, and the names callers reflect on.</summary>
    public class RemoteBuildAttemptTests
    {
        private string _artifact;
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _artifact = Path.Combine(Path.GetTempPath(), "vrcrt-attempt-" + Guid.NewGuid() + ".vrcw");
            File.WriteAllText(_artifact, "fake vrcw bytes");
            _root = Path.Combine(Path.GetTempPath(), "vrcrt-attempt-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_artifact))
            {
                File.Delete(_artifact);
            }

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private static FakeRemoteTransport DeployingTransport(Func<string> sha) => new FakeRemoteTransport
        {
            ResultFactory = buildId => new BuildResult
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                Status = "deployed",
                DeployedFileName = buildId + ".vrcw",
                Sha256 = sha(),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Launch = new LaunchReport { Attempted = true, Outcome = "relaunched", Requested = 2, Started = 2 },
            },
        };

        private RemoteBuildCoordinator Coordinator(FakeVrcSdkBuildAdapter sdk, IRemoteTransport transport) =>
            new RemoteBuildCoordinator(sdk, transport) { ResultWriter = new DeployResultWriter(_root), PackageVersion = "0.11.0" };

        [Test]
        public void A_successful_build_records_its_attempt_tag_and_result_file()
        {
            var sdk = new FakeVrcSdkBuildAdapter { ReturnedPath = _artifact };
            RemoteBuildCoordinator coordinator = null;
            string tagDuringBuild = null;
            sdk.DuringBuild = () => tagDuringBuild = coordinator.ActiveWorldBuildTag;
            coordinator = Coordinator(sdk, DeployingTransport(() => Sha256Calculator.ComputeHash(_artifact)));

            var outcome = coordinator.ExecuteRemoteBuildCoreAsync(
                new RemoteBuildRequest { WorldBuildTag = "run-tag-0001" }, null, default).GetAwaiter().GetResult();

            Assert.IsTrue(outcome.Succeeded);
            Assert.AreEqual("run-tag-0001", tagDuringBuild);
            Assert.IsNull(coordinator.ActiveWorldBuildTag, "the tag is shown only during the SDK build");
            Assert.IsTrue(BuildIdGenerator.IsValid(outcome.AttemptId));
            var json = JObject.Parse(File.ReadAllText(outcome.DeployResultPath));
            Assert.AreEqual(outcome.BuildId, (string)json["buildId"]);
            Assert.AreEqual("run-tag-0001", (string)json["artifact"]["worldBuildTag"]);
            Assert.AreEqual("result", (string)json["stage"]);
            Assert.AreEqual("PASS", (string)json["classification"]["launch"]);
            Assert.AreEqual("0.11.0", (string)json["packageVersion"]);
        }

        [Test]
        public void Without_a_requested_tag_the_attempt_id_is_the_tag()
        {
            var sdk = new FakeVrcSdkBuildAdapter { ReturnedPath = _artifact };
            var coordinator = Coordinator(sdk, DeployingTransport(() => Sha256Calculator.ComputeHash(_artifact)));

            var outcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.AreEqual(outcome.AttemptId, outcome.WorldBuildTag);
        }

        [Test]
        public void A_redeploy_keeps_the_tag_only_while_the_bytes_are_the_same()
        {
            var sdk = new FakeVrcSdkBuildAdapter { ReturnedPath = _artifact };
            var coordinator = Coordinator(sdk, DeployingTransport(() => Sha256Calculator.ComputeHash(_artifact)));
            coordinator.ExecuteRemoteBuildCoreAsync(new RemoteBuildRequest { WorldBuildTag = "first-build" }, null, default)
                .GetAwaiter().GetResult();

            var same = coordinator.DeployLastBuildAsync().GetAwaiter().GetResult();
            Assert.AreEqual("first-build", same.WorldBuildTag);

            File.WriteAllText(_artifact, "other bytes");
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Artifact changed"));
            var changed = coordinator.DeployLastBuildAsync().GetAwaiter().GetResult();
            Assert.IsNull(changed.WorldBuildTag);
            Assert.AreEqual("redeploy", changed.Kind);
        }

        [Test]
        public void A_busy_attempt_and_an_sdk_failure_are_saved_without_a_build_id()
        {
            var hold = new TaskCompletionSource<string>();
            var sdk = new FakeVrcSdkBuildAdapter { BuildTaskOverride = hold.Task };
            var coordinator = Coordinator(sdk, new FakeRemoteTransport());
            var first = coordinator.ExecuteRemoteBuildAsync();

            var busy = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();
            Assert.AreEqual(ErrorCode.BuildAlreadyRunning, busy.ErrorCode);
            StringAssert.StartsWith("attempt-", Path.GetFileName(busy.DeployResultPath));
            Assert.AreEqual("preflight", busy.Stage);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("SDK_BUILD_FAILED"));
            hold.SetException(new RemoteBuildException(ErrorCode.SdkBuildFailed, "SDK failed."));
            var failed = first.GetAwaiter().GetResult();
            Assert.AreEqual("sdkBuild", failed.Stage);
            var json = JObject.Parse(File.ReadAllText(failed.DeployResultPath));
            Assert.AreEqual(JTokenType.Null, json["buildId"].Type);
            Assert.AreEqual("FAIL", (string)json["classification"]["deploy"]);
        }

        [Test]
        public void Cancelling_marks_the_attempt_canceled()
        {
            var hold = new TaskCompletionSource<string>();
            var sdk = new FakeVrcSdkBuildAdapter { BuildTaskOverride = hold.Task };
            var coordinator = Coordinator(sdk, new FakeRemoteTransport());
            using (var cts = new CancellationTokenSource())
            {
                var running = coordinator.ExecuteRemoteBuildAsync(null, cts.Token);
                cts.Cancel();
                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("cancelled"));
                hold.SetResult(_artifact);
                var outcome = running.GetAwaiter().GetResult();

                Assert.IsTrue(outcome.Canceled);
                var json = JObject.Parse(File.ReadAllText(outcome.DeployResultPath));
                Assert.AreEqual(true, (bool)json["unity"]["canceled"]);
                Assert.AreEqual("CANCELED", (string)json["classification"]["deploy"]);
            }
        }

        [Test]
        public void An_upload_aborted_by_cancelling_is_canceled_not_failed()
        {
            var sdk = new FakeVrcSdkBuildAdapter { ReturnedPath = _artifact };
            using (var cts = new CancellationTokenSource())
            {
                var transport = new FakeRemoteTransport
                {
                    // LanRemoteTransport aborts the UnityWebRequest and reports UploadFailed.
                    UploadHandler = token =>
                    {
                        cts.Cancel();
                        return Task.FromException(new RemoteBuildException(ErrorCode.UploadFailed, "Bridge artifact upload failed (HTTP 0): Request aborted"));
                    },
                };
                var coordinator = Coordinator(sdk, transport);
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("UPLOAD_FAILED"));

                var outcome = coordinator.ExecuteRemoteBuildAsync(null, cts.Token).GetAwaiter().GetResult();

                Assert.AreEqual(ErrorCode.UploadFailed, outcome.ErrorCode, "the public error is unchanged");
                Assert.IsTrue(outcome.Canceled);
                var json = JObject.Parse(File.ReadAllText(outcome.DeployResultPath));
                Assert.AreEqual("CANCELED", (string)json["classification"]["deploy"]);
                Assert.AreEqual("canceledAfterSending", (string)json["classification"]["stopReason"]);
            }
        }

        [Test]
        public void The_tag_is_read_without_creating_the_shared_coordinator()
        {
            // Reading it outside a build must not throw, even with no pairing.
            Assert.DoesNotThrow(() => _ = RemoteBuildApi.ActiveWorldBuildTag);
            Assert.IsNull(RemoteBuildApi.ActiveWorldBuildTag);
        }

        [Test]
        public void Without_a_writer_nothing_is_saved()
        {
            var coordinator = new RemoteBuildCoordinator(new FakeVrcSdkBuildAdapter(), new FakeRemoteTransport());
            var outcome = coordinator.DeployLastBuildAsync().GetAwaiter().GetResult();
            Assert.IsNull(outcome.DeployResultPath);
            Assert.IsNotNull(outcome.AttemptId);
        }

        /// <summary>vrchat-commander finds these by name with reflection (PRT001Dispatcher.SavedBuild.cs).</summary>
        [Test]
        public void Names_found_by_reflection_stay_unambiguous()
        {
            var type = typeof(RemoteBuildCoordinator);
            Assert.IsNotNull(type.GetMethod("DeployLastBuildAsync", BindingFlags.Public | BindingFlags.Instance));
            Assert.IsNotNull(type.GetMethod("ExecuteRemoteBuildAsync", BindingFlags.Public | BindingFlags.Instance));
            Assert.IsNotNull(type.GetProperty("LastArtifact", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod(true));
            Assert.IsNotNull(type.GetProperty("IsRunning", BindingFlags.Public | BindingFlags.Instance));
            Assert.IsNotNull(typeof(RemoteBuildCommand).GetMethod("GetCoordinator", BindingFlags.NonPublic | BindingFlags.Static));
            Assert.IsNotNull(typeof(BuildArtifact).GetMethod("FromSdkPath", BindingFlags.Public | BindingFlags.Static));
            foreach (var name in new[] { "Succeeded", "BuildId", "ErrorCode", "ErrorMessage" })
            {
                Assert.IsNotNull(typeof(RemoteBuildOutcome).GetProperty(name), name);
            }
        }
    }
}

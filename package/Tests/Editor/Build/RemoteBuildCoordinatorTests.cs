using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRCRemoteTest.Tests
{
    public class RemoteBuildCoordinatorTests
    {
        private string _validArtifactPath;
        private int _originalTimeout;
        private int _originalInterval;

        [SetUp]
        public void SetUp()
        {
            _validArtifactPath = Path.Combine(Path.GetTempPath(), "vrc-remote-test-coord-" + Guid.NewGuid() + ".vrcw");
            File.WriteAllText(_validArtifactPath, "fake vrcw bytes");

            _originalTimeout = RemoteTestSettings.ResultTimeoutSeconds;
            _originalInterval = RemoteTestSettings.PollIntervalSeconds;
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_validArtifactPath))
            {
                File.Delete(_validArtifactPath);
            }

            RemoteTestSettings.ResultTimeoutSeconds = _originalTimeout;
            RemoteTestSettings.PollIntervalSeconds = _originalInterval;
        }

        // Unity Test Framework's bundled NUnit does not reliably support
        // [Test] methods returning async Task, so these block synchronously
        // via .GetAwaiter().GetResult() instead — the same pattern
        // RemoteBuildCommand.ExecuteRemoteBuildHeadless uses in production.

        [Test]
        public void Happy_path_reaches_Succeeded()
        {
            var sdkAdapter = new FakeVrcSdkBuildAdapter { ReturnedPath = _validArtifactPath };
            var transport = new FakeRemoteTransport
            {
                ResultFactory = buildId => new BuildResult
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    BuildId = buildId,
                    Status = "deployed",
                    DeployedFileName = $"vrc-remote-{buildId}.vrcw",
                    Sha256 = "8f2a5f6802f9dc5307740870312b2df9a99104960df3a9df66166d54665d4d7c",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            var coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);

            var outcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.IsTrue(outcome.Succeeded);
            Assert.IsNotNull(outcome.BuildId);
            Assert.AreEqual("deployed", outcome.Result.Status);
            Assert.IsTrue(transport.UploadCalled);
        }

        [Test]
        public void Sdk_failure_reaches_Failed()
        {
            var sdkAdapter = new FakeVrcSdkBuildAdapter
            {
                ThrowOnBuild = new RemoteBuildException(ErrorCode.SdkBuildFailed, "SDK validation failed."),
            };
            var transport = new FakeRemoteTransport();
            var coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);

            LogAssert.Expect(LogType.Error, new Regex(@"^\[VRC Remote Test\] SDK_BUILD_FAILED:"));
            var outcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(ErrorCode.SdkBuildFailed, outcome.ErrorCode);
            Assert.IsFalse(transport.UploadCalled);
        }

        [Test]
        public void Share_unavailable_fails_at_preflight_before_building()
        {
            var sdkAdapter = new FakeVrcSdkBuildAdapter { ReturnedPath = _validArtifactPath };
            var transport = new FakeRemoteTransport { IsAvailable = false };
            var coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);

            LogAssert.Expect(LogType.Error, new Regex(@"^\[VRC Remote Test\] REMOTE_SHARE_UNAVAILABLE:"));
            var outcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(ErrorCode.RemoteShareUnavailable, outcome.ErrorCode);
            Assert.AreEqual(0, sdkAdapter.BuildCallCount);
        }

        [Test]
        public void Timeout_reaches_Failed_when_result_never_appears()
        {
            RemoteTestSettings.ResultTimeoutSeconds = 1;
            RemoteTestSettings.PollIntervalSeconds = 1;

            var sdkAdapter = new FakeVrcSdkBuildAdapter { ReturnedPath = _validArtifactPath };
            var transport = new FakeRemoteTransport(); // ResultQueue empty -> PollResult always null

            var coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);
            LogAssert.Expect(LogType.Error, new Regex(@"^\[VRC Remote Test\] RESULT_TIMEOUT:"));
            var outcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(ErrorCode.ResultTimeout, outcome.ErrorCode);
        }

        [Test]
        public void Bridge_failure_surfaces_error_code_and_message()
        {
            var sdkAdapter = new FakeVrcSdkBuildAdapter { ReturnedPath = _validArtifactPath };
            var transport = new FakeRemoteTransport
            {
                ResultFactory = buildId => new BuildResult
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    BuildId = buildId,
                    Status = "failed",
                    ErrorCode = ErrorCode.HashMismatch,
                    ErrorMessage = "Computed hash did not match manifest.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            var coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);

            LogAssert.Expect(LogType.Error, new Regex(@"^\[VRC Remote Test\] Bridge error: HASH_MISMATCH"));
            var outcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(ErrorCode.HashMismatch, outcome.ErrorCode);
            Assert.AreEqual("Computed hash did not match manifest.", outcome.ErrorMessage);
        }

        [Test]
        public void Invalid_result_is_a_hard_failure_not_a_retry()
        {
            var sdkAdapter = new FakeVrcSdkBuildAdapter { ReturnedPath = _validArtifactPath };
            var transport = new FakeRemoteTransport
            {
                // Wrong protocolVersion makes this fail IsValidResult.
                ResultFactory = buildId => new BuildResult
                {
                    ProtocolVersion = 999,
                    BuildId = buildId,
                    Status = "deployed",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            var coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);

            LogAssert.Expect(LogType.Error, new Regex(@"^\[VRC Remote Test\] RESULT_INVALID:"));
            var outcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(ErrorCode.ResultInvalid, outcome.ErrorCode);
        }

        [Test]
        public void Concurrent_execution_is_rejected()
        {
            var blockingBuild = new TaskCompletionSource<string>();
            var sdkAdapter = new FakeVrcSdkBuildAdapter { BuildTaskOverride = blockingBuild.Task };
            var transport = new FakeRemoteTransport
            {
                // Without this, the first (blocked-then-released) run would
                // fall through to PollForResultAsync and wait out the full
                // default timeout once unblocked, slowing the test down for
                // no reason relevant to what this test actually checks.
                ResultFactory = buildId => new BuildResult
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    BuildId = buildId,
                    Status = "deployed",
                    DeployedFileName = $"vrc-remote-{buildId}.vrcw",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                },
            };
            var coordinator = new RemoteBuildCoordinator(sdkAdapter, transport);

            var firstRunTask = coordinator.ExecuteRemoteBuildAsync();

            var secondOutcome = coordinator.ExecuteRemoteBuildAsync().GetAwaiter().GetResult();

            Assert.IsFalse(secondOutcome.Succeeded);
            Assert.AreEqual(ErrorCode.BuildAlreadyRunning, secondOutcome.ErrorCode);

            blockingBuild.SetResult(_validArtifactPath);
            firstRunTask.GetAwaiter().GetResult();
        }

        [Test]
        public void IsValidResult_accepts_golden_fixture_shape()
        {
            var result = new BuildResult
            {
                ProtocolVersion = 1,
                BuildId = "20260901T112522481Z-a91f02cc",
                Status = "deployed",
                DeployedFileName = "vrc-remote-20260901T112522481Z-a91f02cc.vrcw",
                Sha256 = "8f2a5f6802f9dc5307740870312b2df9a99104960df3a9df66166d54665d4d7c",
            };

            Assert.IsTrue(RemoteBuildCoordinator.IsValidResult(result, "20260901T112522481Z-a91f02cc"));
        }

        [Test]
        public void IsValidResult_rejects_buildId_mismatch()
        {
            var result = new BuildResult
            {
                ProtocolVersion = 1,
                BuildId = "some-other-build-id",
                Status = "deployed",
            };

            Assert.IsFalse(RemoteBuildCoordinator.IsValidResult(result, "20260901T112522481Z-a91f02cc"));
        }

        [Test]
        public void IsValidResult_rejects_unknown_status()
        {
            var result = new BuildResult
            {
                ProtocolVersion = 1,
                BuildId = "20260901T112522481Z-a91f02cc",
                Status = "pending",
            };

            Assert.IsFalse(RemoteBuildCoordinator.IsValidResult(result, "20260901T112522481Z-a91f02cc"));
        }

        [Test]
        public void IsValidResult_rejects_malformed_sha256()
        {
            var result = new BuildResult
            {
                ProtocolVersion = 1,
                BuildId = "20260901T112522481Z-a91f02cc",
                Status = "deployed",
                Sha256 = "not-hex",
            };

            Assert.IsFalse(RemoteBuildCoordinator.IsValidResult(result, "20260901T112522481Z-a91f02cc"));
        }

        [Test]
        public void IsValidResult_rejects_unsafe_deployedFileName()
        {
            var result = new BuildResult
            {
                ProtocolVersion = 1,
                BuildId = "20260901T112522481Z-a91f02cc",
                Status = "deployed",
                DeployedFileName = "../../evil.vrcw",
            };

            Assert.IsFalse(RemoteBuildCoordinator.IsValidResult(result, "20260901T112522481Z-a91f02cc"));
        }
    }
}

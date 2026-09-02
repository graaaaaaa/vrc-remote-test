using System;
using System.IO;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class SmbRemoteTransportTests
    {
        private string _shareRoot;
        private string _sourceArtifactPath;

        [SetUp]
        public void SetUp()
        {
            _shareRoot = Path.Combine(Path.GetTempPath(), "vrc-remote-test-share-" + Guid.NewGuid());
            Directory.CreateDirectory(_shareRoot);

            _sourceArtifactPath = Path.Combine(Path.GetTempPath(), "vrc-remote-test-source-" + Guid.NewGuid() + ".vrcw");
            File.WriteAllText(_sourceArtifactPath, "fake vrcw bytes");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_shareRoot))
            {
                Directory.Delete(_shareRoot, recursive: true);
            }

            if (File.Exists(_sourceArtifactPath))
            {
                File.Delete(_sourceArtifactPath);
            }
        }

        [Test]
        public void Constructor_rejects_empty_share_path()
        {
            Assert.Throws<RemoteBuildException>(() => new SmbRemoteTransport(string.Empty));
        }

        [Test]
        public void Constructor_rejects_root_share_path()
        {
            Assert.Throws<RemoteBuildException>(() => new SmbRemoteTransport("/"));
        }

        [Test]
        public void Constructor_rejects_smb_uri()
        {
            Assert.Throws<RemoteBuildException>(
                () => new SmbRemoteTransport("smb://user:pass@host/share"));
        }

        [Test]
        public void IsAvailable_true_for_existing_writable_directory()
        {
            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsTrue(transport.IsAvailable);
        }

        [Test]
        public void IsAvailable_false_for_nonexistent_path()
        {
            var transport = new SmbRemoteTransport(Path.Combine(_shareRoot, "does-not-exist"));
            Assert.IsFalse(transport.IsAvailable);
        }

        // Unity Test Framework's bundled NUnit does not reliably support
        // [Test] methods returning async Task, so these block synchronously
        // via .GetAwaiter().GetResult() instead.

        [Test]
        public void UploadBuildAsync_creates_artifact_and_manifest_in_incoming()
        {
            var transport = new SmbRemoteTransport(_shareRoot);
            var artifact = BuildArtifact.FromSdkPath(_sourceArtifactPath);
            artifact.Sha256 = Sha256Calculator.ComputeHash(_sourceArtifactPath);
            var buildId = BuildIdGenerator.Generate();
            var manifest = new BuildManifest
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                FileName = $"{buildId}.vrcw",
                Size = artifact.Size,
                Sha256 = artifact.Sha256,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            transport.UploadBuildAsync(artifact, manifest, buildId).GetAwaiter().GetResult();

            var incomingDir = Path.Combine(_shareRoot, "incoming");
            var artifactPath = Path.Combine(incomingDir, $"{buildId}.vrcw");
            var manifestPath = Path.Combine(incomingDir, $"{buildId}.ready.json");

            Assert.IsTrue(File.Exists(artifactPath), "Artifact was not uploaded.");
            Assert.IsTrue(File.Exists(manifestPath), "Manifest was not uploaded.");
            Assert.AreEqual(File.ReadAllText(_sourceArtifactPath), File.ReadAllText(artifactPath));
        }

        [Test]
        public void UploadBuildAsync_leaves_no_part_files()
        {
            var transport = new SmbRemoteTransport(_shareRoot);
            var artifact = BuildArtifact.FromSdkPath(_sourceArtifactPath);
            artifact.Sha256 = Sha256Calculator.ComputeHash(_sourceArtifactPath);
            var buildId = BuildIdGenerator.Generate();
            var manifest = new BuildManifest
            {
                ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                BuildId = buildId,
                FileName = $"{buildId}.vrcw",
                Size = artifact.Size,
                Sha256 = artifact.Sha256,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            transport.UploadBuildAsync(artifact, manifest, buildId).GetAwaiter().GetResult();

            var incomingDir = Path.Combine(_shareRoot, "incoming");
            var partFiles = Directory.GetFiles(incomingDir, "*.part");
            CollectionAssert.IsEmpty(partFiles);
        }

        [Test]
        public void PollResult_returns_null_when_no_result_file()
        {
            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsNull(transport.PollResult("20260901T112522481Z-a91f02cc"));
        }

        [Test]
        public void PollResult_deserializes_golden_deployed_result()
        {
            var resultsDir = Path.Combine(_shareRoot, "results");
            Directory.CreateDirectory(resultsDir);
            var buildId = "20260901T112522481Z-a91f02cc";
            File.WriteAllText(
                Path.Combine(resultsDir, $"{buildId}.json"), TestFixtures.Read("sample-result.json"));

            var transport = new SmbRemoteTransport(_shareRoot);
            var result = transport.PollResult(buildId);

            Assert.IsNotNull(result);
            Assert.AreEqual("deployed", result.Status);
            Assert.AreEqual("vrc-remote-20260901T112522481Z-a91f02cc.vrcw", result.DeployedFileName);
        }

        [Test]
        public void PollResult_returns_null_for_corrupt_json()
        {
            var resultsDir = Path.Combine(_shareRoot, "results");
            Directory.CreateDirectory(resultsDir);
            var buildId = "20260901T112522481Z-a91f02cc";
            File.WriteAllText(Path.Combine(resultsDir, $"{buildId}.json"), "{ not valid json");

            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsNull(transport.PollResult(buildId));
        }

        [Test]
        public void PollVrchatStatus_returns_null_when_no_status_file()
        {
            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsNull(transport.PollVrchatStatus());
        }

        [Test]
        public void PollVrchatStatus_deserializes_valid_status()
        {
            var statusDir = Path.Combine(_shareRoot, "status");
            Directory.CreateDirectory(statusDir);
            File.WriteAllText(
                Path.Combine(statusDir, "vrchat-status.json"),
                "{\"isRunning\":true,\"watchWorldsDetected\":true,\"processId\":4321," +
                "\"startTimeUtc\":\"2026-09-02T12:00:00Z\",\"updatedAtUtc\":\"2026-09-02T12:00:05Z\"}");

            var transport = new SmbRemoteTransport(_shareRoot);
            var status = transport.PollVrchatStatus();

            Assert.IsNotNull(status);
            Assert.IsTrue(status.IsRunning);
            Assert.IsTrue(status.WatchWorldsDetected);
            Assert.AreEqual(4321, status.ProcessId);
        }

        [Test]
        public void PollVrchatStatus_returns_null_for_corrupt_json()
        {
            var statusDir = Path.Combine(_shareRoot, "status");
            Directory.CreateDirectory(statusDir);
            File.WriteAllText(Path.Combine(statusDir, "vrchat-status.json"), "{ not valid json");

            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsNull(transport.PollVrchatStatus());
        }

        [Test]
        public void PollVrchatStatus_returns_null_when_file_exceeds_size_guard()
        {
            var statusDir = Path.Combine(_shareRoot, "status");
            Directory.CreateDirectory(statusDir);
            // Deliberately oversized (>64 KiB) to exercise the size guard
            // (Codex plan review Phase 4a, Round 2, confidence 0.80).
            var oversizedJson = "{\"padding\":\"" + new string('a', 70 * 1024) + "\"}";
            File.WriteAllText(Path.Combine(statusDir, "vrchat-status.json"), oversizedJson);

            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsNull(transport.PollVrchatStatus());
        }

        [Test]
        public void PollVrchatLog_returns_null_when_no_log_file()
        {
            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsNull(transport.PollVrchatLog());
        }

        [Test]
        public void PollVrchatLog_returns_content_when_present()
        {
            var logsDir = Path.Combine(_shareRoot, "logs");
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(Path.Combine(logsDir, "vrchat-latest.log"), "2026.09.02 12:00:00 Debug - hello");

            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.AreEqual("2026.09.02 12:00:00 Debug - hello", transport.PollVrchatLog());
        }

        [Test]
        public void PollVrchatLog_returns_null_when_file_exceeds_size_guard()
        {
            var logsDir = Path.Combine(_shareRoot, "logs");
            Directory.CreateDirectory(logsDir);
            // Deliberately oversized (>512 KiB) to exercise the size guard
            // (Codex plan review Phase 5, Round 2, confidence 0.90).
            var oversized = new string('a', 520 * 1024);
            File.WriteAllText(Path.Combine(logsDir, "vrchat-latest.log"), oversized);

            var transport = new SmbRemoteTransport(_shareRoot);
            Assert.IsNull(transport.PollVrchatLog());
        }
    }
}

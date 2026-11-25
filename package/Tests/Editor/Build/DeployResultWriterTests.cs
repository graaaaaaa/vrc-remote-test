using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace VRCRemoteTest.Tests
{
    public class DeployResultWriterTests
    {
        private const string BuildId = "20261125T101500123Z-0a1b2c3d";
        private const string RunId = "20261125T101500Z-0a1b2c3d";
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "vrcrt-writer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private static DeployResult Result(string buildId = BuildId, string attemptId = "20261125T101459000Z-11112222") =>
            new DeployResult
            {
                AttemptId = attemptId,
                BuildId = buildId,
                Kind = "build",
                Stage = "result",
                Classification = new DeployResultClassification { Deploy = "PASS", Launch = "PASS" },
            };

        private string MakeRun(string runId = RunId, bool withRunJson = true)
        {
            var run = Path.Combine(_root, "runs", runId);
            Directory.CreateDirectory(run);
            if (withRunJson)
            {
                File.WriteAllText(Path.Combine(run, "run.json"), "{}");
            }

            return run;
        }

        [Test]
        public void Writes_the_result_under_its_build_id()
        {
            var path = new DeployResultWriter(_root).TryWrite(Result(), null);

            Assert.AreEqual(Path.Combine(_root, "deploy-results", BuildId + ".json"), path);
            var json = JObject.Parse(File.ReadAllText(path));
            Assert.AreEqual("vrcrt.deploy-result", (string)json["schema"]);
            Assert.AreEqual(1, (int)json["schemaVersion"]);
            Assert.AreEqual("PASS", (string)json["classification"]["deploy"]);
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path), "*.part"));
        }

        [Test]
        public void Without_a_build_id_it_is_named_after_the_attempt()
        {
            var path = new DeployResultWriter(_root).TryWrite(Result(buildId: null), null);

            Assert.AreEqual("attempt-20261125T101459000Z-11112222.json", Path.GetFileName(path));
        }

        [Test]
        public void A_finished_result_is_never_replaced()
        {
            var writer = new DeployResultWriter(_root);
            var first = writer.TryWrite(Result(), null);
            var before = File.ReadAllText(first);

            LogAssert.Expect(LogType.Error, new Regex("already exists"));
            Assert.IsNull(writer.TryWrite(Result(), null));
            Assert.AreEqual(before, File.ReadAllText(first));
        }

        [Test]
        public void A_run_gets_its_own_copy()
        {
            var run = MakeRun();

            var path = new DeployResultWriter(_root).TryWrite(Result(), run);

            Assert.AreEqual(Path.Combine(run, "deploy-result.json"), path);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "deploy-results", BuildId + ".json")));
        }

        [TestCase("not-a-run-id", true)]
        [TestCase(RunId, false)]
        public void Refuses_a_folder_that_is_not_a_run(string runId, bool withRunJson)
        {
            var run = MakeRun(runId, withRunJson);

            LogAssert.Expect(LogType.Error, new Regex("run folder"));
            Assert.IsNull(new DeployResultWriter(_root).TryWrite(Result(), run));
            Assert.IsFalse(File.Exists(Path.Combine(run, "deploy-result.json")));
        }

        [Test]
        public void Refuses_a_run_folder_inside_a_git_work_tree()
        {
            Directory.CreateDirectory(Path.Combine(_root, ".git"));
            var run = MakeRun();

            LogAssert.Expect(LogType.Error, new Regex("Git work tree"));
            Assert.IsNull(new DeployResultWriter(_root).TryWrite(Result(), run));
            Assert.IsFalse(File.Exists(Path.Combine(run, "deploy-result.json")));
        }

        [Test]
        public void Synced_home_folders_are_named_as_such()
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
            {
                Assert.Ignore("no HOME");
            }

            StringAssert.Contains("Documents", DeployResultWriter.SyncedOrTrackedLocation(Path.Combine(home, "Documents", "x", "runs", RunId)));
            StringAssert.Contains("Mobile Documents",
                DeployResultWriter.SyncedOrTrackedLocation(Path.Combine(home, "Library", "Mobile Documents", "runs", RunId)));
            Assert.IsNull(DeployResultWriter.SyncedOrTrackedLocation(Path.Combine(_root, "runs", RunId)));
        }

        [Test]
        public void Refuses_a_relative_or_unnormalized_run_path()
        {
            var run = MakeRun();
            LogAssert.Expect(LogType.Error, new Regex("run folder"));
            Assert.IsNull(new DeployResultWriter(_root).TryWrite(Result(), Path.Combine(run, "..", RunId)));
        }

        [Test]
        public void Refuses_a_run_folder_that_is_a_symbolic_link()
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
            {
                Assert.Ignore("symbolic links are checked with realpath on macOS");
            }

            var real = Path.Combine(_root, "elsewhere", RunId);
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "run.json"), "{}");
            Directory.CreateDirectory(Path.Combine(_root, "runs"));
            var link = Path.Combine(_root, "runs", RunId);
            Run("/bin/ln", "-s", real, link);

            LogAssert.Expect(LogType.Error, new Regex("symbolic link"));
            Assert.IsNull(new DeployResultWriter(_root).TryWrite(Result(), link));
            Assert.IsFalse(File.Exists(Path.Combine(real, "deploy-result.json")));
        }

        [Test]
        public void Folders_and_files_are_owner_only()
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
            {
                Assert.Ignore("permissions are set on macOS");
            }

            var path = new DeployResultWriter(_root).TryWrite(Result(), null);

            Assert.AreEqual("600", Run("/usr/bin/stat", "-f", "%Lp", path));
            Assert.AreEqual("700", Run("/usr/bin/stat", "-f", "%Lp", Path.GetDirectoryName(path)));
            Assert.AreEqual("700", Run("/usr/bin/stat", "-f", "%Lp", _root));
        }

        [Test]
        public void Build_cuts_the_message_and_drops_an_invalid_build_id()
        {
            var outcome = RemoteBuildOutcome.Failure("not a build id", ErrorCode.UnknownError, new string('x', 5000));
            outcome.AttemptId = "20261125T101459000Z-11112222";
            outcome.Stage = "result";

            var result = DeployResultWriter.Build(outcome, "0.11.0");

            Assert.IsNull(result.BuildId);
            Assert.LessOrEqual(result.Unity.Message.Length, 2000 + "...(truncated)".Length);
            Assert.AreEqual("0.11.0", result.PackageVersion);
        }

        private static string Run(string file, params string[] arguments)
        {
            // Test paths have no quotes or spaces; ArgumentList is not relied on (Unity's Mono).
            var info = new ProcessStartInfo(file, "\"" + string.Join("\" \"", arguments) + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };

            using (var process = Process.Start(info))
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return output;
            }
        }
    }
}

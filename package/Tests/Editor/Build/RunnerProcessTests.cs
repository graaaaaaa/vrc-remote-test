using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace VRCRemoteTest.Tests
{
    public class RunnerProcessTests
    {
        [TestCase("3.9", true)]
        [TestCase("3.12", true)]
        [TestCase("4.0", true)]
        [TestCase("3.8", false)]
        [TestCase("2.7", false)]
        [TestCase("", false)]
        [TestCase("3.9\nextra", false)]
        public void Python_39_or_later_is_enough(string version, bool enough)
        {
            Assert.AreEqual(enough, RunnerProcess.VersionIsEnough(version));
        }

        [Test]
        public void The_runner_gets_no_token_and_a_fixed_path()
        {
            var environment = RunnerProcess.ChildEnvironment("https://192.0.2.1:38443", "ABCDEF");

            Assert.AreEqual("/usr/bin:/bin:/usr/sbin:/sbin", environment["PATH"]);
            Assert.AreEqual("abcdef", environment["VRCRT_CERT_SHA256"]);
            foreach (var name in environment.Keys)
            {
                StringAssert.DoesNotContain("TOKEN", name);
            }
        }

        [Test]
        public void The_packaged_runner_is_found()
        {
            var path = RunnerProcess.RunnerPath();
            if (path == null)
            {
                Assert.Ignore("the package is not installed through the package manager here");
            }

            StringAssert.EndsWith(Path.Combine("Tools~", "vrcrt_runner.py"), path);
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(path), "vrcrt.sh")));
        }

        [Test]
        public void Arguments_reach_Python_exactly_as_given()
        {
            var python = RunnerProcess.PythonPath();
            if (RunnerProcess.CheckPython(python) != null)
            {
                Assert.Ignore("no Python 3.9+ here");
            }

            var arguments = new[]
            {
                "-I", "-c", "import json, sys; print(json.dumps(sys.argv[1:]))",
                "/Users/x/Library/Application Support/VRCRemoteTest/runs/1",
                "quote \" inside", "back\\slash", "trailing\\", "\\\"both", "",
                "two\\\\backslashes", "dollar\\$HOME", "back`tick` \\`x`", "it's", "'", "$(nope)",
            };
            var result = RunnerProcess.Run(python, arguments, RunnerProcess.ChildEnvironment(null, null), TimeSpan.FromSeconds(10));

            Assert.AreEqual(0, result.ExitCode, result.Stderr);
            var echoed = JArray.Parse(result.Stdout.Trim());
            CollectionAssert.AreEqual(new[]
            {
                "/Users/x/Library/Application Support/VRCRemoteTest/runs/1",
                "quote \" inside", "back\\slash", "trailing\\", "\\\"both", "",
                "two\\\\backslashes", "dollar\\$HOME", "back`tick` \\`x`", "it's", "'", "$(nope)",
            }, echoed.ToObject<string[]>());
        }

        [Test]
        public void The_claim_key_is_the_one_the_runner_computes()
        {
            var python = RunnerProcess.PythonPath();
            if (RunnerProcess.CheckPython(python) != null)
            {
                Assert.Ignore("no Python 3.9+ here");
            }

            const string url = "https://192.0.2.1:38443";
            const string pin = "ABCDEF0123";
            var result = RunnerProcess.Run(python, new[]
            {
                "-I", "-c", "import hashlib, sys; print(hashlib.sha256((sys.argv[1] + '|' + sys.argv[2].lower()).encode()).hexdigest()[:24])",
                url, pin,
            }, RunnerProcess.ChildEnvironment(null, null), TimeSpan.FromSeconds(10));

            Assert.AreEqual(result.Stdout.Trim(), RemoteRunController.ClaimKey(url, pin));
        }

        [Test]
        public void A_child_that_overstays_is_stopped()
        {
            var python = RunnerProcess.PythonPath();
            if (RunnerProcess.CheckPython(python) != null)
            {
                Assert.Ignore("no Python 3.9+ here");
            }

            var started = DateTime.UtcNow;
            var result = RunnerProcess.Run(python, new[] { "-I", "-c", "import time; time.sleep(30)" },
                RunnerProcess.ChildEnvironment(null, null), TimeSpan.FromSeconds(1));

            Assert.IsTrue(result.TimedOut);
            Assert.Less((DateTime.UtcNow - started).TotalSeconds, 10);
        }

        [Test]
        public void A_run_summary_reads_the_results_and_the_last_progress()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vrcrt-summary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "run.json"),
                    "{\"schema\":\"vrcrt.run\",\"state\":\"finished\",\"result\":\"UNKNOWN\",\"reason\":\"likelyLoggedOut\","
                    + "\"detail\":\"slot 1: log in to VRChat profile 1\",\"results\":{\"deploy\":\"PASS\",\"clientReadiness\":\"UNKNOWN\","
                    + "\"testVerdict\":\"notJudged\"}}");
                File.WriteAllText(Path.Combine(dir, "progress.jsonl"),
                    "{\"kind\":\"step\",\"message\":\"prepare\"}\n{\"kind\":\"wait\",\"message\":\"waiting\",\"reasons\":{\"1\":\"x\"}}\nnot json\n");

                var summary = RunSummary.Read(dir);

                Assert.IsTrue(summary.Finished);
                Assert.AreEqual("notJudged", summary.Results["testVerdict"]);
                StringAssert.Contains("likelyLoggedOut", summary.Describe());
                StringAssert.Contains("profile 1", summary.Describe());
                StringAssert.StartsWith("waiting", summary.LastProgress);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void A_pass_is_described_as_prepared_not_as_a_passed_test()
        {
            var summary = new RunSummary { Finished = true, Result = "PASS" };
            StringAssert.Contains("not judged", summary.Describe());
        }
    }
}

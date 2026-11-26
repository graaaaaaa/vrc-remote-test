using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace VRCRemoteTest.Tests
{
    public class RunWaitingTests
    {
        private const string Url = "https://192.0.2.1:38443";
        private const string Pin = "ABCDEF0123";
        private const string RunId = "20261126T010203Z-0123abcd";
        private const string WaitId = "0123456789abcdef0123456789abcdef";

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "vrcrt-find-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "locks"));
            Directory.CreateDirectory(Path.Combine(_root, "runs"));
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_root, true);
        }

        // --- 6.1: the claim key and the run ID -------------------------------------------------

        [Test]
        public void The_claim_key_is_sha256_of_url_bar_lowercased_pin_cut_to_24()
        {
            // hashlib.sha256(b"https://192.0.2.1:38443|abcdef0123").hexdigest()[:24]
            Assert.AreEqual("3afe7ce3a981acf714e8a658", RemoteRunController.ClaimKey(Url, Pin));
            Assert.AreEqual(RemoteRunController.ClaimKey(Url, "abcdef0123"), RemoteRunController.ClaimKey(Url, Pin));
        }

        [TestCase("20261126T010203Z-0123abcd", true)]
        [TestCase("20261126T010203Z-0123ABCD", false)]
        [TestCase("20261126T010203Z-0123abcd\n", false)]
        [TestCase("../20261126T010203Z-0123abcd", false)]
        [TestCase("20261126T010203Z-0123abc", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void A_run_ID_is_the_runners_shape_only(string value, bool expected)
        {
            Assert.AreEqual(expected, RunFiles.IsRunId(value));
        }

        // --- 6.1: finding the run --------------------------------------------------------------

        private string ClaimPath(string url = Url, string pin = Pin) =>
            Path.Combine(_root, "locks", RemoteRunController.ClaimKey(url, pin) + ".json");

        private void WriteClaim(string runId, string url = Url, string pin = Pin, string extra = "")
        {
            File.WriteAllText(ClaimPath(url, pin),
                "{\"runId\": " + (runId == null ? "null" : "\"" + runId + "\"") + ", \"token\": \"" + WaitId + "\", "
                + "\"owner\": {\"pid\": 1, \"start\": 2}, \"deadlineUtc\": \"2026-11-26T03:00:00Z\"" + extra + "}");
        }

        private string WriteRun(string runId, string state, string procedure = "manual-steps", string recordRunId = null)
        {
            var directory = Path.Combine(_root, "runs", runId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "run.json"),
                "{\"schema\": \"vrcrt.run\", \"schemaVersion\": 1, \"runId\": \"" + (recordRunId ?? runId) + "\", \"state\": \"" + state
                + "\", \"deadlineUtc\": \"2026-11-26T03:00:00Z\", \"procedure\": {\"name\": \"" + procedure + "\"}}");
            return directory;
        }

        [Test]
        public void A_running_run_named_by_this_Bridges_claim_is_found()
        {
            WriteClaim(RunId);
            var directory = WriteRun(RunId, "running");

            var found = RunDiscovery.Find(_root, Url, Pin, out var why);

            Assert.IsNotNull(found, why);
            Assert.AreEqual(RunId, found.RunId);
            Assert.AreEqual(directory, found.Directory);
            Assert.AreEqual("running", found.State);
            Assert.AreEqual("manual-steps", found.Procedure);
        }

        [Test]
        public void A_building_run_is_found_too()
        {
            WriteClaim(RunId);
            WriteRun(RunId, "building", "deploy-collect");

            Assert.AreEqual("building", RunDiscovery.Find(_root, Url, Pin, out _)?.State);
        }

        [TestCase("prepared")]
        [TestCase("finished")]
        [TestCase("preparing")]
        public void A_run_that_is_not_running_or_building_is_not_found(string state)
        {
            WriteClaim(RunId);
            WriteRun(RunId, state);

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
        }

        [Test]
        public void No_claim_finds_nothing()
        {
            WriteRun(RunId, "running");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out var why));
            Assert.IsNotNull(why);
        }

        [TestCase("not json")]
        [TestCase("[\"20261126T010203Z-0123abcd\"]")]
        [TestCase("{\"runId\": \"20261126T010203Z-0123abcd\"} trailing")]
        [TestCase("{\"runId\": \"20261126T010203Z-0123abcd\", \"runId\": \"20261126T010203Z-0123abcd\"}")]
        public void A_broken_claim_finds_nothing(string text)
        {
            File.WriteAllText(ClaimPath(), text);
            WriteRun(RunId, "running");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
        }

        [TestCase(null)]
        [TestCase("../../etc")]
        [TestCase("20261126T010203Z-0123ABCD")]
        public void A_claim_whose_run_ID_is_not_a_run_ID_finds_nothing(string runId)
        {
            WriteClaim(runId);
            WriteRun(RunId, "running");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
        }

        [Test]
        public void A_claim_for_another_Bridge_is_not_this_ones()
        {
            WriteClaim(RunId, "https://192.0.2.9:38443", Pin);
            WriteRun(RunId, "running");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
            Assert.IsNull(RunDiscovery.Find(_root, Url, "ffff", out _), "another certificate is another Bridge");
        }

        [Test]
        public void A_path_written_in_the_claim_is_not_used()
        {
            var elsewhere = Path.Combine(_root, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            WriteClaim(RunId, extra: ", \"runDir\": \"" + elsewhere + "\"");
            var directory = WriteRun(RunId, "running");

            Assert.AreEqual(directory, RunDiscovery.Find(_root, Url, Pin, out _)?.Directory);
        }

        [Test]
        public void A_run_json_with_another_run_ID_is_not_the_claimed_run()
        {
            WriteClaim(RunId);
            WriteRun(RunId, "running", recordRunId: "20261126T010203Z-ffffffff");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
        }

        [Test]
        public void A_symlinked_run_json_is_refused()
        {
            WriteClaim(RunId);
            var other = WriteRun("20261126T010203Z-ffffffff", "running", recordRunId: RunId);
            var directory = Path.Combine(_root, "runs", RunId);
            Directory.CreateDirectory(directory);
            SafeFileReaderTests.Symlink(Path.Combine(other, "run.json"), Path.Combine(directory, "run.json"));

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
        }

        [Test]
        public void A_symlinked_claim_is_refused()
        {
            var real = Path.Combine(_root, "claim.json");
            File.WriteAllText(real, "{\"runId\": \"" + RunId + "\"}");
            SafeFileReaderTests.Symlink(real, ClaimPath());
            WriteRun(RunId, "running");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
        }

        [Test]
        public void An_oversize_run_json_is_refused()
        {
            WriteClaim(RunId);
            var directory = WriteRun(RunId, "running");
            var path = Path.Combine(directory, "run.json");
            var text = File.ReadAllText(path);
            File.WriteAllText(path, text.Substring(0, text.Length - 1) + ", \"pad\": \"" + new string('x', RunFiles.MaxRunJsonBytes) + "\"}");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out var why));
            StringAssert.Contains("larger than", why);
        }

        [Test]
        public void An_oversize_claim_is_refused()
        {
            WriteClaim(RunId, extra: ", \"pad\": \"" + new string('x', RunFiles.MaxClaimBytes) + "\"");
            WriteRun(RunId, "running");

            Assert.IsNull(RunDiscovery.Find(_root, Url, Pin, out _));
        }

        [Test]
        public void No_pairing_finds_nothing()
        {
            Assert.IsNull(RunDiscovery.Find(_root, null, null, out _));
            Assert.IsNull(RunDiscovery.Find(_root, string.Empty, Pin, out _));
        }

        // --- 6.2: `waiting --json` -------------------------------------------------------------

        private static string WaitingJson(string valid = "true", string waitId = WaitId, string itemId = "grid-insert",
            string index = "2", string count = "3", string runId = RunId, string title = "\"Hand\"", string text = "\"Look at the line\"",
            string deadline = "\"2026-11-26T02:30:00Z\"", string items = null) =>
            "{\"runId\": \"" + runId + "\", \"waiting\": true, \"valid\": " + valid + ", \"why\": null, \"title\": " + title
            + ", \"items\": " + (items ?? "[{\"id\": \"a\", \"state\": \"answered\", \"answer\": \"ok\"}, {\"id\": \"grid-insert\", \"state\": \"waiting\", \"answer\": null}]")
            + ", \"waitId\": " + (waitId == null ? "null" : "\"" + waitId + "\"") + ", \"index\": " + index + ", \"count\": " + count
            + ", \"itemId\": " + (itemId == null ? "null" : "\"" + itemId + "\"") + ", \"text\": " + text
            + ", \"startedAtUtc\": \"2026-11-26T02:10:00Z\", \"deadlineUtc\": " + deadline + "}";

        [Test]
        public void A_valid_wait_is_shown_with_its_fields()
        {
            var report = WaitingReport.Parse(WaitingJson() + "\n");

            Assert.IsNotNull(report);
            Assert.IsTrue(report.ShowableFor(RunId));
            Assert.AreEqual(WaitId, report.WaitId);
            Assert.AreEqual(2, report.Index);
            Assert.AreEqual(3, report.Count);
            Assert.AreEqual("grid-insert", report.ItemId);
            Assert.AreEqual("Hand", report.Title);
            Assert.AreEqual("Look at the line", report.Text);
            Assert.AreEqual(new DateTime(2026, 11, 26, 2, 30, 0, DateTimeKind.Utc), report.DeadlineUtc);
            Assert.AreEqual(DateTimeKind.Utc, report.DeadlineUtc.Value.Kind);
            Assert.AreEqual("answered", report.ItemState("a"));
            Assert.AreEqual("waiting", report.ItemState("grid-insert"));
            Assert.IsNull(report.ItemState("missing"));
        }

        [Test]
        public void A_deadline_as_the_runner_writes_it_is_read()
        {
            // Python's isoformat: microseconds and +00:00, not Z (found on Windows, 2026-11-26).
            var report = WaitingReport.Parse(WaitingJson(deadline: "\"2026-11-26T02:30:00.331676+00:00\""));
            Assert.IsTrue(report.ShowableFor(RunId));
            Assert.AreEqual(new DateTime(2026, 11, 26, 2, 30, 0, DateTimeKind.Utc).AddTicks(3316760), report.DeadlineUtc);
            Assert.AreEqual(DateTimeKind.Utc, report.DeadlineUtc.Value.Kind);

            var tokyo = WaitingReport.Parse(WaitingJson(deadline: "\"2026-11-26T11:30:00+09:00\""));
            Assert.AreEqual(new DateTime(2026, 11, 26, 2, 30, 0, DateTimeKind.Utc), tokyo.DeadlineUtc);
        }

        [TestCase("false")]
        [TestCase("\"true\"")]
        [TestCase("1")]
        [TestCase("null")]
        public void Only_the_runners_valid_true_shows_a_wait(string valid)
        {
            var report = WaitingReport.Parse(WaitingJson(valid));

            Assert.IsNotNull(report);
            Assert.IsFalse(report.ShowableFor(RunId));
        }

        [Test]
        public void A_wait_of_another_run_is_not_shown()
        {
            Assert.IsFalse(WaitingReport.Parse(WaitingJson()).ShowableFor("20261126T010203Z-ffffffff"));
        }

        [Test]
        public void A_valid_wait_with_missing_or_malformed_fields_is_not_shown()
        {
            foreach (var json in new[]
                     {
                         WaitingJson(waitId: null), WaitingJson(waitId: "XYZ"), WaitingJson(itemId: null), WaitingJson(itemId: "Grid Insert"),
                         WaitingJson(index: "0"), WaitingJson(index: "4"), WaitingJson(count: "0"), WaitingJson(index: "\"2\""),
                         WaitingJson(title: "null"), WaitingJson(text: "null"), WaitingJson(text: "5"), WaitingJson(deadline: "null"),
                         WaitingJson(deadline: "\"tomorrow\""), WaitingJson(deadline: "\"2026-11-26T02:30:00\""),
                     })
            {
                var report = WaitingReport.Parse(json);
                Assert.IsNotNull(report, json);
                Assert.IsFalse(report.ShowableFor(RunId), json);
            }
        }

        [TestCase("")]
        [TestCase("\n\n")]
        [TestCase("not json")]
        [TestCase("[1, 2]")]
        [TestCase("{\"valid\": true} {\"valid\": true}")]
        public void Output_that_is_not_one_JSON_object_is_nothing(string stdout)
        {
            Assert.IsNull(WaitingReport.Parse(stdout));
        }

        [Test]
        public void Oversize_output_is_nothing()
        {
            var json = WaitingJson(text: "\"" + new string('x', WaitingReport.MaxOutputChars) + "\"");

            Assert.IsNull(WaitingReport.Parse(json));
        }

        [Test]
        public void Hidden_characters_stay_visible_even_if_the_runner_missed_them()
        {
            var report = WaitingReport.Parse(WaitingJson(title: "\"a\\u202eb\"", text: "\"line\\nnext\\u0007\\u2028<b>x</b>\""));

            Assert.AreEqual("a\\u202eb", report.Title);
            Assert.AreEqual("line\nnext\\u0007\\u2028<b>x</b>", report.Text);
            Assert.AreEqual("already \\u202e escaped", RunFiles.Visible("already \\u202e escaped"));
            Assert.AreEqual("\U0001F600 \\ud800", RunFiles.Visible("\U0001F600 \ud800"));
        }

        [Test]
        public void The_time_left_comes_from_the_runners_deadline()
        {
            var deadline = new DateTime(2026, 11, 26, 2, 30, 0, DateTimeKind.Utc);

            Assert.AreEqual("12 min left.", WaitingReport.TimeLeft(deadline, deadline.AddMinutes(-11.5)));
            Assert.AreEqual("Under a minute left.", WaitingReport.TimeLeft(deadline, deadline.AddSeconds(-20)));
            Assert.AreEqual("The deadline has passed.", WaitingReport.TimeLeft(deadline, deadline));
            Assert.AreEqual(" ", WaitingReport.TimeLeft(null, deadline));
        }

        // --- 6.2: `answer` ---------------------------------------------------------------------

        [Test]
        public void The_answer_command_line_is_fixed_and_the_memo_is_one_argument()
        {
            var memo = "-line \"seen\" 'twice'\n$(nope) --by cli";
            var arguments = RunWaitingCommands.AnswerArguments("/pkg/Tools~/vrcrt_runner.py", "/root/runs/" + RunId, WaitId, "grid-insert", "ng", memo);

            CollectionAssert.AreEqual(new[]
            {
                "-I", "/pkg/Tools~/vrcrt_runner.py", "answer", "--run", "/root/runs/" + RunId, "--wait-id", WaitId, "--item", "grid-insert",
                "ng", "--memo=" + memo, "--by", "unity",
            }, arguments);
        }

        [Test]
        public void The_waiting_command_line_is_fixed()
        {
            CollectionAssert.AreEqual(new[] { "-I", "/r.py", "waiting", "--run", "/root/runs/" + RunId, "--json" },
                RunWaitingCommands.WaitingArguments("/r.py", "/root/runs/" + RunId));
        }

        [TestCase("XYZ", "grid-insert", "ok")]
        [TestCase(WaitId, "--by", "ok")]
        [TestCase(WaitId, "grid-insert", "OK")]
        [TestCase(WaitId, "grid-insert", "pass")]
        [TestCase(null, "grid-insert", "ok")]
        public void An_answer_the_runner_would_refuse_is_not_sent(string waitId, string itemId, string answer)
        {
            Assert.Throws<ArgumentException>(() => RunWaitingCommands.AnswerArguments("/r.py", "/root/runs/" + RunId, waitId, itemId, answer, ""));
        }

        [Test]
        public void The_memo_keeps_what_the_runner_keeps()
        {
            Assert.AreEqual("a\nb\tc\U0001F600d", RunWaitingCommands.CleanMemo("a\r\nb\tc\u0000\u007f\u0085\U0001F600\ud800d"));
            Assert.AreEqual(string.Empty, RunWaitingCommands.CleanMemo(null));

            var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 2001));
            var cut = RunWaitingCommands.CleanMemo(emoji);
            Assert.AreEqual(2000, RunFiles.CodePoints(cut));
            Assert.AreEqual(4000, cut.Length);
            Assert.AreEqual(2000, RunFiles.CodePoints(RunWaitingView.LimitMemo(emoji)));
        }

        [Test]
        public void The_answer_command_line_parses_as_the_runner_parses_it()
        {
            var python = RunnerProcess.PythonPath();
            if (RunnerProcess.CheckPython(python) != null)
            {
                Assert.Ignore("no Python 3.9+ here");
            }

            // The runner's `answer` options (vrcrt_runner.py), parsed by argparse as there.
            const string script = "import argparse, json, sys\n"
                                  + "p = argparse.ArgumentParser(); s = p.add_subparsers(dest='command', required=True)\n"
                                  + "a = s.add_parser('answer'); a.add_argument('--run', required=True); a.add_argument('--wait-id', required=True)\n"
                                  + "a.add_argument('--item', required=True); a.add_argument('answer', choices=('ok', 'ng', 'skip'))\n"
                                  + "a.add_argument('--memo', default=''); a.add_argument('--by', choices=('unity', 'cli'), default='cli')\n"
                                  + "n = p.parse_args(sys.argv[1:]); print(json.dumps([n.run, n.wait_id, n.item, n.answer, n.memo, n.by]))\n";
            foreach (var memo in new[] { "-starts with a dash", "--by cli", "", "line\nnext 'q' \"d\" \\ $HOME" })
            {
                var arguments = RunWaitingCommands.AnswerArguments("runner", "/r/runs/" + RunId, WaitId, "a", "skip", memo).ToList();
                arguments.RemoveRange(0, 2); // "-I", runner: the script stands in for the runner
                arguments.InsertRange(0, new[] { "-I", "-c", script });
                var result = RunnerProcess.Run(python, arguments, RunnerProcess.ChildEnvironment(null, null), TimeSpan.FromSeconds(10));

                Assert.AreEqual(0, result.ExitCode, result.Stderr);
                CollectionAssert.AreEqual(new[] { "/r/runs/" + RunId, WaitId, "a", "skip", memo, "unity" },
                    JArray.Parse(result.Stdout.Trim()).ToObject<string[]>(), memo);
            }
        }

        [Test]
        public void What_the_window_says_after_answer()
        {
            Assert.AreEqual("Sent. Waiting for the runner to take it.", RunWaitingCommands.DescribeAnswer(new RunnerProcess.Result { ExitCode = 0 }));
            Assert.AreEqual("Not sent: vrcrt_runner: not answered: alreadyAnswered (item a has an answer already)",
                RunWaitingCommands.DescribeAnswer(new RunnerProcess.Result
                {
                    ExitCode = 1, Stderr = "warning\nvrcrt_runner: not answered: alreadyAnswered (item a has an answer already)\n",
                }));
            StringAssert.StartsWith("The runner did not answer", RunWaitingCommands.DescribeAnswer(new RunnerProcess.Result { TimedOut = true, ExitCode = -1 }));
            Assert.AreEqual("The runner could not be started.", RunWaitingCommands.DescribeAnswer(null));
        }

        // --- 6.2: the watcher's states (without processes) -------------------------------------

        private static readonly string Key = RemoteRunController.ClaimKey(Url, Pin);

        private static FoundRun Found(string directory = null) => new FoundRun
        {
            RunId = RunId, Directory = directory ?? "/r/runs/" + RunId, State = "running", Procedure = "manual-steps", ClaimKey = Key,
        };

        private static RunWaitingWatcher Watcher()
        {
            var watcher = new RunWaitingWatcher();
            watcher.SetPairing(Url, Pin);
            return watcher;
        }

        [Test]
        public void The_panel_shows_only_a_wait_the_runner_calls_valid()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson(valid: "false")));
            Assert.IsNull(watcher.Snapshot().Shown);

            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            var snapshot = watcher.Snapshot();
            Assert.AreEqual(WaitId, snapshot.Shown?.WaitId);
            Assert.IsTrue(snapshot.CanAnswer);

            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson(valid: "false")));
            Assert.IsNull(watcher.Snapshot().Shown, "closed with nothing tried from here");
        }

        [Test]
        public void Sent_then_taken_and_never_offered_again_for_that_wait()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            watcher.Answered(WaitId, new RunnerProcess.Result { ExitCode = 0 });

            var sent = watcher.Snapshot();
            Assert.AreEqual("Sent. Waiting for the runner to take it.", sent.Status);
            Assert.IsFalse(sent.CanAnswer);

            // Still open for a moment: not offered again.
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            Assert.IsFalse(watcher.Snapshot().CanAnswer);

            const string answered = "[{\"id\": \"grid-insert\", \"state\": \"answered\", \"answer\": \"ng\"}]";
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson(valid: "false", items: answered)));
            var taken = watcher.Snapshot();
            Assert.AreEqual("Taken.", taken.Status);
            Assert.AreEqual(WaitId, taken.Shown?.WaitId);
            Assert.IsFalse(taken.CanAnswer);

            const string next = "fedcba9876543210fedcba9876543210";
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson(waitId: next, itemId: "c", index: "3")));
            var nextItem = watcher.Snapshot();
            Assert.AreEqual(next, nextItem.Shown?.WaitId);
            Assert.IsTrue(nextItem.CanAnswer);
            Assert.IsNull(nextItem.Status);
        }

        [Test]
        public void A_refusal_shows_the_runners_line_and_stays_until_the_next_wait()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            watcher.Answered(WaitId, new RunnerProcess.Result { ExitCode = 1, Stderr = "vrcrt_runner: not answered: notWaiting (the run is finished)\n" });

            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson(valid: "false")));
            var snapshot = watcher.Snapshot();
            Assert.AreEqual("Not sent: vrcrt_runner: not answered: notWaiting (the run is finished)", snapshot.Status);
            Assert.IsNotNull(snapshot.Shown);
            Assert.IsFalse(snapshot.CanAnswer);

            watcher.Take(Key, null, null);
            Assert.IsNull(watcher.Snapshot().Shown, "the run is gone");
        }

        [Test]
        public void A_timed_out_answer_is_not_offered_again()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            watcher.Answered(WaitId, new RunnerProcess.Result { TimedOut = true, ExitCode = -1 });
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));

            Assert.IsFalse(watcher.Snapshot().CanAnswer);
        }

        [Test]
        public void A_refused_wait_is_not_offered_again_while_it_stays_open()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            watcher.Answered(WaitId, new RunnerProcess.Result
            {
                ExitCode = 1, Stderr = "vrcrt_runner: not answered: alreadyAnswered (item grid-insert has an answer already)\n",
            });

            // The CLI answered first; the runner has not closed the wait yet.
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            var snapshot = watcher.Snapshot();
            Assert.IsFalse(snapshot.CanAnswer);
            StringAssert.Contains("alreadyAnswered", snapshot.Status);
            Assert.IsNull(watcher.PrepareAnswer(WaitId, "ok", "", Url, Pin, "/r.py"));
        }

        [Test]
        public void An_answer_goes_only_to_a_run_found_for_the_current_pairing()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));

            Assert.IsNull(watcher.PrepareAnswer(WaitId, "ok", "", "https://192.0.2.9:38443", Pin, "/r.py"), "another Bridge's settings");
            Assert.IsNull(watcher.PrepareAnswer(WaitId, "ok", "", Url, "ffff", "/r.py"), "another certificate");

            var arguments = watcher.PrepareAnswer(WaitId, "ok", "m", Url, Pin, "/r.py");
            Assert.IsNotNull(arguments);
            Assert.AreEqual("/r/runs/" + RunId, arguments[4]);
            Assert.IsTrue(watcher.Snapshot().Answering);
            Assert.IsNull(watcher.PrepareAnswer(WaitId, "ok", "m", Url, Pin, "/r.py"), "one in flight");
        }

        [Test]
        public void A_pairing_change_drops_the_previous_Bridges_run_and_its_late_polls()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            Assert.IsNotNull(watcher.Snapshot().Shown);

            watcher.SetPairing("https://192.0.2.9:38443", Pin);
            Assert.IsNull(watcher.Snapshot().Shown);
            Assert.IsNull(watcher.Snapshot().Run);

            // A poll started for the old pairing ends after the change.
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            Assert.IsNull(watcher.Snapshot().Shown);
            Assert.IsNull(watcher.Snapshot().Run);
            Assert.IsNull(watcher.PrepareAnswer(WaitId, "ok", "", Url, Pin, "/r.py"));
            Assert.IsNull(watcher.PrepareAnswer(WaitId, "ok", "", "https://192.0.2.9:38443", Pin, "/r.py"));

            // A run found under another key than the poll's is not taken either.
            var newKey = RemoteRunController.ClaimKey("https://192.0.2.9:38443", Pin);
            watcher.Take(newKey, Found(), WaitingReport.Parse(WaitingJson()));
            Assert.IsNull(watcher.Snapshot().Shown);
        }

        [Test]
        public void An_answer_taken_as_the_run_ends_is_still_reported()
        {
            var directory = WriteRun(RunId, "finished");
            var watcher = Watcher();
            watcher.Take(Key, Found(directory), WaitingReport.Parse(WaitingJson()));
            watcher.Answered(WaitId, new RunnerProcess.Result { ExitCode = 0 });

            var pending = new RunWaitingWatcher.PendingAnswer { RunId = RunId, Directory = directory, WaitId = WaitId, ItemId = "grid-insert" };
            Assert.IsFalse(RunWaitingWatcher.AnsweredInRunJson(pending), "run.json without the item");
            File.WriteAllText(Path.Combine(directory, "run.json"),
                "{\"schema\": \"vrcrt.run\", \"runId\": \"" + RunId + "\", \"state\": \"finished\", \"manualSteps\": {\"items\": ["
                + "{\"index\": 2, \"id\": \"grid-insert\", \"state\": \"answered\", \"answer\": \"ok\", \"waitId\": \"" + WaitId + "\"}]}}");
            Assert.IsTrue(RunWaitingWatcher.AnsweredInRunJson(pending));
            Assert.IsFalse(RunWaitingWatcher.AnsweredInRunJson(new RunWaitingWatcher.PendingAnswer
            {
                RunId = RunId, Directory = directory, WaitId = "fedcba9876543210fedcba9876543210", ItemId = "grid-insert",
            }), "another wait of the same item");

            // The run finished before the next poll: discovery finds nothing.
            watcher.Take(Key, null, null, true);
            var snapshot = watcher.Snapshot();
            Assert.IsNull(snapshot.Shown);
            Assert.AreEqual("Run " + RunId + ", item grid-insert: Taken.", snapshot.EndedNote);
        }

        [Test]
        public void An_answer_not_seen_taken_when_the_run_ends_gets_a_neutral_line()
        {
            var watcher = Watcher();
            watcher.Take(Key, Found(), WaitingReport.Parse(WaitingJson()));
            watcher.Answered(WaitId, new RunnerProcess.Result { ExitCode = 0 });
            watcher.Take(Key, null, null, false);

            Assert.AreEqual("Run " + RunId + ", item grid-insert: The run has ended; check its summary.", watcher.Snapshot().EndedNote);
        }

        // --- 6.3: procedures -------------------------------------------------------------------

        [Test]
        public void The_window_offers_deploy_collect_and_manual_steps()
        {
            CollectionAssert.AreEqual(new[] { "deploy-collect", "manual-steps" }, RunProcedures.Offered());
            CollectionAssert.Contains(RunProcedures.Validated, "manual-steps");
        }

        [Test]
        public void The_validated_list_mirrors_the_runners()
        {
            var runner = RunnerProcess.RunnerPath();
            if (runner == null)
            {
                Assert.Ignore("the package is not installed through the package manager here");
            }

            var match = Regex.Match(File.ReadAllText(runner), @"^VALIDATED_PROCEDURES = \{([^}]*)\}", RegexOptions.Multiline);
            Assert.IsTrue(match.Success, "VALIDATED_PROCEDURES not found in the runner");
            var names = Regex.Matches(match.Groups[1].Value, "\"([a-z-]+)\"\\s*:").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            CollectionAssert.AreEquivalent(names, RunProcedures.Validated);
        }

        [Test]
        public void Prepare_gets_the_manual_steps_arguments()
        {
            CollectionAssert.IsEmpty(RunProcedures.PrepareArguments("deploy-collect", null, null, null));
            CollectionAssert.AreEqual(new[] { "--procedure", "manual-steps", "--checklist", "/x/c.json" },
                RunProcedures.PrepareArguments("manual-steps", "/x/c.json", null, null));
            CollectionAssert.AreEqual(new[] { "--procedure", "manual-steps", "--checklist", "/x/c.json", "--leave-rejoin-after", "b", "--target-slot", "1" },
                RunProcedures.PrepareArguments("manual-steps", "/x/c.json", "b", 1));
            Assert.Throws<ArgumentException>(() => RunProcedures.PrepareArguments("manual-steps", "c.json", null, null));
            Assert.Throws<ArgumentException>(() => RunProcedures.PrepareArguments("manual-steps", "/x/c.json", "b", null));
            Assert.Throws<ArgumentException>(() => RunProcedures.PrepareArguments("manual-steps", "/x/c.json", "B b", 1));
            Assert.Throws<ArgumentException>(() => RunProcedures.PrepareArguments("leave-rejoin", null, null, null));
        }

        private string WriteChecklist(string json)
        {
            var path = Path.Combine(_root, "checklist-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, json);
            return path;
        }

        private static string Checklist(string items, string extra = "") =>
            "{\"schema\": \"vrcrt.checklist\", \"schemaVersion\": 1, \"title\": \"Hand\", \"items\": " + items + extra + "}";

        [Test]
        public void The_checklist_ids_are_read_for_the_popup()
        {
            var path = WriteChecklist(Checklist("[{\"id\": \"grid-insert\", \"text\": \"a\"}, {\"id\": \"desktop-click\", \"text\": \"b\"}]"));

            CollectionAssert.AreEqual(new[] { "grid-insert", "desktop-click" }, RunProcedures.ChecklistItemIds(path, out var problem), problem);
        }

        [Test]
        public void A_checklist_not_of_the_contracts_shape_gives_no_ids()
        {
            var twentyOne = "[" + string.Join(", ", Enumerable.Range(0, 21).Select(i => "{\"id\": \"i" + i + "\", \"text\": \"t\"}")) + "]";
            foreach (var json in new[]
                     {
                         Checklist("[{\"id\": \"a\", \"text\": \"t\"}]", ", \"extra\": 1"),
                         Checklist("[{\"id\": \"a\", \"text\": \"t\"}, {\"id\": \"a\", \"text\": \"u\"}]"),
                         Checklist("[{\"id\": \"A\", \"text\": \"t\"}]"),
                         Checklist("[{\"id\": \"a\", \"text\": \"\"}]"),
                         Checklist("[{\"id\": \"a\", \"text\": \"t\", \"slot\": 1}]"),
                         Checklist("[\"a\"]"),
                         Checklist("[]"),
                         Checklist(twentyOne),
                         "{\"schema\": \"vrcrt.checklist\", \"schemaVersion\": 2, \"title\": \"Hand\", \"items\": [{\"id\": \"a\", \"text\": \"t\"}]}",
                     })
            {
                Assert.IsNull(RunProcedures.ChecklistItemIds(WriteChecklist(json), out _), json);
            }

            var oversize = WriteChecklist(Checklist("[{\"id\": \"a\", \"text\": \"" + new string('x', RunFiles.MaxChecklistBytes) + "\"}]"));
            Assert.IsNull(RunProcedures.ChecklistItemIds(oversize, out _));
        }

        [Test]
        public void A_symlinked_checklist_gives_no_ids()
        {
            var real = WriteChecklist(Checklist("[{\"id\": \"a\", \"text\": \"t\"}]"));
            var link = Path.Combine(_root, "link.json");
            SafeFileReaderTests.Symlink(real, link);

            Assert.IsNull(RunProcedures.ChecklistItemIds(link, out _));
        }

        // --- 6.4: the summary ------------------------------------------------------------------

        private static JObject ManualRecord(string items, string result = "FAIL") => JObject.Parse(
            "{\"schema\": \"vrcrt.run\", \"runId\": \"" + RunId + "\", \"state\": \"finished\", \"result\": \"" + result + "\", \"reason\": \"humanNg\", "
            + "\"procedure\": {\"name\": \"manual-steps\"}, "
            + "\"results\": {\"deploy\": \"PASS\", \"evidence\": \"PASS\", \"testVerdict\": \"notJudged\", \"manualSteps\": \"FAIL\"}, "
            + "\"manualSteps\": {\"title\": \"Hand\", \"items\": " + items + "}}");

        [Test]
        public void The_human_answers_are_counted_from_run_json()
        {
            var record = ManualRecord("[{\"id\": \"a\", \"state\": \"answered\", \"answer\": \"ok\"}, "
                                      + "{\"id\": \"b\", \"state\": \"answered\", \"answer\": \"ok\"}, "
                                      + "{\"id\": \"c\", \"state\": \"answered\", \"answer\": \"skip\"}, "
                                      + "{\"id\": \"d\", \"state\": \"answered\", \"answer\": \"ng\"}, "
                                      + "{\"id\": \"e\", \"state\": \"unanswered\"}, {\"id\": \"f\", \"state\": \"notAsked\"}, "
                                      + "{\"id\": \"g\", \"state\": \"interrupted\", \"answer\": \"ok\"}]");

            Assert.AreEqual("Human answers: ok 2, ng 1, skip 1 (not judged by the tool)", ManualStepsSummary.Describe(record));
        }

        [Test]
        public void The_summary_of_a_manual_steps_run_has_the_answers_and_manualSteps()
        {
            var summary = RunSummary.FromRecord(ManualRecord("[{\"id\": \"a\", \"state\": \"answered\", \"answer\": \"ng\"}]"), null);

            Assert.AreEqual("FAIL", summary.Results["manualSteps"]);
            Assert.AreEqual("Human answers: ok 0, ng 1, skip 0 (not judged by the tool)", summary.HumanAnswers);
            Assert.AreEqual("FAIL: humanNg\nHuman answers: ok 0, ng 1, skip 0 (not judged by the tool)", summary.Describe());
        }

        [Test]
        public void A_deploy_collect_summary_has_no_human_answers()
        {
            var summary = RunSummary.FromRecord(JObject.Parse(
                "{\"schema\": \"vrcrt.run\", \"state\": \"finished\", \"result\": \"PASS\", \"procedure\": {\"name\": \"deploy-collect\"}, \"results\": {}}"), null);

            Assert.IsNull(summary.HumanAnswers);
            Assert.IsNull(ManualStepsSummary.Describe(JObject.Parse("{\"results\": {}}")));
            StringAssert.DoesNotContain("Human answers", summary.Describe());
        }

        [Test]
        public void The_run_summary_refuses_a_symlinked_run_json()
        {
            var other = WriteRun("20261126T010203Z-ffffffff", "finished");
            var directory = Path.Combine(_root, "runs", RunId);
            Directory.CreateDirectory(directory);
            SafeFileReaderTests.Symlink(Path.Combine(other, "run.json"), Path.Combine(directory, "run.json"));

            Assert.IsNull(RunSummary.Read(directory));
            Assert.AreEqual("finished", RunSummary.Read(other)?.State);
        }

        [Test]
        public void Dates_in_run_json_stay_as_written()
        {
            var value = RunFiles.ParseObject("{\"deadlineUtc\": \"2026-11-26T03:00:00Z\"}");

            Assert.AreEqual(JTokenType.String, value["deadlineUtc"].Type);
            Assert.AreEqual("2026-11-26T03:00:00Z", (string)value["deadlineUtc"]);
        }

        // --- labels ----------------------------------------------------------------------------

        [Test]
        public void Run_text_is_drawn_without_rich_text()
        {
            var plain = RunWaitingView.PlainStyle(new GUIStyle { richText = true, wordWrap = false });

            Assert.IsFalse(plain.richText);
            Assert.IsTrue(plain.wordWrap);
            // (RunWaitingView's own styles are PlainStyle of EditorStyles, which exist only inside OnGUI.)
        }

        [Test]
        public void The_panel_names_the_run_and_the_item()
        {
            var report = WaitingReport.Parse(WaitingJson());

            Assert.AreEqual("Run " + RunId + " is waiting for you", RunWaitingView.Heading(report));
            Assert.AreEqual("Item 2 of 3: grid-insert", RunWaitingView.ItemLine(report));
        }
    }
}

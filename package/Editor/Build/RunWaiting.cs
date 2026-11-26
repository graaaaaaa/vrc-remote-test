using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VRCRemoteTest
{
    /// <summary>
    /// The runner's files and commands as the window reads them (r3-contract.md §6). Everything is
    /// read with <see cref="SafeFileReader"/> under a cap; paths are built here from the default run
    /// root and a checked run ID, never taken from a file.
    /// </summary>
    internal static class RunFiles
    {
        internal const int MaxClaimBytes = 64 * 1024;
        internal const int MaxRunJsonBytes = 1024 * 1024;
        internal const int MaxChecklistBytes = 64 * 1024;
        internal const int MaxProgressTailBytes = 8 * 1024;

        private const int MaxDepth = 64;
        private static readonly Regex RunIdPattern = new Regex(@"^\d{8}T\d{6}Z-[0-9a-f]{8}\z", RegexOptions.Compiled);
        private static readonly Regex WaitIdPattern = new Regex(@"^[0-9a-f]{32}\z", RegexOptions.Compiled);
        private static readonly Regex ItemIdPattern = new Regex(@"^[a-z0-9][a-z0-9_-]{0,31}\z", RegexOptions.Compiled);

        internal static bool IsRunId(string value) => value != null && RunIdPattern.IsMatch(value);

        internal static bool IsWaitId(string value) => value != null && WaitIdPattern.IsMatch(value);

        internal static bool IsItemId(string value) => value != null && ItemIdPattern.IsMatch(value);

        /// <summary>
        /// One JSON object, or null. Dates stay strings (Json.NET would turn "...Z" into a DateTime
        /// otherwise) and duplicate keys are refused.
        /// </summary>
        internal static JObject ParseObject(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                using (var reader = new JsonTextReader(new StringReader(json))
                       {
                           DateParseHandling = DateParseHandling.None,
                           FloatParseHandling = FloatParseHandling.Double,
                           MaxDepth = MaxDepth,
                       })
                {
                    var value = JToken.ReadFrom(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        CommentHandling = CommentHandling.Ignore,
                    });
                    if (reader.Read())
                    {
                        return null; // something after the object
                    }

                    return value as JObject;
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is OverflowException
                                       || ex is ArgumentException)
            {
                return null;
            }
        }

        internal static JObject ReadObject(string path, int maxBytes, out string problem)
        {
            var text = SafeFileReader.ReadText(path, maxBytes, out problem);
            if (text == null)
            {
                return null;
            }

            var value = ParseObject(text);
            if (value == null)
            {
                problem = Path.GetFileName(path) + " is not a JSON object";
            }

            return value;
        }

        internal static string String(JToken token) => token != null && token.Type == JTokenType.String ? (string)token : null;

        /// <summary>
        /// Display only (r3-contract.md §1): what could hide or reorder text becomes \uXXXX — C0
        /// controls but the newline, DEL, C1, U+2028/2029, the bidirectional controls, and a lone
        /// surrogate. The runner already does this; doing it again changes nothing.
        /// </summary>
        internal static string Visible(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (IsHidden(c) || char.IsSurrogate(c))
                {
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        private static bool IsHidden(char c) =>
            (c < ' ' && c != '\n') || (c >= '\u007f' && c <= '\u009f') || c == '\u2028' || c == '\u2029'
            || (c >= '\u202a' && c <= '\u202e') || (c >= '\u2066' && c <= '\u2069');

        /// <summary>Number of Unicode code points (a surrogate pair counts once), as the runner counts.</summary>
        internal static int CodePoints(string text)
        {
            var count = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }

                count++;
            }

            return count;
        }
    }

    /// <summary>A run of the default run root that holds this Bridge's claim and is running or building.</summary>
    internal sealed class FoundRun
    {
        internal string RunId;
        internal string Directory;
        internal string State;
        internal string Procedure;

        /// <summary>The claim key it was found under: the pairing (Bridge URL and pin) it belongs to.</summary>
        internal string ClaimKey;
    }

    /// <summary>
    /// r3-contract.md §6.1: the claim for this Bridge (key from the URL and the certificate pin, as
    /// the runner's Claim computes it) names a run ID; the run folder is built from the root and
    /// that ID, and its run.json must have the same ID and be running or building. Read only.
    /// Works for a run this Editor started, one from before a restart, and one started from the CLI.
    /// </summary>
    internal static class RunDiscovery
    {
        internal static FoundRun Find(string root, string bridgeUrl, string fingerprint, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(bridgeUrl))
            {
                why = "no pairing";
                return null;
            }

            var claim = RunFiles.ReadObject(Path.Combine(root, "locks", RemoteRunController.ClaimKey(bridgeUrl, fingerprint) + ".json"),
                RunFiles.MaxClaimBytes, out why);
            if (claim == null)
            {
                return null;
            }

            var runId = RunFiles.String(claim["runId"]);
            if (!RunFiles.IsRunId(runId))
            {
                why = "the claim names no run";
                return null;
            }

            var directory = Path.Combine(root, "runs", runId);
            var record = RunFiles.ReadObject(Path.Combine(directory, "run.json"), RunFiles.MaxRunJsonBytes, out why);
            if (record == null)
            {
                return null;
            }

            var state = RunFiles.String(record["state"]);
            if (RunFiles.String(record["schema"]) != "vrcrt.run" || RunFiles.String(record["runId"]) != runId)
            {
                why = "run.json is not the claimed run's";
                return null;
            }

            if (state != "running" && state != "building")
            {
                why = "the claimed run is " + (state ?? "in no state");
                return null;
            }

            return new FoundRun
            {
                ClaimKey = RemoteRunController.ClaimKey(bridgeUrl, fingerprint),
                RunId = runId,
                Directory = directory,
                State = state,
                Procedure = RunFiles.String((record["procedure"] as JObject)?["name"]),
            };
        }
    }

    /// <summary>One line of `runner waiting --run DIR --json` (r3-contract.md §2.2). Whether the wait is valid is the runner's answer.</summary>
    internal sealed class WaitingReport
    {
        internal const int MaxOutputChars = 64 * 1024;
        private const int MaxItems = 100;

        internal sealed class Item
        {
            internal string Id;
            internal string State;
            internal string Answer;
        }

        internal string RunId;
        internal bool Waiting;

        /// <summary>The runner's <c>valid</c>.</summary>
        internal bool Valid;

        internal string WaitId;
        internal int Index;
        internal int Count;
        internal string ItemId;
        internal string Title;
        internal string Text;
        internal DateTime? DeadlineUtc;
        internal readonly List<Item> Items = new List<Item>();

        /// <summary>
        /// Valid as the runner says, for <paramref name="runId"/>, and with everything the panel
        /// needs well formed: otherwise nothing is shown (Unity never shows a wait the runner did
        /// not call valid, and never one it cannot describe).
        /// </summary>
        internal bool ShowableFor(string runId) =>
            Valid && Waiting && RunId == runId && RunFiles.IsWaitId(WaitId) && RunFiles.IsItemId(ItemId)
            && Count >= 1 && Index >= 1 && Index <= Count && Title != null && Text != null && DeadlineUtc != null;

        internal string ItemState(string itemId)
        {
            foreach (var item in Items)
            {
                if (item.Id == itemId)
                {
                    return item.State;
                }
            }

            return null;
        }

        /// <summary>The first JSON line of the output, or null when there is none, it is too long, or it is not an object.</summary>
        internal static WaitingReport Parse(string stdout)
        {
            if (string.IsNullOrEmpty(stdout) || stdout.Length > MaxOutputChars)
            {
                return null;
            }

            JObject value = null;
            foreach (var line in stdout.Split('\n'))
            {
                if (line.Trim().Length > 0)
                {
                    value = RunFiles.ParseObject(line.Trim());
                    break;
                }
            }

            if (value == null)
            {
                return null;
            }

            var report = new WaitingReport
            {
                RunId = RunFiles.String(value["runId"]),
                Waiting = value["waiting"]?.Type == JTokenType.Boolean && (bool)value["waiting"],
                Valid = value["valid"]?.Type == JTokenType.Boolean && (bool)value["valid"],
                WaitId = RunFiles.String(value["waitId"]),
                Index = Int(value["index"]),
                Count = Int(value["count"]),
                ItemId = RunFiles.String(value["itemId"]),
                Title = Displayed(value["title"]),
                Text = Displayed(value["text"]),
                DeadlineUtc = Utc(RunFiles.String(value["deadlineUtc"])),
            };
            if (value["items"] is JArray items)
            {
                foreach (var entry in items)
                {
                    if (report.Items.Count >= MaxItems)
                    {
                        break;
                    }

                    if (entry is JObject item && RunFiles.IsItemId(RunFiles.String(item["id"])))
                    {
                        report.Items.Add(new Item
                        {
                            Id = RunFiles.String(item["id"]),
                            State = RunFiles.String(item["state"]),
                            Answer = RunFiles.String(item["answer"]),
                        });
                    }
                }
            }

            return report;
        }

        /// <summary>"12 min left", "under a minute left" or "the deadline has passed", from the runner's deadline.</summary>
        internal static string TimeLeft(DateTime? deadlineUtc, DateTime nowUtc)
        {
            if (deadlineUtc == null)
            {
                return " ";
            }

            var left = deadlineUtc.Value - nowUtc;
            if (left <= TimeSpan.Zero)
            {
                return "The deadline has passed.";
            }

            return left < TimeSpan.FromMinutes(1)
                ? "Under a minute left."
                : ((int)Math.Ceiling(left.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min left.";
        }

        private static string Displayed(JToken token)
        {
            var text = RunFiles.String(token);
            return text == null ? null : RunFiles.Visible(text);
        }

        private static int Int(JToken token) =>
            token != null && token.Type == JTokenType.Integer && (long)token >= 0 && (long)token <= 1000 ? (int)(long)token : 0;

        /// An ISO 8601 time with its zone stated: "...Z" or "...+00:00" (Python's isoformat, which
        /// the runner writes). A time without a zone is refused rather than guessed.
        internal static DateTime? Utc(string text)
        {
            if (text == null || !System.Text.RegularExpressions.Regex.IsMatch(text, @"(Z|[+-]\d{2}:\d{2})\z"))
            {
                return null;
            }

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                ? value.UtcDateTime
                : (DateTime?)null;
        }
    }

    /// <summary>The fixed command lines for `waiting` and `answer` (r3-contract.md §2.2, §2.3, §6.2).</summary>
    internal static class RunWaitingCommands
    {
        internal const int MaxMemoChars = 2000;
        internal static readonly TimeSpan WaitingTimeout = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

        internal static readonly string[] Answers = { "ok", "ng", "skip" };

        internal static string[] WaitingArguments(string runner, string runDirectory) =>
            new[] { "-I", runner, "waiting", "--run", runDirectory, "--json" };

        /// <summary>
        /// `answer --run DIR --wait-id ID --item ID ok|ng|skip --memo=MEMO --by unity`. The memo is
        /// one argument joined to its option, so a memo that starts with "-" is not taken for an
        /// option. Throws ArgumentException for an ID or answer the runner would refuse anyway.
        /// </summary>
        internal static string[] AnswerArguments(string runner, string runDirectory, string waitId, string itemId, string answer, string memo)
        {
            if (string.IsNullOrEmpty(runner) || string.IsNullOrEmpty(runDirectory))
            {
                throw new ArgumentException("no runner or run folder");
            }

            if (!RunFiles.IsWaitId(waitId) || !RunFiles.IsItemId(itemId) || Array.IndexOf(Answers, answer) < 0)
            {
                throw new ArgumentException("not a wait, item or answer the runner prints");
            }

            return new[]
            {
                "-I", runner, "answer", "--run", runDirectory, "--wait-id", waitId, "--item", itemId, answer,
                "--memo=" + CleanMemo(memo), "--by", "unity",
            };
        }

        /// <summary>
        /// What the runner keeps of a memo: control characters but the newline and the tab are
        /// dropped, as are lone surrogates (the runner would refuse them), and it is cut at 2000
        /// code points.
        /// </summary>
        internal static string CleanMemo(string memo)
        {
            if (string.IsNullOrEmpty(memo))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(Math.Min(memo.Length, MaxMemoChars * 2));
            var count = 0;
            for (var i = 0; i < memo.Length && count < MaxMemoChars; i++)
            {
                var c = memo[i];
                if (char.IsHighSurrogate(c) && i + 1 < memo.Length && char.IsLowSurrogate(memo[i + 1]))
                {
                    builder.Append(c).Append(memo[i + 1]);
                    i++;
                    count++;
                    continue;
                }

                if (char.IsSurrogate(c) || ((c < ' ' || (c >= '\u007f' && c <= '\u009f')) && c != '\n' && c != '\t'))
                {
                    continue;
                }

                builder.Append(c);
                count++;
            }

            return builder.ToString();
        }

        /// <summary>The window's line after `answer` returned.</summary>
        internal static string DescribeAnswer(RunnerProcess.Result result)
        {
            if (result == null)
            {
                return "The runner could not be started.";
            }

            if (result.TimedOut)
            {
                return "The runner did not answer within 30 s. Nothing is sent again; check the run before answering again.";
            }

            if (result.ExitCode == 0)
            {
                return "Sent. Waiting for the runner to take it.";
            }

            var line = LastLine(result.Stderr);
            return "Not sent: " + (line.Length == 0 ? "the runner refused (exit code " + result.ExitCode + ")" : BridgeMessages.Sanitize(line, 300));
        }

        internal static string LastLine(string text)
        {
            var lines = (text ?? string.Empty).Trim().Split('\n');
            return lines[lines.Length - 1].Trim();
        }
    }

    /// <summary>
    /// What the waiting panel shows, taken at a Layout event so the Layout and the event after it
    /// draw the same controls.
    /// </summary>
    internal sealed class WaitingSnapshot
    {
        internal FoundRun Run;

        /// <summary>The wait the panel shows (valid now, or the one just answered), or null for no panel.</summary>
        internal WaitingReport Shown;

        internal bool CanAnswer;
        internal bool Answering;
        internal string Status;

        /// <summary>After the run ended with an answer from here pending: "Taken." or a neutral line; else null.</summary>
        internal string EndedNote;
    }

    /// <summary>
    /// r3-contract.md §6.1-6.2 for the window: once a second at most, on a worker thread, finds
    /// this Bridge's run and, while it is a running manual-steps run, asks `runner waiting --json`;
    /// answers with `runner answer` on a worker thread too. Never decides by itself whether a wait
    /// is valid, never answers twice by itself, and writes nothing but through the runner.
    /// </summary>
    internal sealed class RunWaitingWatcher
    {
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        internal const string TakenStatus = "Taken.";
        internal const string EndedStatus = "The run has ended; check its summary.";

        private readonly object _gate = new object();
        private bool _polling;
        private DateTime _lastPollEndedUtc = DateTime.MinValue;
        private string _runner;
        private bool _runnerLooked;

        // The claim key of the pairing in the settings now. A poll made for another key is dropped,
        // and an answer is sent only to a run found under this key.
        private string _pairingKey;

        private FoundRun _run;
        private WaitingReport _current;
        private WaitingReport _shown;
        private FoundRun _shownRun;
        private bool _answering;
        private string _status;

        // An answer was published (exit 0) or may have been (timed out): "Taken." is looked for.
        private string _sentWaitId;

        // Any answer attempt for this wait (sent, refused or timed out): never offered again.
        private string _triedWaitId;
        private bool _taken;
        private string _endedNote;
        private int _version;

        /// <summary>Where an answer sent from here is still waiting to be seen taken (read when the run has gone).</summary>
        internal sealed class PendingAnswer
        {
            internal string RunId;
            internal string Directory;
            internal string WaitId;
            internal string ItemId;
        }

        /// <summary>Changes whenever something the panel shows changed, so the window repaints only then.</summary>
        internal int Version
        {
            get
            {
                lock (_gate)
                {
                    return _version;
                }
            }
        }

        /// <summary>
        /// The pairing in the settings (null URL: none). A change drops everything found for the
        /// previous one, so nothing found for one Bridge is shown or answered under another.
        /// </summary>
        internal void SetPairing(string bridgeUrl, string fingerprint)
        {
            var key = string.IsNullOrEmpty(bridgeUrl) ? null : RemoteRunController.ClaimKey(bridgeUrl, fingerprint);
            lock (_gate)
            {
                if (key == _pairingKey)
                {
                    return;
                }

                _pairingKey = key;
                _run = null;
                _current = null;
                _endedNote = null;
                Forget();
                _version++;
            }
        }

        /// <summary>From the window's update, on the main thread: starts one poll when the last one ended a second ago or more.</summary>
        internal void Tick(string root, string bridgeUrl, string fingerprint)
        {
            SetPairing(bridgeUrl, fingerprint);
            if (string.IsNullOrEmpty(bridgeUrl))
            {
                return;
            }

            var key = RemoteRunController.ClaimKey(bridgeUrl, fingerprint);
            PendingAnswer pending;
            lock (_gate)
            {
                if (_polling || DateTime.UtcNow - _lastPollEndedUtc < PollInterval)
                {
                    return;
                }

                _polling = true;
                pending = Pending();
            }

            if (!_runnerLooked)
            {
                // Package lookup is main-thread work; the runner's path does not change in a session.
                _runnerLooked = true;
                _runner = RunnerProcess.RunnerPath();
            }

            var python = RunnerProcess.PythonPath();
            var runner = _runner;
            var environment = RunnerProcess.ChildEnvironment(bridgeUrl, fingerprint);
            System.Threading.Tasks.Task.Run(() => Poll(key, root, bridgeUrl, fingerprint, python, runner, environment, pending));
        }

        private PendingAnswer Pending() =>
            _shown != null && _shownRun != null && _sentWaitId == _shown.WaitId && !_taken
                ? new PendingAnswer { RunId = _shownRun.RunId, Directory = _shownRun.Directory, WaitId = _shown.WaitId, ItemId = _shown.ItemId }
                : null;

        private void Poll(string key, string root, string bridgeUrl, string fingerprint, string python, string runner,
            Dictionary<string, string> environment, PendingAnswer pending)
        {
            FoundRun run = null;
            WaitingReport report = null;
            bool? endedTaken = null;
            try
            {
                run = RunDiscovery.Find(root, bridgeUrl, fingerprint, out _);
                if (pending != null && (run == null || run.RunId != pending.RunId))
                {
                    endedTaken = AnsweredInRunJson(pending);
                }

                if (run != null && run.State == "running" && run.Procedure == RunProcedures.ManualSteps && runner != null)
                {
                    var result = RunnerProcess.Run(python, RunWaitingCommands.WaitingArguments(runner, run.Directory), environment,
                        RunWaitingCommands.WaitingTimeout);
                    report = result.TimedOut ? null : WaitingReport.Parse(result.Stdout);
                }
            }
            catch (Exception)
            {
                // Python missing, the runner unreadable: nothing is shown, and the next poll tries again.
                report = null;
            }
            finally
            {
                Take(key, run, report, endedTaken);
            }
        }

        /// <summary>
        /// Once the run is no longer found (it finished): whether its run.json (an ordinary file,
        /// up to 1 MiB, in the folder built from the checked run ID) records this wait's item as answered.
        /// </summary>
        internal static bool AnsweredInRunJson(PendingAnswer pending)
        {
            var record = RunFiles.ReadObject(Path.Combine(pending.Directory, "run.json"), RunFiles.MaxRunJsonBytes, out _);
            if (record == null || RunFiles.String(record["runId"]) != pending.RunId
                || !((record["manualSteps"] as JObject)?["items"] is JArray items))
            {
                return false;
            }

            foreach (var entry in items)
            {
                if (entry is JObject item && RunFiles.String(item["waitId"]) == pending.WaitId
                    && RunFiles.String(item["id"]) == pending.ItemId && RunFiles.String(item["state"]) == "answered")
                {
                    return true;
                }
            }

            return false;
        }

        /// <param name="key">The claim key the poll was made for; a result for another pairing is dropped.</param>
        /// <param name="endedTaken">When the run with an answer pending was gone: whether its run.json shows it taken.</param>
        internal void Take(string key, FoundRun run, WaitingReport report, bool? endedTaken = null)
        {
            lock (_gate)
            {
                _polling = false;
                _lastPollEndedUtc = DateTime.UtcNow;
                if (key == null || key != _pairingKey || (run != null && run.ClaimKey != key))
                {
                    return;
                }

                var before = Describe();
                _run = run;
                _current = run != null && report != null && report.ShowableFor(run.RunId) ? report : null;
                if (_shown != null && (run == null || run.RunId != _shown.RunId) && !_answering)
                {
                    // The run is gone (or another holds the claim). An answer still pending is settled from its run.json.
                    if (_sentWaitId == _shown.WaitId && !_taken && endedTaken != null)
                    {
                        _endedNote = "Run " + _shown.RunId + ", item " + _shown.ItemId + ": "
                                     + (endedTaken.Value ? TakenStatus : EndedStatus);
                    }

                    Forget();
                }

                if (_shown != null && _sentWaitId == _shown.WaitId && !_taken && report != null && report.RunId == _shown.RunId
                    && report.ItemState(_shown.ItemId) == "answered")
                {
                    _taken = true;
                    _status = TakenStatus;
                }

                if (_current != null && (_shown == null || _current.WaitId != _shown.WaitId) && !_answering)
                {
                    // A new wait: the next item, or the first one.
                    _shown = _current;
                    _shownRun = run;
                    _sentWaitId = null;
                    _triedWaitId = null;
                    _taken = false;
                    _status = null;
                    _endedNote = null;
                }
                else if (_current == null && _shown != null && !_answering && _triedWaitId != _shown.WaitId)
                {
                    // Closed with no answer tried from here (the CLI, the deadline, a stop): nothing to show.
                    // After a try, its outcome stays on screen until the next wait or the run's end.
                    Forget();
                }

                if (Describe() != before)
                {
                    _version++;
                }
            }
        }

        private void Forget()
        {
            _shown = null;
            _shownRun = null;
            _sentWaitId = null;
            _triedWaitId = null;
            _taken = false;
            _status = null;
        }

        private string Describe() =>
            (_run?.RunId ?? "-") + "|" + (_run?.State ?? "-") + "|" + (_shown?.WaitId ?? "-") + "|" + (_current?.WaitId ?? "-")
            + "|" + _answering + "|" + _status + "|" + _endedNote + "|" + (_shown?.Title ?? "") + (_shown?.Text ?? "");

        private bool CanAnswerLocked() =>
            _shown != null && _shownRun != null && !_answering && _current != null && _current.WaitId == _shown.WaitId
            && _triedWaitId != _shown.WaitId && _sentWaitId != _shown.WaitId
            && _pairingKey != null && _shownRun.ClaimKey == _pairingKey && _run != null && _run.RunId == _shownRun.RunId;

        internal WaitingSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new WaitingSnapshot
                {
                    Run = _run,
                    Shown = _shown,
                    Answering = _answering,
                    CanAnswer = CanAnswerLocked(),
                    Status = _status,
                    EndedNote = _endedNote,
                };
            }
        }

        /// <summary>
        /// Sends one answer for the wait on screen (no confirmation; r3-contract.md §6.2). Ignored
        /// while another is in flight, once this wait had an attempt (sent, refused or timed out),
        /// and unless the run was found under the claim key of the pairing given here.
        /// </summary>
        internal void Answer(string waitId, string answer, string memo, string bridgeUrl, string fingerprint)
        {
            var arguments = PrepareAnswer(waitId, answer, memo, bridgeUrl, fingerprint, _runner);
            if (arguments == null)
            {
                return;
            }

            var python = RunnerProcess.PythonPath();
            var environment = RunnerProcess.ChildEnvironment(bridgeUrl, fingerprint);
            System.Threading.Tasks.Task.Run(() =>
            {
                RunnerProcess.Result result = null;
                try
                {
                    result = RunnerProcess.Run(python, arguments, environment, RunWaitingCommands.AnswerTimeout);
                }
                catch (Exception)
                {
                    result = null;
                }

                Answered(waitId, result);
            });
        }

        /// <summary>The answer's argv and the wait marked as in flight, or null when it must not be sent.</summary>
        internal string[] PrepareAnswer(string waitId, string answer, string memo, string bridgeUrl, string fingerprint, string runner)
        {
            var key = string.IsNullOrEmpty(bridgeUrl) ? null : RemoteRunController.ClaimKey(bridgeUrl, fingerprint);
            lock (_gate)
            {
                if (runner == null || key == null || key != _pairingKey || !CanAnswerLocked() || _shown.WaitId != waitId)
                {
                    return null;
                }

                var arguments = RunWaitingCommands.AnswerArguments(runner, _shownRun.Directory, _shown.WaitId, _shown.ItemId, answer, memo);
                _answering = true;
                _status = "Sending " + answer.ToUpperInvariant() + "...";
                _version++;
                return arguments;
            }
        }

        internal void Answered(string waitId, RunnerProcess.Result result)
        {
            lock (_gate)
            {
                _answering = false;
                if (_shown == null || _shown.WaitId != waitId)
                {
                    // The pairing changed or the run went while it was in flight: nothing of it is on screen.
                    _version++;
                    return;
                }

                _triedWaitId = waitId;
                _status = RunWaitingCommands.DescribeAnswer(result);
                if (result != null && (result.TimedOut || result.ExitCode == 0))
                {
                    // Published, or (timed out) perhaps: look for it being taken.
                    _sentWaitId = waitId;
                }

                _version++;
            }
        }
    }

    /// <summary>r3-contract.md §6.4: the human answers of a manual-steps run, counted from run.json.</summary>
    internal static class ManualStepsSummary
    {
        /// <summary>"Human answers: ok 2, ng 0, skip 1 (not judged by the tool)", or null when there is no manualSteps record.</summary>
        internal static string Describe(JObject record)
        {
            if (!(record?["manualSteps"] is JObject manual) || !(manual["items"] is JArray items))
            {
                return null;
            }

            int ok = 0, ng = 0, skip = 0;
            foreach (var entry in items)
            {
                if (!(entry is JObject item) || RunFiles.String(item["state"]) != "answered")
                {
                    continue;
                }

                switch (RunFiles.String(item["answer"]))
                {
                    case "ok":
                        ok++;
                        break;
                    case "ng":
                        ng++;
                        break;
                    case "skip":
                        skip++;
                        break;
                }
            }

            return "Human answers: ok " + ok + ", ng " + ng + ", skip " + skip + " (not judged by the tool)";
        }
    }

    /// <summary>
    /// The procedures the window may start (r3-contract.md §6.3). <see cref="Validated"/> mirrors
    /// the runner's VALIDATED_PROCEDURES; the window offers only what it has a start UI for and
    /// what is validated. manual-steps' start UI is behind <see cref="ManualStepsStartEnabled"/>.
    /// </summary>
    internal static class RunProcedures
    {
        internal const string DeployCollect = "deploy-collect";
        internal const string LeaveRejoin = "leave-rejoin";
        internal const string ManualSteps = "manual-steps";

        /// <summary>The runner's VALIDATED_PROCEDURES (package/Tools~/vrcrt_runner.py).</summary>
        internal static readonly string[] Validated = { DeployCollect, LeaveRejoin, ManualSteps };

        /// <summary>Starting manual-steps from this window (the checklist picker and its prepare) is checked
        /// on real clients (docs/validation/manual-steps.md, check 4).</summary>
        internal const bool ManualStepsStartEnabled = true;

        /// <summary>What the window's procedure choice lists, in order.</summary>
        internal static string[] Offered()
        {
            var offered = new List<string>();
            foreach (var name in new[] { DeployCollect, ManualSteps })
            {
                if (Array.IndexOf(Validated, name) >= 0 && (name != ManualSteps || ManualStepsStartEnabled))
                {
                    offered.Add(name);
                }
            }

            return offered.ToArray();
        }

        /// <summary>
        /// The arguments prepare gets after its fixed ones for <paramref name="procedure"/>:
        /// nothing for deploy-collect (the runner's default, as the window has always started it);
        /// for manual-steps `--procedure manual-steps --checklist PATH [--leave-rejoin-after ID --target-slot N]`.
        /// </summary>
        internal static string[] PrepareArguments(string procedure, string checklistPath, string leaveRejoinAfter, int? targetSlot)
        {
            if (procedure == DeployCollect)
            {
                return new string[0];
            }

            if (procedure != ManualSteps)
            {
                throw new ArgumentException("the window does not start " + procedure);
            }

            if (string.IsNullOrEmpty(checklistPath) || !Path.IsPathRooted(checklistPath))
            {
                throw new ArgumentException("manual-steps needs the checklist's absolute path");
            }

            var arguments = new List<string> { "--procedure", ManualSteps, "--checklist", checklistPath };
            if (!string.IsNullOrEmpty(leaveRejoinAfter))
            {
                if (!RunFiles.IsItemId(leaveRejoinAfter) || targetSlot == null || targetSlot < 0)
                {
                    throw new ArgumentException("Leave and rejoin after needs a checklist item and a target slot");
                }

                arguments.AddRange(new[] { "--leave-rejoin-after", leaveRejoinAfter, "--target-slot", targetSlot.Value.ToString(CultureInfo.InvariantCulture) });
            }

            return arguments.ToArray();
        }

        /// <summary>
        /// The item IDs of a checklist (r3-contract.md §1), for the "Leave and rejoin after" choice
        /// only; null when the file is refused or not that shape. The runner checks it again.
        /// </summary>
        internal static string[] ChecklistItemIds(string path, out string problem)
        {
            var value = RunFiles.ReadObject(path, RunFiles.MaxChecklistBytes, out problem);
            if (value == null)
            {
                return null;
            }

            var keys = new HashSet<string>();
            foreach (var pair in value)
            {
                keys.Add(pair.Key);
            }

            if (!keys.SetEquals(new[] { "schema", "schemaVersion", "title", "items" })
                || RunFiles.String(value["schema"]) != "vrcrt.checklist"
                || value["schemaVersion"]?.Type != JTokenType.Integer || (long)value["schemaVersion"] != 1
                || !(value["title"]?.Type == JTokenType.String && RunFiles.CodePoints((string)value["title"]) is int t && t >= 1 && t <= 120)
                || !(value["items"] is JArray items) || items.Count < 1 || items.Count > 20)
            {
                problem = "the checklist is not vrcrt.checklist version 1 with a title and 1 to 20 items";
                return null;
            }

            var ids = new List<string>();
            foreach (var entry in items)
            {
                var item = entry as JObject;
                var id = RunFiles.String(item?["id"]);
                var text = RunFiles.String(item?["text"]);
                if (item == null || item.Count != 2 || !RunFiles.IsItemId(id) || ids.Contains(id)
                    || text == null || RunFiles.CodePoints(text) < 1 || RunFiles.CodePoints(text) > 1000)
                {
                    problem = "checklist item " + (ids.Count + 1) + " needs a unique id and a text of 1 to 1000 characters";
                    return null;
                }

                ids.Add(id);
            }

            return ids.ToArray();
        }
    }
}

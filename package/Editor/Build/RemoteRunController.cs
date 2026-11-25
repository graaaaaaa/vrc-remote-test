using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace VRCRemoteTest
{
    /// <summary>
    /// One "deploy and collect" run from the window: the runner's prepare, the build in this open
    /// Editor through <see cref="RemoteBuildApi"/>, then the runner's continue, detached so that it
    /// finishes even if the Editor closes. Waiting, readiness and evidence are the runner's; this
    /// only starts it, shows run.json and progress.jsonl, and asks it to stop (cancel.request).
    ///
    /// The run is tied to the Bridge it was prepared for (URL and certificate pin, kept for the
    /// whole run) and survives a domain reload through SessionState: a run found prepared after a
    /// reload, or one whose hand-over failed, is handed to continue again, which records what
    /// happened (no deploy result is UNKNOWN). A reload during prepare is recovered from the claim
    /// file whose owner is this Editor.
    /// </summary>
    internal sealed class RemoteRunController
    {
        internal enum Phase
        {
            Idle,
            Preparing,
            Building,
            Continuing,
            Finished,
        }

        private const string DirKey = "VRCRemoteTest.Run.Dir";
        private const string TokenKey = "VRCRemoteTest.Run.Token";
        private const string PhaseKey = "VRCRemoteTest.Run.Phase";
        private const string UrlKey = "VRCRemoteTest.Run.BridgeUrl";
        private const string PinKey = "VRCRemoteTest.Run.BridgePin";
        private const int MaxHandOverAttempts = 3;
        private static readonly TimeSpan HandOverRetryAfter = TimeSpan.FromSeconds(10);

        private CancellationTokenSource _buildCts;
        private bool _cancelRequested;
        private bool _handingOver;
        private int _handOverAttempts;
        private DateTime _lastHandOver = DateTime.MinValue;

        internal Phase Current { get; private set; } = Phase.Idle;

        internal string RunDirectory { get; private set; }

        /// <summary>One line for the window: what is happening or why it stopped.</summary>
        internal string Status { get; private set; }

        internal RunSummary Summary { get; private set; }

        internal bool Active => Current == Phase.Preparing || Current == Phase.Building || Current == Phase.Continuing;

        private string BridgeUrl => SessionState.GetString(UrlKey, null);

        private string BridgePin => SessionState.GetString(PinKey, null);

        private string Token => SessionState.GetString(TokenKey, null);

        /// <summary>After a domain reload: back to the run that was going on.</summary>
        internal void Restore()
        {
            Enum.TryParse(SessionState.GetString(PhaseKey, nameof(Phase.Idle)), out Phase phase);
            var dir = SessionState.GetString(DirKey, null);
            if (phase == Phase.Preparing && string.IsNullOrEmpty(dir) && FindThisEditorsRun(out var found, out var token))
            {
                // Reloaded while prepare ran: the claim it wrote names this Editor as its owner.
                dir = found;
                SessionState.SetString(DirKey, dir);
                SessionState.SetString(TokenKey, token);
            }

            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                if (phase != Phase.Idle && phase != Phase.Finished)
                {
                    Status = "A run was interrupted by a script reload before it had a run folder.";
                    SetPhase(Phase.Finished);
                }

                return;
            }

            RunDirectory = dir;
            Current = phase;
            if (phase == Phase.Preparing || phase == Phase.Building)
            {
                // The build Task did not survive the reload; continue records the run as it is.
                Status = "The build was interrupted by a script reload; recording the run as it is...";
                SetPhase(Phase.Continuing);
                _ = HandOverAsync();
            }

            Refresh();
        }

        /// <param name="waitForTag">false: look for the build tag once instead of waiting up to 120 s, for worlds that log none.</param>
        internal async void Start(string slots, bool useLastBuild, string bridgeUrl, string fingerprint, bool waitForTag = true)
        {
            if (Active)
            {
                return;
            }

            // A new run forgets the last one entirely, so nothing below can act on its folder.
            Summary = null;
            RunDirectory = null;
            _cancelRequested = false;
            _handOverAttempts = 0;
            SessionState.EraseString(DirKey);
            SessionState.EraseString(TokenKey);
            SessionState.SetString(UrlKey, bridgeUrl ?? string.Empty);
            SessionState.SetString(PinKey, (fingerprint ?? string.Empty).ToLowerInvariant());
            SetPhase(Phase.Preparing);
            Status = "Checking Python...";

            var python = RunnerProcess.PythonPath();
            var runner = RunnerProcess.RunnerPath();
            var environment = RunnerProcess.ChildEnvironment(BridgeUrl, BridgePin);
            var owner = System.Diagnostics.Process.GetCurrentProcess().Id;
            RunnerProcess.Result prepared;
            try
            {
                if (runner == null)
                {
                    Stop("This package has no Tools~/vrcrt_runner.py; reinstall it.");
                    return;
                }

                var problem = await Task.Run(() => RunnerProcess.CheckPython(python));
                if (problem != null)
                {
                    Stop(problem);
                    return;
                }

                if (_cancelRequested)
                {
                    Stop("Cancelled before anything was prepared.");
                    return;
                }

                Status = "Saving the state before the deploy...";
                var arguments = new List<string> { "-I", runner, "prepare", "--slots", slots, "--owner-pid", owner.ToString() };
                if (!waitForTag)
                {
                    arguments.Add("--world-build-wait");
                    arguments.Add("0");
                }

                prepared = await Task.Run(() => RunnerProcess.Run(python, arguments.ToArray(), environment, TimeSpan.FromSeconds(90)));
            }
            catch (Exception ex)
            {
                Stop("Could not start the runner: " + ex.Message);
                return;
            }

            var line = ParseObject(prepared.Stdout);
            var dir = line?["runDir"]?.Type == JTokenType.String ? (string)line["runDir"] : null;
            var token = line?["claimToken"]?.Type == JTokenType.String ? (string)line["claimToken"] : null;
            var tag = line?["worldBuildTag"]?.Type == JTokenType.String ? (string)line["worldBuildTag"] : null;
            if (prepared.ExitCode != 0 || dir == null || token == null)
            {
                Stop("The runner did not prepare the run: " + BridgeMessages.Sanitize(LastLine(prepared.Stderr), 400));
                return;
            }

            RunDirectory = dir;
            SessionState.SetString(DirKey, dir);
            SessionState.SetString(TokenKey, token);

            // The build goes to the Bridge this run was prepared for, or not at all.
            var samePairing = RemoteTestSettings.BridgeUrl == BridgeUrl
                && (RemoteTestSettings.BridgeFingerprint ?? string.Empty).ToLowerInvariant() == BridgePin;
            if (_cancelRequested || !samePairing)
            {
                if (!samePairing)
                {
                    Debug.LogWarning("[VRC Remote Test] Run " + Path.GetFileName(dir)
                                     + ": the pairing changed after prepare; nothing was deployed.");
                }

                WriteCancelRequest(samePairing ? "cancelled from the Unity window" : "the pairing changed after prepare");
            }
            else
            {
                SetPhase(Phase.Building);
                Status = useLastBuild ? "Deploying the last build..." : "Building and deploying...";
                _buildCts = new CancellationTokenSource();
                var request = new RemoteBuildRequest { RunDirectory = dir, WorldBuildTag = tag };
                try
                {
                    var outcome = useLastBuild
                        ? await RemoteBuildApi.DeployLastBuildAsync(request, null, _buildCts.Token)
                        : await RemoteBuildApi.BuildAndDeployAsync(request, null, _buildCts.Token);
                    Debug.Log("[VRC Remote Test] Run " + Path.GetFileName(dir) + ": deploy "
                              + (outcome.Succeeded ? "succeeded" : "did not succeed") + "; the runner continues.");
                }
                catch (Exception ex)
                {
                    Debug.LogError("[VRC Remote Test] Run " + Path.GetFileName(dir) + ": the build threw: " + ex.Message);
                }
                finally
                {
                    _buildCts.Dispose();
                    _buildCts = null;
                }
            }

            // Always: continue records the deploy result, a cancel, or their absence, as the outcome.
            SetPhase(Phase.Continuing);
            Status = "Waiting for the clients and collecting evidence...";
            await HandOverAsync();
        }

        /// <summary>
        /// Starts `continue --detach`. A failure is shown and retried by <see cref="Refresh"/> while
        /// run.json still says prepared (up to three times); nothing is lost meanwhile, since the run
        /// stays prepared and the claim stays with this Editor.
        /// </summary>
        private async Task HandOverAsync()
        {
            var dir = RunDirectory;
            var token = Token;
            if (_handingOver || string.IsNullOrEmpty(dir))
            {
                return;
            }

            if (string.IsNullOrEmpty(token))
            {
                Stop("The run's claim token was lost; it ends at its deadline (or run vrcrt_runner.py recover).");
                return;
            }

            _handingOver = true;
            _handOverAttempts++;
            _lastHandOver = DateTime.UtcNow;
            try
            {
                var python = RunnerProcess.PythonPath();
                var runner = RunnerProcess.RunnerPath();
                var environment = RunnerProcess.ChildEnvironment(BridgeUrl, BridgePin);
                var started = await Task.Run(() => RunnerProcess.Run(python,
                    new[] { "-I", runner ?? string.Empty, "continue", "--run", dir, "--claim-token", token, "--detach" },
                    environment, TimeSpan.FromSeconds(30)));
                if (started.ExitCode != 0)
                {
                    Status = "Could not hand the run to the runner (attempt " + _handOverAttempts + "): "
                        + BridgeMessages.Sanitize(LastLine(started.Stderr), 300);
                }
            }
            catch (Exception ex)
            {
                Status = "Could not hand the run to the runner (attempt " + _handOverAttempts + "): " + ex.Message;
            }
            finally
            {
                _handingOver = false;
            }
        }

        /// <summary>Stops the build when it is running, and asks the runner to stop; before prepare ends, remembers it.</summary>
        internal void Cancel()
        {
            _cancelRequested = true;
            _buildCts?.Cancel();
            if (Current == Phase.Preparing && string.IsNullOrEmpty(RunDirectory))
            {
                Status = "Cancelling once the run folder exists...";
                return;
            }

            WriteCancelRequest("cancelled from the Unity window");
        }

        private void WriteCancelRequest(string why)
        {
            if (string.IsNullOrEmpty(RunDirectory))
            {
                return;
            }

            try
            {
                var dir = DeployResultWriter.ValidatedRunDirectory(RunDirectory);
                var request = Path.Combine(dir, "cancel.request");
                File.WriteAllText(request, why + "\n");
                DeployResultWriter.SetMode(request, Convert.ToInt32("600", 8));
                Status = "Cancelling...";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                Status = "Could not ask the runner to stop: " + ex.Message;
            }
        }

        /// <summary>Re-reads run.json and the last progress line; called every second or so by the window.</summary>
        internal void Refresh()
        {
            if (string.IsNullOrEmpty(RunDirectory))
            {
                return;
            }

            Summary = RunSummary.Read(RunDirectory);
            if (Summary == null)
            {
                return;
            }

            if (Summary.Finished && Current != Phase.Building && Current != Phase.Preparing)
            {
                SetPhase(Phase.Finished);
                Status = Summary.Describe();
                return;
            }

            if (Current != Phase.Continuing)
            {
                return;
            }

            if (Summary.State == "prepared" && !_handingOver && DateTime.UtcNow - _lastHandOver > HandOverRetryAfter)
            {
                if (_handOverAttempts >= MaxHandOverAttempts)
                {
                    Stop("The runner did not take the run over after " + MaxHandOverAttempts + " attempts; it stays prepared "
                         + "until its deadline. See the run folder.");
                    return;
                }

                _ = HandOverAsync();
            }
            else if (!string.IsNullOrEmpty(Summary.LastProgress))
            {
                Status = Summary.LastProgress;
            }
        }

        /// <summary>
        /// The run whose claim (in the default run root, for this run's Bridge) names this Editor
        /// process as its owner and whose run.json is still prepared.
        /// </summary>
        private bool FindThisEditorsRun(out string dir, out string token)
        {
            dir = null;
            token = null;
            try
            {
                var root = DeployResultWriter.DefaultRoot();
                var claim = BridgeMessages.Parse<JObject>(File.ReadAllText(Path.Combine(root, "locks", ClaimKey(BridgeUrl, BridgePin) + ".json")));
                var owner = claim?["owner"]?["pid"];
                var runId = claim?["runId"]?.Type == JTokenType.String ? (string)claim["runId"] : null;
                if (owner == null || owner.Type != JTokenType.Integer || (int)owner != System.Diagnostics.Process.GetCurrentProcess().Id
                    || runId == null || !System.Text.RegularExpressions.Regex.IsMatch(runId, @"^\d{8}T\d{6}Z-[0-9a-f]{8}$"))
                {
                    return false;
                }

                var candidate = Path.Combine(root, "runs", runId);
                if (RunSummary.Read(candidate)?.State != "prepared")
                {
                    return false;
                }

                dir = candidate;
                token = claim["token"]?.Type == JTokenType.String ? (string)claim["token"] : null;
                return token != null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The runner's claim key: SHA-256 of "URL|pin" (pin in lower case), first 24 hex digits.</summary>
        internal static string ClaimKey(string url, string pin)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((url ?? string.Empty) + "|" + (pin ?? string.Empty).ToLowerInvariant()));
                var hex = new StringBuilder();
                foreach (var b in bytes)
                {
                    hex.Append(b.ToString("x2"));
                }

                return hex.ToString(0, 24);
            }
        }

        private void SetPhase(Phase phase)
        {
            Current = phase;
            SessionState.SetString(PhaseKey, phase.ToString());
        }

        private void Stop(string message)
        {
            Status = message;
            SetPhase(Phase.Finished);
        }

        private static string LastLine(string text)
        {
            var lines = (text ?? string.Empty).Trim().Split('\n');
            return lines.Length == 0 ? string.Empty : lines[lines.Length - 1];
        }

        private static JObject ParseObject(string text)
        {
            foreach (var line in (text ?? string.Empty).Split('\n'))
            {
                var parsed = BridgeMessages.Parse<JObject>(line.Trim());
                if (parsed != null)
                {
                    return parsed;
                }
            }

            return null;
        }
    }

    /// <summary>What the window shows of a run: run.json's state and results, and the last progress line.</summary>
    internal sealed class RunSummary
    {
        private const int MaxRunJsonBytes = 1024 * 1024;
        private const int MaxProgressTail = 8 * 1024;

        internal bool Finished;
        internal string State;
        internal string Result;
        internal string Reason;
        internal string Detail;
        internal string RunId;
        internal readonly Dictionary<string, string> Results = new Dictionary<string, string>();
        internal string LastProgress;

        internal static RunSummary Read(string runDirectory)
        {
            var path = Path.Combine(runDirectory, "run.json");
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxRunJsonBytes)
                {
                    return null;
                }

                var record = BridgeMessages.Parse<JObject>(File.ReadAllText(path));
                if (record == null || (string)record["schema"] != "vrcrt.run")
                {
                    return null;
                }

                var summary = new RunSummary
                {
                    State = Text(record["state"]),
                    Result = Text(record["result"]),
                    Reason = Text(record["reason"]),
                    Detail = Text(record["detail"]),
                    RunId = Text(record["runId"]),
                };
                summary.Finished = summary.State == "finished";
                if (record["results"] is JObject results)
                {
                    foreach (var pair in results)
                    {
                        summary.Results[pair.Key] = Text(pair.Value) ?? "-";
                    }
                }

                summary.LastProgress = LastProgressMessage(Path.Combine(runDirectory, "progress.jsonl"));
                return summary;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>"PASS: ... not judged", or the result and its reason.</summary>
        internal string Describe()
        {
            if (!Finished)
            {
                return "Running...";
            }

            var text = Result == "PASS"
                ? "PASS: the clients were ready and the evidence was collected. The test itself is not judged."
                : (Result ?? "UNKNOWN") + ": " + (Reason ?? "no reason recorded");
            return Detail == null ? text : text + " (" + Detail + ")";
        }

        private static string Text(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return BridgeMessages.Sanitize(token.Type == JTokenType.String ? (string)token : token.ToString(), 300);
        }

        private static string LastProgressMessage(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var start = Math.Max(0, stream.Length - MaxProgressTail);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - start];
                var read = stream.Read(buffer, 0, buffer.Length);
                var lines = Encoding.UTF8.GetString(buffer, 0, read).Trim().Split('\n');
                for (var i = lines.Length - 1; i >= 0; i--)
                {
                    var line = BridgeMessages.Parse<JObject>(lines[i].Trim());
                    if (line != null)
                    {
                        var message = Text(line["message"]) ?? string.Empty;
                        var reasons = line["reasons"] is JObject r ? " " + Text(r) : string.Empty;
                        return message + reasons;
                    }
                }
            }

            return null;
        }
    }
}

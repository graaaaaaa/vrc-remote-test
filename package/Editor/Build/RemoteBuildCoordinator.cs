using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace VRCRemoteTest
{
    /// <summary>
    /// Orchestrates a single remote build end to end: preflight, SDK build,
    /// artifact resolution, hashing, upload, and result polling. Dependencies
    /// are constructor-injected so tests can substitute fakes for
    /// IVrcSdkBuildAdapter and IRemoteTransport (spec section 53) — the only
    /// two interfaces this design keeps, per the Phase 2 Codex plan review.
    /// </summary>
    public sealed class RemoteBuildCoordinator
    {
        private static readonly Regex Sha256HexPattern = new Regex(@"^[0-9a-f]{64}$", RegexOptions.Compiled);
        private const int MaxErrorMessageLength = 2000;

        private readonly IVrcSdkBuildAdapter _sdkAdapter;
        private readonly IRemoteTransport _transport;
        private readonly SemaphoreSlim _executionLock = new SemaphoreSlim(1, 1);

        public RemoteBuildCoordinator(IVrcSdkBuildAdapter sdkAdapter, IRemoteTransport transport)
        {
            _sdkAdapter = sdkAdapter;
            _transport = transport;
        }

        /// <summary>
        /// Display-only. Reading SemaphoreSlim.CurrentCount needs no lock, but
        /// the TOCTOU window this allows is acceptable only for UI status —
        /// the actual concurrency guard remains the Wait(0) below.
        /// </summary>
        public bool IsRunning => _executionLock.CurrentCount == 0;

        public bool IsBridgeReachable => _transport.IsAvailable;

        internal void ProbeBridgeForHeadless() => _transport.ProbeAsync().GetAwaiter().GetResult();

        public bool IsSdkAvailable => _sdkAdapter.IsAvailable;

        /// <summary>
        /// Phase 4a advisory status (VRChat process running / --watch-worlds
        /// detected). Never gates build success — display only. Null if the
        /// Bridge hasn't written a status file yet or it couldn't be read.
        /// </summary>
        public VrchatStatus VrchatStatus => _transport.PollVrchatStatus();

        /// <summary>
        /// Phase 5 advisory log content (recent VRChat output_log lines,
        /// republished by the Bridge). Never gates build success — display
        /// only. Null if the Bridge hasn't published a log yet or it couldn't
        /// be read.
        /// </summary>
        public string VrchatLog => _transport.PollVrchatLog();

        /// <summary>Null with a Bridge older than v0.6.0; see IRemoteTransport.PollVrchatClients.</summary>
        public VrchatClients VrchatClients => _transport.PollVrchatClients();

        /// <summary>Null with a Bridge older than v0.8.0; see IRemoteTransport.PollBridgeInfo. Display only.</summary>
        public BridgeInfo BridgeInfo => _transport.PollBridgeInfo();

        public string GetClientLog(int slot) => _transport.PollClientLog(slot);

        public string GetClientMarkers(int slot) => _transport.PollClientMarkers(slot);

        public Task DownloadFullLogAsync(int slot, long clientGeneration, long observationEpoch,
            string destinationPath, CancellationToken cancellationToken = default) =>
            _transport.DownloadFullLogAsync(slot, clientGeneration, observationEpoch,
                destinationPath, cancellationToken);

        // === VRChat clients: the Bridge's management API (v0.8.0 and later) ===

        // Deadlines of a settings change, each for one step, so the build lock is held for at
        // most about 2.5 minutes even when the Bridge stops answering (each management call
        // would otherwise wait up to LanRemoteTransport's 2 minutes). The tests shorten them.

        /// <summary>GET /config before the change.</summary>
        internal TimeSpan ClientSettingsReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>PUT /config: closing the clients the Bridge launched, when the mode changes, takes up to 20 s.</summary>
        internal TimeSpan ClientSettingsWriteTimeout { get; set; } = TimeSpan.FromSeconds(45);

        /// <summary>From the accepted change until the restarted host reports it (as scripts/vrcrt.sh).</summary>
        internal TimeSpan ClientSettingsApplyTimeout { get; set; } = TimeSpan.FromSeconds(90);

        internal TimeSpan ClientSettingsPollInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>One GET /bridge while waiting; a slow answer is retried at the next poll.</summary>
        internal TimeSpan ClientSettingsPollRequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Replaced by the tests so that waiting for a restart takes no time.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        /// <summary>GET /profiles: the account each profile last logged in to; null when it cannot be read.</summary>
        public async Task<BridgeProfiles> GetVrchatProfilesAsync(CancellationToken cancellationToken = default)
        {
            var response = await _transport.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
            return response.IsSuccess ? response.As<BridgeProfiles>() : null;
        }

        /// <summary>
        /// Writes <paramref name="settings"/> into the Bridge's config.json (PUT /config with the
        /// etag just read) and waits for the restarted host to report it applied, rolled back or
        /// failed. The Bridge validates the change against its own rules, closes the clients it
        /// launched first when the mode changes, and refuses when any other VRChat runs. When
        /// <paramref name="expectedActiveMode"/> is given (the mode the user saw when confirming),
        /// nothing is written unless the running host still has that mode, which narrows the
        /// race with another change; only the Bridge could close it completely. Holds the same
        /// lock as a build, so neither can start while the other runs; nothing here reads
        /// EditorPrefs. Cancelling stops the wait, not a change the Bridge already accepted.
        /// </summary>
        public async Task<VrchatClientSettingsOutcome> ApplyVrchatClientSettingsAsync(
            VrchatClientSettings settings, string expectedActiveMode = null, IProgress<string> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (!_executionLock.Wait(0))
            {
                return VrchatClientSettingsOutcome.Failure("A remote build or another Bridge change is in progress.");
            }

            try
            {
                return await ApplyVrchatClientSettingsCoreAsync(settings, expectedActiveMode, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return VrchatClientSettingsOutcome.Failure(
                    "Stopped waiting. A change the Bridge already accepted may still be applied, and VRChat clients "
                    + "may have closed; see the Bridge section before applying again.");
            }
            finally
            {
                _executionLock.Release();
            }
        }

        private async Task<VrchatClientSettingsOutcome> ApplyVrchatClientSettingsCoreAsync(
            VrchatClientSettings settings, string expectedActiveMode, IProgress<string> progress,
            CancellationToken cancellationToken)
        {
            progress?.Report("Reading the Bridge's settings...");
            var configResponse = await CallWithin(_transport.GetConfigAsync, ClientSettingsReadTimeout, cancellationToken)
                .ConfigureAwait(false);
            var etag = configResponse.IsSuccess ? StringMember(configResponse.Body, "etag") : null;
            if (etag == null)
            {
                return VrchatClientSettingsOutcome.Failure(AdminFailure("read config.json", configResponse));
            }

            // "active" is what the running host was started with, read with the etag just used.
            if (expectedActiveMode != null)
            {
                var active = VrchatClientSettings.FromActiveConfig(ParseObjectOrNull(configResponse.Body)?["active"] as JObject);
                if (active == null || active.Mode != expectedActiveMode)
                {
                    return VrchatClientSettingsOutcome.Failure(
                        "The Bridge's mode is now " + (active?.Mode ?? "unknown") + ", not " + expectedActiveMode
                        + " as when you confirmed, so nothing was written. Check its settings and apply again.");
                }
            }

            progress?.Report("Writing " + settings.Describe() + "...");
            var put = await CallWithin(token => _transport.PutConfigAsync(etag, settings.ToMergePatch(), token),
                ClientSettingsWriteTimeout, cancellationToken).ConfigureAwait(false);
            if (put.Supported && put.StatusCode == 0)
            {
                // Sent, but no answer: the Bridge may have closed clients and written the change.
                return VrchatClientSettingsOutcome.Failure(
                    "The Bridge did not answer the change: " + BridgeMessages.Sanitize(put.Message, 200)
                    + " It may have been applied, and VRChat clients may have closed; see the Bridge section before applying again.");
            }

            var operationId = put.StatusCode == 202 ? StringMember(put.Body, "operationId") : null;
            if (operationId == null)
            {
                return VrchatClientSettingsOutcome.Failure(RefusedChange(put), Problems(put.Body));
            }

            progress?.Report("config.json written; waiting for the Bridge to restart...");
            using (var applyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                applyDeadline.CancelAfter(ClientSettingsApplyTimeout);
                try
                {
                    while (true)
                    {
                        await Delay(ClientSettingsPollInterval, applyDeadline.Token).ConfigureAwait(false);

                        // No answer while the host restarts is expected. The outcome is kept by the
                        // Bridge across restarts and written only once a host has started (or none
                        // could), so our operation id with a final outcome is the answer; the host
                        // generation is not compared, as it starts again at 1 in a new process.
                        var response = await CallWithin(_transport.GetBridgeInfoAsync, ClientSettingsPollRequestTimeout,
                            applyDeadline.Token).ConfigureAwait(false);
                        var now = response.IsSuccess ? response.As<BridgeInfo>() : null;
                        var last = now?.LastConfigApply;
                        if (last == null || last.OperationId != operationId
                            || string.IsNullOrEmpty(last.Outcome) || last.Outcome == "pending")
                        {
                            continue;
                        }

                        return ApplyOutcome(settings, last, now.ConfigNotes);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return VrchatClientSettingsOutcome.Failure(
                        "The Bridge did not report the change within " + ClientSettingsApplyTimeout.TotalSeconds
                        + " s. It may still be applied; see the Bridge section, or run scripts/vrcrt.sh doctor.");
                }
            }
        }

        /// <summary>What the Bridge's final lastConfigApply means for the user; its problems go into the details.</summary>
        internal static VrchatClientSettingsOutcome ApplyOutcome(VrchatClientSettings settings, BridgeConfigApply last,
            IEnumerable<string> notes)
        {
            switch (last.Outcome)
            {
                case "applied":
                    return VrchatClientSettingsOutcome.Success(
                        "Applied: " + settings.Describe() + ". The next Remote Build & Test or Deploy Last Build uses it.",
                        notes);
                case "rolledBack":
                    return VrchatClientSettingsOutcome.Failure(
                        "The Bridge could not start with the change and restored its previous config.json. "
                        + "Check its current settings.", last.Problems);
                case "failed":
                    return VrchatClientSettingsOutcome.Failure(
                        "The Bridge reported that the change failed. Check its current settings and the details below.",
                        last.Problems);
                default:
                    return VrchatClientSettingsOutcome.Failure(
                        "The Bridge reported an outcome this package does not know (" + BridgeMessages.Sanitize(last.Outcome, 40)
                        + "). Check its current settings.", last.Problems);
            }
        }

        /// <summary>
        /// One management call with its own deadline. A deadline that passes is an answer like
        /// any unreachable Bridge (status 0); cancellation of <paramref name="cancellationToken"/>
        /// is thrown.
        /// </summary>
        private static async Task<BridgeAdminResponse> CallWithin(
            Func<CancellationToken, Task<BridgeAdminResponse>> call, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(timeout);
                try
                {
                    return await call(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return BridgeAdminResponse.Unreachable(
                        "The Windows Bridge did not answer within " + timeout.TotalSeconds + " s.");
                }
            }
        }

        /// <summary>
        /// POST /clients/close: closes the VRChat clients the Bridge launched (any other VRChat is
        /// left running), so that the next deploy launches the configured clients from none.
        /// </summary>
        public async Task<VrchatClientSettingsOutcome> CloseVrchatClientsAsync(CancellationToken cancellationToken = default)
        {
            if (!_executionLock.Wait(0))
            {
                return VrchatClientSettingsOutcome.Failure("A remote build or another Bridge change is in progress.");
            }

            try
            {
                var response = await _transport.CloseClientsAsync(cancellationToken).ConfigureAwait(false);
                var body = response.Supported && response.StatusCode != 0 ? ParseObjectOrNull(response.Body) : null;
                var closed = BridgeMessages.ByteCount(body?["closed"]);

                // Also on success: the clients closed, but the Bridge may not have updated its record of them.
                var problem = body?["problem"]?.Type == JTokenType.String ? (string)body["problem"] : null;
                var details = string.IsNullOrWhiteSpace(problem) ? null : new[] { problem };
                if (!response.IsSuccess || closed == null)
                {
                    return VrchatClientSettingsOutcome.Failure(AdminFailure("close the VRChat clients", response), details);
                }

                var notConfirmed = BridgeMessages.ByteCount(body["notConfirmed"]) ?? 0;
                var others = BridgeMessages.ByteCount(body["othersRunning"]) ?? 0;
                var message = $"Closed {closed} VRChat client(s) the Bridge launched.";
                if (notConfirmed > 0)
                {
                    message += $" {notConfirmed} did not confirm closing; close them on Windows.";
                }

                if (others > 0)
                {
                    message += $" {others} other VRChat client(s), not launched by the Bridge, were left running.";
                }

                return notConfirmed > 0
                    ? VrchatClientSettingsOutcome.Failure(message, details)
                    : VrchatClientSettingsOutcome.Success(message, details);
            }
            catch (OperationCanceledException)
            {
                return VrchatClientSettingsOutcome.Failure("Stopped waiting for the Bridge to close its VRChat clients.");
            }
            finally
            {
                _executionLock.Release();
            }
        }

        /// <summary>Why a management call gave no usable answer, in one line.</summary>
        internal static string AdminFailure(string what, BridgeAdminResponse response)
        {
            if (!response.Supported || response.StatusCode == 0)
            {
                return "Could not " + what + ": " + BridgeMessages.Sanitize(response.Message, 300);
            }

            if (response.StatusCode == 404)
            {
                return "Could not " + what + ": this Bridge has no management API; update it to v0.8.0 or later.";
            }

            if (response.StatusCode == 409 && response.Error == "busy")
            {
                return "Could not " + what + ": the Bridge is busy (" + BusyOperation(response.Body)
                    + "). Try again when it is done.";
            }

            return "Could not " + what + ": the Bridge answered HTTP " + response.StatusCode
                + (response.Error == null ? "." : " (" + response.Error + ").");
        }

        /// <summary>Why the Bridge refused PUT /config, in one line; its problems go into the outcome's details.</summary>
        internal static string RefusedChange(BridgeAdminResponse response)
        {
            if (response.Supported && response.StatusCode != 0)
            {
                switch (response.Error)
                {
                    case "vrchatRunning":
                        return "The Bridge did not change the mode: it closes only the VRChat clients it launched, "
                            + "and another VRChat is running or one did not close. Close VRChat on Windows, then apply again.";
                    case "etagMismatch":
                        return "config.json changed on Windows meanwhile, so nothing was written. Apply again.";
                    case "invalidConfig":
                    case "invalidPatch":
                        return "The Bridge refused these settings; config.json was not changed.";
                }
            }

            return AdminFailure("change the settings", response);
        }

        internal static IReadOnlyList<string> Problems(string body)
        {
            var problems = ParseObjectOrNull(body)?["problems"] as JArray;
            var list = new List<string>();
            if (problems != null)
            {
                foreach (var item in problems)
                {
                    if (item.Type == JTokenType.String)
                    {
                        list.Add((string)item);
                    }
                }
            }

            return list;
        }

        private static string BusyOperation(string body)
        {
            var operation = StringMember(body, "operation");
            return operation == null ? "another operation" : BridgeMessages.Sanitize(operation, 40);
        }

        /// <summary>A string member of a JSON object body that is safe to send back (an etag, an operation id), or null.</summary>
        private static string StringMember(string body, string name)
        {
            var token = ParseObjectOrNull(body)?[name];
            if (token == null || token.Type != JTokenType.String)
            {
                return null;
            }

            var value = (string)token;
            return value.Length > 0 && value.Length <= 128 && Regex.IsMatch(value, @"^[A-Za-z0-9._-]+$") ? value : null;
        }

        private static JObject ParseObjectOrNull(string body) =>
            BridgeMessages.Parse<JObject>(body);

        /// <summary>
        /// Set as soon as SHA-256 has been computed for a successful SDK
        /// build, even if the subsequent upload/poll fails — a build that got
        /// this far is "redeployable" (spec section 40's Deploy Last Build use
        /// case: retrying a failed transfer without re-running the SDK build).
        /// In-memory only; does not survive a domain reload.
        /// </summary>
        public BuildArtifact LastArtifact { get; private set; }

        public async Task<RemoteBuildOutcome> ExecuteRemoteBuildAsync(
            IProgress<RemoteBuildProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!_executionLock.Wait(0))
            {
                var busyMessage = "A remote build is already in progress.";
                progress?.Report(new RemoteBuildProgress(RemoteBuildStatus.Failed, busyMessage));
                return RemoteBuildOutcome.Failure(null, ErrorCode.BuildAlreadyRunning, busyMessage);
            }

            try
            {
                return await ExecuteInternalAsync(progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _executionLock.Release();
            }
        }

        private async Task<RemoteBuildOutcome> ExecuteInternalAsync(
            IProgress<RemoteBuildProgress> progress, CancellationToken cancellationToken)
        {
            string buildId = null;

            try
            {
                // Read Unity-API-backed settings now, while we are still
                // guaranteed to be on the main thread (this method runs
                // synchronously up to its first await). DeployArtifactAsync
                // and PollForResultAsync run after several ConfigureAwait(false)
                // hops and may no longer be on the main thread, so they cannot
                // safely call EditorPrefs themselves (EditorPrefs.GetInt throws
                // if called off the main thread) — pass the values in instead.
                var resultTimeout = TimeSpan.FromSeconds(RemoteTestSettings.ResultTimeoutSeconds);
                var pollInterval = TimeSpan.FromSeconds(RemoteTestSettings.PollIntervalSeconds);

                Report(progress, RemoteBuildStatus.Running, "Running preflight checks...");
                await RunPreflightAsync(cancellationToken);

                Report(progress, RemoteBuildStatus.Running, "Building Windows world...");
                cancellationToken.ThrowIfCancellationRequested();
                var sdkPath = await _sdkAdapter.BuildWindowsWorldAsync();
                cancellationToken.ThrowIfCancellationRequested();

                Report(progress, RemoteBuildStatus.Running, "Resolving build artifact...");
                var artifact = BuildArtifact.FromSdkPath(sdkPath);
                Debug.Log($"[VRC Remote Test] Artifact: {artifact.FullPath} ({artifact.Size} bytes)");

                Report(progress, RemoteBuildStatus.Running, "Computing SHA-256...");
                artifact.Sha256 = Sha256Calculator.ComputeHash(artifact.FullPath);
                Debug.Log($"[VRC Remote Test] SHA-256: {artifact.Sha256}");
                LastArtifact = artifact;

                return await DeployArtifactAsync(artifact, resultTimeout, pollInterval, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RemoteBuildException ex)
            {
                Report(progress, RemoteBuildStatus.Failed, ex.Message);
                Debug.LogError($"[VRC Remote Test] {ex.ErrorCodeValue}: {ex.Message}");
                return RemoteBuildOutcome.Failure(buildId, ex.ErrorCodeValue, ex.Message);
            }
            catch (OperationCanceledException)
            {
                const string message = "Build cancelled.";
                Report(progress, RemoteBuildStatus.Failed, message);
                Debug.LogWarning($"[VRC Remote Test] {message}");
                return RemoteBuildOutcome.Failure(buildId, ErrorCode.UnknownError, message);
            }
            catch (Exception ex)
            {
                Report(progress, RemoteBuildStatus.Failed, ex.Message);
                Debug.LogError($"[VRC Remote Test] Unexpected error: {ex}");
                return RemoteBuildOutcome.Failure(buildId, ErrorCode.UnknownError, ex.Message);
            }
        }

        /// <summary>
        /// Shared by ExecuteInternalAsync (fresh build) and DeployLastBuildAsync
        /// (redeploy of a cached artifact): buildId generation, upload, result
        /// polling, and outcome mapping. Owns the full try/catch itself so
        /// buildId is preserved on every error path regardless of which caller
        /// invoked it (Codex plan review Phase 3, confidence 0.90).
        /// </summary>
        private async Task<RemoteBuildOutcome> DeployArtifactAsync(
            BuildArtifact artifact,
            TimeSpan resultTimeout,
            TimeSpan pollInterval,
            IProgress<RemoteBuildProgress> progress,
            CancellationToken cancellationToken)
        {
            string buildId = null;

            try
            {
                buildId = BuildIdGenerator.Generate();
                if (!BuildIdGenerator.IsValid(buildId))
                {
                    throw new RemoteBuildException(
                        ErrorCode.UnknownError, "Generated buildId failed self-validation.");
                }

                var manifest = new BuildManifest
                {
                    ProtocolVersion = ProtocolConstants.CurrentProtocolVersion,
                    BuildId = buildId,
                    FileName = $"{buildId}.vrcw",
                    Size = artifact.Size,
                    Sha256 = artifact.Sha256,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };

                // Before uploading, so that a world over the Bridge's limit is not sent only to be
                // refused. No ConfigureAwait(false): the upload below starts on the thread it did before.
                var maxSize = await _transport.GetMaxArtifactSizeBytesAsync(cancellationToken);
                var oversize = DescribeOversize(artifact.Size, maxSize);
                if (oversize != null)
                {
                    throw new RemoteBuildException(ErrorCode.SizeExceeded, oversize);
                }

                Report(progress, RemoteBuildStatus.Running, "Uploading to Windows Bridge...");
                await _transport.UploadBuildAsync(artifact, manifest, buildId, cancellationToken).ConfigureAwait(false);
                Debug.Log($"[VRC Remote Test] Upload complete. Build ID: {buildId}");

                Report(progress, RemoteBuildStatus.Running, "Waiting for Bridge result...");
                var result = await PollForResultAsync(buildId, resultTimeout, pollInterval, cancellationToken)
                    .ConfigureAwait(false);

                var notes = ResultNotes(result);
                foreach (var note in notes)
                {
                    Debug.LogWarning($"[VRC Remote Test] {note}");
                }

                if (result.Status == "deployed")
                {
                    var message = $"Deployed as {result.DeployedFileName}";
                    Report(progress, RemoteBuildStatus.Succeeded, WithNotes(message, notes));
                    Debug.Log($"[VRC Remote Test] {message}");
                    return RemoteBuildOutcome.Success(buildId, result);
                }

                var sanitizedMessage = SanitizeErrorMessage(result.ErrorMessage) ?? "Bridge reported failure.";
                Report(progress, RemoteBuildStatus.Failed, WithNotes($"{result.ErrorCode}: {sanitizedMessage}", notes));
                Debug.LogError($"[VRC Remote Test] Bridge error: {result.ErrorCode} - {sanitizedMessage}");
                return RemoteBuildOutcome.Failure(buildId, result.ErrorCode, sanitizedMessage, result);
            }
            catch (RemoteBuildException ex)
            {
                Report(progress, RemoteBuildStatus.Failed, ex.Message);
                Debug.LogError($"[VRC Remote Test] {ex.ErrorCodeValue}: {ex.Message}");
                return RemoteBuildOutcome.Failure(buildId, ex.ErrorCodeValue, ex.Message);
            }
            catch (OperationCanceledException)
            {
                const string message = "Deploy cancelled.";
                Report(progress, RemoteBuildStatus.Failed, message);
                Debug.LogWarning($"[VRC Remote Test] {message}");
                return RemoteBuildOutcome.Failure(buildId, ErrorCode.UnknownError, message);
            }
            catch (Exception ex)
            {
                Report(progress, RemoteBuildStatus.Failed, ex.Message);
                Debug.LogError($"[VRC Remote Test] Unexpected error: {ex}");
                return RemoteBuildOutcome.Failure(buildId, ErrorCode.UnknownError, ex.Message);
            }
        }

        /// <summary>
        /// Redeploys the last successfully-built artifact without invoking the
        /// SDK build again (spec section 40: Bridge testing / Windows-side
        /// troubleshooting / re-confirming --watch-worlds without waiting for a
        /// full SDK build). Re-hashes before upload since the file at the
        /// cached path may have changed since it was originally built (Codex
        /// plan review Phase 3, confidence 0.93) — a stale hash would just get
        /// rejected by the Bridge as HASH_MISMATCH after a wasted upload.
        /// </summary>
        public async Task<RemoteBuildOutcome> DeployLastBuildAsync(
            IProgress<RemoteBuildProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!_executionLock.Wait(0))
            {
                var busyMessage = "A remote build is already in progress.";
                progress?.Report(new RemoteBuildProgress(RemoteBuildStatus.Failed, busyMessage));
                return RemoteBuildOutcome.Failure(null, ErrorCode.BuildAlreadyRunning, busyMessage);
            }

            try
            {
                var artifact = LastArtifact;
                if (artifact == null)
                {
                    var msg = "No previous build artifact available. Run Remote Build & Test first.";
                    Report(progress, RemoteBuildStatus.Failed, msg);
                    return RemoteBuildOutcome.Failure(null, ErrorCode.ArtifactNotFound, msg);
                }

                if (!File.Exists(artifact.FullPath))
                {
                    var msg = $"Previous build artifact no longer exists: {artifact.FullPath}";
                    Report(progress, RemoteBuildStatus.Failed, msg);
                    return RemoteBuildOutcome.Failure(null, ErrorCode.ArtifactNotFound, msg);
                }

                Report(progress, RemoteBuildStatus.Running, "Re-verifying artifact...");
                var currentHash = Sha256Calculator.ComputeHash(artifact.FullPath);
                var currentSize = new FileInfo(artifact.FullPath).Length;
                if (currentHash != artifact.Sha256 || currentSize != artifact.Size)
                {
                    Debug.LogWarning(
                        $"[VRC Remote Test] Artifact changed since last build. " +
                        $"Hash: {artifact.Sha256} -> {currentHash}, Size: {artifact.Size} -> {currentSize}");
                    artifact.Sha256 = currentHash;
                    artifact.Size = currentSize;
                }

                Report(progress, RemoteBuildStatus.Running, "Running preflight checks...");
                await RunPreflightAsync(cancellationToken);

                var resultTimeout = TimeSpan.FromSeconds(RemoteTestSettings.ResultTimeoutSeconds);
                var pollInterval = TimeSpan.FromSeconds(RemoteTestSettings.PollIntervalSeconds);
                return await DeployArtifactAsync(artifact, resultTimeout, pollInterval, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RemoteBuildException ex)
            {
                Report(progress, RemoteBuildStatus.Failed, ex.Message);
                Debug.LogError($"[VRC Remote Test] {ex.ErrorCodeValue}: {ex.Message}");
                return RemoteBuildOutcome.Failure(null, ex.ErrorCodeValue, ex.Message);
            }
            catch (OperationCanceledException)
            {
                const string message = "Deploy cancelled.";
                Report(progress, RemoteBuildStatus.Failed, message);
                Debug.LogWarning($"[VRC Remote Test] {message}");
                return RemoteBuildOutcome.Failure(null, ErrorCode.UnknownError, message);
            }
            catch (Exception ex)
            {
                Report(progress, RemoteBuildStatus.Failed, ex.Message);
                Debug.LogError($"[VRC Remote Test] Unexpected error: {ex}");
                return RemoteBuildOutcome.Failure(null, ErrorCode.UnknownError, ex.Message);
            }
            finally
            {
                _executionLock.Release();
            }
        }

        private async Task RunPreflightAsync(CancellationToken cancellationToken)
        {
            if (EditorApplication.isPlaying)
            {
                throw new RemoteBuildException(
                    ErrorCode.PlayModeActive, "Cannot start a remote build while in Play Mode.");
            }

            await _transport.ProbeAsync(cancellationToken);
        }

        private async Task<BuildResult> PollForResultAsync(
            string buildId, TimeSpan timeout, TimeSpan interval, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await _transport.PollResultAsync(buildId, cancellationToken).ConfigureAwait(false);
                if (result != null)
                {
                    if (!IsValidResult(result, buildId))
                    {
                        throw new RemoteBuildException(
                            ErrorCode.ResultInvalid,
                            $"Bridge returned an invalid or unexpected result for build {buildId}.");
                    }

                    return result;
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }

            throw new RemoteBuildException(
                ErrorCode.ResultTimeout,
                $"Timed out after {timeout.TotalSeconds}s waiting for Bridge result. Build ID: {buildId}");
        }

        /// <summary>
        /// Bridge results are unauthenticated on the wire (v1 security model is
        /// ACL + SHA-256 only), so Unity validates before trusting one. An
        /// existing-but-invalid result is a hard failure, not "keep polling" —
        /// silently ignoring a malformed result and waiting for a well-formed
        /// one risks masking a real problem (Codex plan review Round 2,
        /// confidence 0.90).
        /// </summary>
        internal static bool IsValidResult(BuildResult result, string expectedBuildId)
        {
            if (result == null)
            {
                return false;
            }

            if (result.ProtocolVersion != ProtocolConstants.CurrentProtocolVersion)
            {
                return false;
            }

            if (result.BuildId != expectedBuildId)
            {
                return false;
            }

            if (result.Status != "deployed" && result.Status != "failed")
            {
                return false;
            }

            if (result.Sha256 != null && !Sha256HexPattern.IsMatch(result.Sha256))
            {
                return false;
            }

            if (result.DeployedFileName != null && !PathUtility.IsSafeBasename(result.DeployedFileName))
            {
                return false;
            }

            return true;
        }

        internal const string AutoLaunchOffNote =
            "AutoLaunchVrchat is off on the Bridge: the world was installed but no VRChat was launched.";

        private const int MaxResultWarnings = 10;
        private const int MaxResultWarningLength = 500;

        /// <summary>
        /// Why the artifact cannot be uploaded to a Bridge with this limit, or null when it
        /// fits or the limit is not known (the Bridge then decides).
        /// </summary>
        internal static string DescribeOversize(long size, long? maxArtifactSizeBytes)
        {
            if (maxArtifactSizeBytes == null || maxArtifactSizeBytes.Value <= 0 || size <= maxArtifactSizeBytes.Value)
            {
                return null;
            }

            return $"The world is {BridgeMessages.FormatBytes(size)}, more than the Bridge accepts "
                + $"({BridgeMessages.FormatBytes(maxArtifactSizeBytes.Value)}, MaxArtifactSizeBytes). "
                + "Raise MaxArtifactSizeBytes on the Bridge (scripts/vrcrt.sh config set MaxArtifactSizeBytes=<bytes>) "
                + "or make the world smaller.";
        }

        /// <summary>
        /// What a result says beyond deployed/failed that the developer should see: that the
        /// Bridge launched no VRChat (AutoLaunchVrchat off), then its warnings. Empty for a
        /// Bridge before v0.8.0. The warnings are untrusted and are cut and stripped of controls.
        /// </summary>
        internal static IReadOnlyList<string> ResultNotes(BuildResult result)
        {
            var notes = new List<string>();
            if (result == null)
            {
                return notes;
            }

            if (result.Launch != null && result.Launch.Outcome == LaunchReport.Disabled)
            {
                notes.Add(AutoLaunchOffNote);
            }

            if (result.Warnings != null)
            {
                var shown = 0;
                foreach (var warning in result.Warnings)
                {
                    if (shown == MaxResultWarnings)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(warning))
                    {
                        notes.Add("Bridge warning: " + BridgeMessages.Sanitize(warning, MaxResultWarningLength));
                        shown++;
                    }
                }
            }

            return notes;
        }

        private static string WithNotes(string message, IReadOnlyList<string> notes) =>
            notes.Count == 0 ? message : message + "\n" + string.Join("\n", notes);

        private static string SanitizeErrorMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            return message.Length <= MaxErrorMessageLength
                ? message
                : message.Substring(0, MaxErrorMessageLength) + "...(truncated)";
        }

        private static void Report(
            IProgress<RemoteBuildProgress> progress, RemoteBuildStatus status, string message)
        {
            progress?.Report(new RemoteBuildProgress(status, message));
        }
    }
}

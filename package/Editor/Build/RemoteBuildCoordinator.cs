using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

        public bool IsShareReachable => _transport.IsAvailable;

        public bool IsSdkAvailable => _sdkAdapter.IsAvailable;

        /// <summary>
        /// Phase 4a advisory status (VRChat process running / --watch-worlds
        /// detected). Never gates build success — display only. Null if the
        /// Bridge hasn't written a status file yet or it couldn't be read.
        /// </summary>
        public VrchatStatus VrchatStatus => _transport.PollVrchatStatus();

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
                RunPreflight();

                Report(progress, RemoteBuildStatus.Running, "Building Windows world...");
                cancellationToken.ThrowIfCancellationRequested();
                var sdkPath = await _sdkAdapter.BuildWindowsWorldAsync().ConfigureAwait(false);
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

                Report(progress, RemoteBuildStatus.Running, "Uploading to remote share...");
                await _transport.UploadBuildAsync(artifact, manifest, buildId, cancellationToken).ConfigureAwait(false);
                Debug.Log($"[VRC Remote Test] Upload complete. Build ID: {buildId}");

                Report(progress, RemoteBuildStatus.Running, "Waiting for Bridge result...");
                var result = await PollForResultAsync(buildId, resultTimeout, pollInterval, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Status == "deployed")
                {
                    var message = $"Deployed as {result.DeployedFileName}";
                    Report(progress, RemoteBuildStatus.Succeeded, message);
                    Debug.Log($"[VRC Remote Test] {message}");
                    return RemoteBuildOutcome.Success(buildId, result);
                }

                var sanitizedMessage = SanitizeErrorMessage(result.ErrorMessage) ?? "Bridge reported failure.";
                Report(progress, RemoteBuildStatus.Failed, $"{result.ErrorCode}: {sanitizedMessage}");
                Debug.LogError($"[VRC Remote Test] Bridge error: {result.ErrorCode} - {sanitizedMessage}");
                return RemoteBuildOutcome.Failure(buildId, result.ErrorCode, sanitizedMessage);
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
                RunPreflight();

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

        private void RunPreflight()
        {
            if (EditorApplication.isPlaying)
            {
                throw new RemoteBuildException(
                    ErrorCode.PlayModeActive, "Cannot start a remote build while in Play Mode.");
            }

            if (!_transport.IsAvailable)
            {
                throw new RemoteBuildException(
                    ErrorCode.RemoteShareUnavailable,
                    "Remote share is not available. Check that the SMB share is mounted and writable.");
            }
        }

        private async Task<BuildResult> PollForResultAsync(
            string buildId, TimeSpan timeout, TimeSpan interval, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = _transport.PollResult(buildId);
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

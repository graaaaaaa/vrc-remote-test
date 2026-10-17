using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;

namespace VRCRemoteTest.Bridge.Deployment;

/// <summary>
/// The Bridge's core loop. Deliberately a "file promoter", not an orchestration
/// service: watch a staging directory, validate, atomically install into VRChat's
/// Worlds directory, write one result file, archive or quarantine. No heartbeat; with
/// AutoLaunchVrchat, the VRChat side of a deploy is delegated to
/// <see cref="IVrchatClientCoordinator"/>. Uses periodic polling as the
/// source of truth per spec section 51; a FileSystemWatcher can be layered on later
/// purely as a wake-up optimization without changing correctness.
///
/// Trust boundary: everything under the SMB share can be rewritten by the SMB
/// account at any time, so the Bridge never moves a shared file into its own state
/// (a same-volume move keeps the uploader's security descriptor) and never reopens
/// a shared file after validating it. Instead it copies bounded bytes into new
/// files under <see cref="BridgeOptions.StateDirectory"/>, validates that private
/// snapshot, and installs from the same snapshot (security review S7/S15).
/// </summary>
public sealed class StagingWatcher : BackgroundService, IBridgeWatcher
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Real manifests are a few hundred bytes; the cap only exists so an SMB writer
    /// cannot make the Bridge buffer an arbitrarily large file.
    /// </summary>
    internal const int MaxManifestBytes = 64 * 1024;

    private const string PartialSuffix = ".part";

    private const int CopyBufferSize = 81920;

    /// <summary>
    /// The Unity sender writes exactly the six <see cref="BuildManifest"/> fields, so
    /// an unknown member means a different protocol or a hand-crafted file.
    /// </summary>
    internal static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly BridgeOptions _options;
    private readonly IPackageValidator _validator;
    private readonly IWorldInstaller _installer;
    private readonly IResultWriter _resultWriter;
    private readonly ICleanupService _cleanupService;
    private readonly IVrchatClientCoordinator _clientCoordinator;
    private readonly VrchatLogNotice _logNotice;
    private readonly ILogger<StagingWatcher> _logger;

    public StagingWatcher(
        IOptions<BridgeOptions> options,
        IPackageValidator validator,
        IWorldInstaller installer,
        IResultWriter resultWriter,
        ICleanupService cleanupService,
        IVrchatClientCoordinator clientCoordinator,
        VrchatLogNotice logNotice,
        ILogger<StagingWatcher> logger)
    {
        _options = options.Value;
        _validator = validator;
        _installer = installer;
        _resultWriter = resultWriter;
        _cleanupService = cleanupService;
        _clientCoordinator = clientCoordinator;
        _logNotice = logNotice;
        _logger = logger;
    }

    // Shared with the Mac over SMB.
    private string IncomingDir => Path.Combine(_options.StagingDirectory, "incoming");
    private string ResultsDir => Path.Combine(_options.StagingDirectory, "results");

    // Bridge-private, outside the share.
    private string ProcessingDir => Path.Combine(_options.StateDirectory, "processing");
    private string ArchiveDir => Path.Combine(_options.StateDirectory, "archive");
    private string FailedDir => Path.Combine(_options.StateDirectory, "failed");

    private enum SnapshotOutcome
    {
        Copied,
        TooLarge,
        Unavailable,
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureDirectoriesExist();
        WarnAboutLegacySharedState();

        await RecoverProcessingAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error while scanning staging directory.");
            }

            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoriesExist();

        if (!Directory.Exists(IncomingDir))
        {
            return;
        }

        foreach (var readyFile in Directory.EnumerateFiles(IncomingDir, $"*{ProtocolConstants.ManifestExtension}"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // One file the Bridge cannot handle must not stall every build queued behind
            // it: the share is attacker-writable, so a single crafted name or a transient
            // I/O error would otherwise block ingestion on every scan.
            try
            {
                await ClaimAndProcessAsync(readyFile, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to ingest {File}; continuing with the next manifest.", readyFile);
            }
        }
    }

    /// <summary>
    /// Only private state is recovered. Partial snapshots left by a crash are never
    /// trusted, only discarded: the retried claim copies again from incoming/ or
    /// reports ArtifactNotFound. Builds left in a pre-upgrade shared processing/
    /// directory are deliberately not imported (see <see cref="WarnAboutLegacySharedState"/>).
    /// </summary>
    internal async Task RecoverProcessingAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoriesExist();

        foreach (var partial in Directory.EnumerateFiles(ProcessingDir, $"*{PartialSuffix}"))
        {
            SafeDelete(partial);
        }

        // Retries any pruning a previous run could not finish.
        _cleanupService.CleanupFailedArtifacts(FailedDir, _options.RetainBuilds);

        foreach (var readyFile in Directory.EnumerateFiles(ProcessingDir, $"*{ProtocolConstants.ManifestExtension}"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Recovering unfinished build left in processing/: {File}", readyFile);
            await ProcessClaimedManifestSafelyAsync(readyFile, cancellationToken);
        }
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(IncomingDir);
        Directory.CreateDirectory(ResultsDir);
        Directory.CreateDirectory(ProcessingDir);
        Directory.CreateDirectory(ArchiveDir);
        Directory.CreateDirectory(FailedDir);
    }

    private void WarnAboutLegacySharedState()
    {
        foreach (var name in new[] { "processing", "archive", "failed" })
        {
            var legacyDirectory = Path.Combine(_options.StagingDirectory, name);
            if (Directory.Exists(legacyDirectory))
            {
                _logger.LogWarning(
                    "Ignoring legacy {LegacyDirectory} inside the share: Bridge state now lives in {StateDirectory} " +
                    "and is never read back from the share. Remove it manually (see docs/setup-windows.md).",
                    legacyDirectory,
                    _options.StateDirectory);
            }
        }
    }

    private async Task ClaimAndProcessAsync(string readyFilePath, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(readyFilePath);
        var claimedPath = Path.Combine(ProcessingDir, fileName);

        if (IsReparsePoint(readyFilePath))
        {
            _logger.LogWarning("Rejecting {File}: links are never ingested from the share.", readyFilePath);
            SafeDelete(readyFilePath); // removes the link itself, never its target
            return;
        }

        byte[]? manifestBytes;
        try
        {
            manifestBytes = ReadBounded(readyFilePath, MaxManifestBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open on the Mac side, or gone since enumeration: the next scan retries.
            return;
        }

        if (manifestBytes is null)
        {
            _logger.LogWarning(
                "Rejecting {File}: manifest exceeds {Limit} bytes.", readyFilePath, MaxManifestBytes);
            SafeDelete(readyFilePath);
            WriteFailureForGuessedBuildId(
                GuessBuildId(fileName), ErrorCode.ManifestInvalid, $"Manifest exceeds {MaxManifestBytes} bytes.");
            return;
        }

        if (!TryPublishPrivateFile(claimedPath, manifestBytes))
        {
            // Normal processing never leaves a claim behind between scans, so this one was
            // stranded by a crash or by a withdrawal (below) that could not delete it. Once
            // the shared copy is consumed, resume the trusted private claim as recovery
            // would, rather than leaving it until the next restart (Codex code review
            // Phase 0, Round 3). The shared copy's content is never used.
            _logger.LogWarning(
                "{File} is already claimed in processing/; resuming the private claim.", readyFilePath);
            if (SafeDelete(readyFilePath))
            {
                await ProcessClaimedManifestSafelyAsync(claimedPath, cancellationToken);
            }

            return;
        }

        // The claim only counts once the shared copy is consumed, as with the original
        // move-based claim: a shared manifest that survives (e.g. still open on the Mac)
        // would otherwise be claimed again on the next scan and its BuildAlreadyProcessed
        // result would overwrite this build's real result.
        if (!SafeDelete(readyFilePath))
        {
            SafeDelete(claimedPath);
            return;
        }

        await ProcessClaimedManifestSafelyAsync(claimedPath, cancellationToken);
    }

    /// <summary>
    /// Every claim ends in archive/ or failed/ with a result, even when something
    /// unexpected throws (e.g. an access error hashing the snapshot): a claim stranded
    /// in processing/ is never retried by the normal scan, and an exception escaping
    /// recovery would stop the Bridge from scanning at all (Codex code review Phase 0,
    /// Round 2). Cancellation keeps the claim for recovery at the next start. Once a
    /// cold start has archived the build, the manifest stays in archive/ whatever fails
    /// next, so the build is never deployed again.
    ///
    /// A Relaunch notice in the Log Viewer never outlives the build that put it up: the
    /// coordinator clears it itself, except when the build stops between a cold start
    /// and the launch (the install or the archive fails), which only this flow sees.
    /// </summary>
    private async Task ProcessClaimedManifestSafelyAsync(string claimedManifestPath, CancellationToken cancellationToken)
    {
        var progress = new ClaimProgress();
        try
        {
            await ProcessClaimedManifestAsync(claimedManifestPath, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (progress.Archived)
            {
                _logger.LogError(ex, "Unexpected error after archiving {Path}.", claimedManifestPath);
            }
            else
            {
                _logger.LogError(ex, "Unexpected error while processing {Path}; moving it to failed/.", claimedManifestPath);
                MoveToFailed(claimedManifestPath, progress.ArtifactPath);
            }

            const string message = "Unexpected error while processing the build.";
            try
            {
                if (progress.BuildId is not null)
                {
                    _resultWriter.WriteFailure(progress.BuildId, ErrorCode.UnknownError, message, ResultsDir);
                }
                else
                {
                    WriteFailureForGuessedBuildId(
                        GuessBuildId(Path.GetFileName(claimedManifestPath)), ErrorCode.UnknownError, message);
                }
            }
            catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(writeEx, "Could not write the failure result for {Path}.", claimedManifestPath);
            }
        }
        finally
        {
            _logNotice.Clear();
        }
    }

    /// <summary>What has been established about a claim, for the failure path above.</summary>
    private sealed class ClaimProgress
    {
        public string? BuildId { get; set; }

        public string? ArtifactPath { get; set; }

        /// <summary>Set only once the move into archive/ is known to have succeeded.</summary>
        public bool Archived { get; set; }
    }

    private async Task ProcessClaimedManifestAsync(
        string claimedManifestPath, ClaimProgress progress, CancellationToken cancellationToken)
    {
        var buildIdGuess = GuessBuildId(Path.GetFileName(claimedManifestPath));

        BuildManifest? manifest;
        try
        {
            var json = await File.ReadAllTextAsync(claimedManifestPath, cancellationToken);
            manifest = JsonSerializer.Deserialize<BuildManifest>(json, DeserializeOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Failed to parse manifest {Path}", claimedManifestPath);
            MoveToFailed(claimedManifestPath, artifactPath: null);
            WriteFailureForGuessedBuildId(buildIdGuess, ErrorCode.ManifestInvalid, "Manifest could not be parsed.");
            return;
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.BuildId))
        {
            MoveToFailed(claimedManifestPath, artifactPath: null);
            WriteFailureForGuessedBuildId(buildIdGuess, ErrorCode.ManifestInvalid, "Manifest missing buildId.");
            return;
        }

        // manifest.BuildId must be validated BEFORE it is used in any Path.Combine
        // below (the idempotency archive-path check immediately below this, plus
        // WorldInstaller/ResultWriter further downstream): only null/whitespace was
        // being rejected, so a buildId with an embedded path separator (e.g.
        // "x\..\..\evil") could traverse outside the intended directory once
        // combined with a literal prefix like "vrc-remote-" — most severely in
        // WorldInstaller, which writes into VRChat's live Worlds directory
        // (Codex code review, confidence 0.85).
        var buildIdResult = PackageValidator.ValidateBuildId(manifest.BuildId);
        if (buildIdResult is not null)
        {
            _logger.LogWarning(
                "Build {BuildId} failed validation: {Code} {Message}",
                manifest.BuildId, buildIdResult.ErrorCode, buildIdResult.ErrorMessage);
            MoveToFailed(claimedManifestPath, artifactPath: null);
            WriteFailureForGuessedBuildId(
                buildIdGuess,
                buildIdResult.ErrorCode ?? ErrorCode.UnknownError,
                buildIdResult.ErrorMessage ?? "Validation failed.");
            return;
        }

        progress.BuildId = manifest.BuildId;

        // manifest.FileName must be validated BEFORE it is ever handed to
        // Path.Combine below: Path.Combine silently discards its first argument
        // if the second is a rooted/absolute path, so an unvalidated fileName
        // could otherwise steer the subsequent copy entirely outside
        // ProcessingDir/IncomingDir. PackageValidator.Validate() also checks this,
        // but only after the snapshot step already ran (Codex code review,
        // confidence 0.85).
        var fileNameResult = PackageValidator.ValidateFileName(manifest.FileName);
        if (fileNameResult is not null)
        {
            _logger.LogWarning(
                "Build {BuildId} failed validation: {Code} {Message}",
                manifest.BuildId, fileNameResult.ErrorCode, fileNameResult.ErrorMessage);
            MoveToFailed(claimedManifestPath, artifactPath: null);
            _resultWriter.WriteFailure(
                manifest.BuildId,
                fileNameResult.ErrorCode ?? ErrorCode.UnknownError,
                fileNameResult.ErrorMessage ?? "Validation failed.",
                ResultsDir);
            return;
        }

        var artifactPath = Path.Combine(ProcessingDir, manifest.FileName);
        var incomingArtifactPath = Path.Combine(IncomingDir, manifest.FileName);
        progress.ArtifactPath = artifactPath;

        // Idempotency: never reprocess a build that already reached archive/. The
        // duplicate upload is consumed so it does not linger in the share; note the
        // BuildAlreadyProcessed result replaces the earlier result file.
        var archivedManifestPath = Path.Combine(ArchiveDir, $"{manifest.BuildId}{ProtocolConstants.ManifestExtension}");
        if (File.Exists(archivedManifestPath))
        {
            _logger.LogInformation("Build {BuildId} was already processed, skipping.", manifest.BuildId);
            _resultWriter.WriteFailure(manifest.BuildId, ErrorCode.BuildAlreadyProcessed, "This build was already processed.", ResultsDir);
            SafeDelete(claimedManifestPath);
            SafeDelete(incomingArtifactPath);
            return;
        }

        // Reject what the manifest alone proves invalid before copying a possibly
        // large artifact. The shared artifact is consumed either way, as it was when
        // invalid builds were moved to failed/.
        var declaredResult = PackageValidator.ValidateProtocolVersion(manifest)
            ?? PackageValidator.ValidateDeclaredArtifact(manifest, _options.MaxArtifactSizeBytes);
        if (declaredResult is not null)
        {
            _logger.LogWarning(
                "Build {BuildId} failed validation: {Code} {Message}",
                manifest.BuildId, declaredResult.ErrorCode, declaredResult.ErrorMessage);
            SafeDelete(incomingArtifactPath);
            MoveToFailed(claimedManifestPath, artifactPath);
            _resultWriter.WriteFailure(
                manifest.BuildId,
                declaredResult.ErrorCode ?? ErrorCode.UnknownError,
                declaredResult.ErrorMessage ?? "Validation failed.",
                ResultsDir);
            return;
        }

        // A complete snapshot left by a crash is reused (and fully revalidated below).
        if (!File.Exists(artifactPath) && File.Exists(incomingArtifactPath))
        {
            var outcome = CopyIntoPrivate(
                incomingArtifactPath, artifactPath, _options.MaxArtifactSizeBytes, cancellationToken);

            if (outcome == SnapshotOutcome.TooLarge)
            {
                var message = $"Artifact exceeds maximum {_options.MaxArtifactSizeBytes} bytes.";
                _logger.LogWarning("Build {BuildId} rejected: {Message}", manifest.BuildId, message);
                SafeDelete(incomingArtifactPath);
                MoveToFailed(claimedManifestPath, artifactPath: null);
                _resultWriter.WriteFailure(manifest.BuildId, ErrorCode.SizeExceeded, message, ResultsDir);
                return;
            }

            if (outcome == SnapshotOutcome.Copied)
            {
                SafeDelete(incomingArtifactPath);
            }

            // Unavailable (e.g. still open for writing on the Mac): fall through; the
            // validator reports ArtifactNotFound and the shared copy is left in place.
        }

        var validation = _validator.Validate(manifest, artifactPath, _options.MaxArtifactSizeBytes);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "Build {BuildId} failed validation: {Code} {Message}",
                manifest.BuildId, validation.ErrorCode, validation.ErrorMessage);
            MoveToFailed(claimedManifestPath, artifactPath);
            _resultWriter.WriteFailure(
                manifest.BuildId,
                validation.ErrorCode ?? ErrorCode.UnknownError,
                validation.ErrorMessage ?? "Validation failed.",
                ResultsDir);
            return;
        }

        var preparation = VrchatPreparation.DeployOnly();
        if (_options.AutoLaunchVrchat)
        {
            try
            {
                preparation = await _clientCoordinator.PrepareAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Without this catch, an unexpected exception here would escape to
                // ExecuteAsync's outer catch, leaving no BuildResult written at
                // all -- Unity would then hit a generic RESULT_TIMEOUT instead of
                // an immediate, informative VRCHAT_START_FAILED (Codex plan
                // review Phase 4.1, Round 2, confidence 0.93).
                _logger.LogError(
                    ex, "Unexpected error during VRChat readiness check for build {BuildId}.", manifest.BuildId);
                MoveToFailed(claimedManifestPath, artifactPath);
                _resultWriter.WriteFailure(manifest.BuildId, ErrorCode.VrchatStartFailed, ex.Message, ResultsDir);
                return;
            }

            if (preparation.Kind == VrchatPreparationKind.Failed)
            {
                _logger.LogWarning(
                    "Build {BuildId}: VRChat readiness check failed: {Code} {Message}",
                    manifest.BuildId, preparation.ErrorCode, preparation.ErrorMessage);
                MoveToFailed(claimedManifestPath, artifactPath);
                _resultWriter.WriteFailure(
                    manifest.BuildId, preparation.ErrorCode!, preparation.ErrorMessage!, ResultsDir);
                return;
            }
        }

        string deployedFileName;
        try
        {
            deployedFileName = _installer.Install(artifactPath, manifest.BuildId, _options.VrchatWorldsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Deploy failed for build {BuildId}.", manifest.BuildId);
            MoveToFailed(claimedManifestPath, artifactPath);
            _resultWriter.WriteFailure(manifest.BuildId, ErrorCode.DeployFailed, ex.Message, ResultsDir);
            return;
        }

        if (preparation.Kind == VrchatPreparationKind.ColdStart)
        {
            await LaunchAfterDeployAsync(
                claimedManifestPath, artifactPath, manifest, deployedFileName, progress, cancellationToken);
            return;
        }

        try
        {
            _resultWriter.WriteSuccess(manifest.BuildId, deployedFileName, manifest.Sha256, ResultsDir);
            TryArchiveProcessed(claimedManifestPath, artifactPath, manifest.BuildId);
            _cleanupService.Cleanup(_options.VrchatWorldsDirectory, _options.RetainBuilds, deployedFileName);
            _logger.LogInformation("Build {BuildId} deployed as {DeployedFileName}.", manifest.BuildId, deployedFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Deploy failed for build {BuildId}.", manifest.BuildId);
            MoveToFailed(claimedManifestPath, artifactPath);
            _resultWriter.WriteFailure(manifest.BuildId, ErrorCode.DeployFailed, ex.Message, ResultsDir);
        }
    }

    /// <summary>
    /// Cold start: the build is installed, archived, and only then are the clients
    /// launched with --url pointing at the installed file. Archiving first is what keeps
    /// a Bridge that stops mid-launch from deploying the build again on restart (which
    /// would reload the clients that did start); the result is then never written and
    /// Unity reports RESULT_TIMEOUT. If the archive cannot be confirmed, nothing is
    /// launched and the build fails the same way as an unexpected error (multi-client
    /// plan section 13.1). The installed file stays in Worlds either way, and nothing
    /// is reading it until a client starts.
    /// </summary>
    private async Task LaunchAfterDeployAsync(
        string claimedManifestPath,
        string artifactPath,
        BuildManifest manifest,
        string deployedFileName,
        ClaimProgress progress,
        CancellationToken cancellationToken)
    {
        if (!TryArchiveProcessed(claimedManifestPath, artifactPath, manifest.BuildId))
        {
            MoveToFailed(claimedManifestPath, artifactPath);
            _resultWriter.WriteFailure(
                manifest.BuildId,
                ErrorCode.UnknownError,
                "The build was installed but could not be recorded as processed, so VRChat was not launched.",
                ResultsDir);
            return;
        }

        progress.Archived = true;

        var deployedWorldPath = Path.GetFullPath(Path.Combine(_options.VrchatWorldsDirectory, deployedFileName));
        VrchatLaunchOutcome outcome;
        try
        {
            outcome = await _clientCoordinator.LaunchClientsAsync(deployedWorldPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error while launching VRChat for build {BuildId}.", manifest.BuildId);
            outcome = VrchatLaunchOutcome.Failure(ex.Message);
        }

        // A Bridge that is stopping publishes neither outcome: the build stays archived
        // and Unity reports RESULT_TIMEOUT, the same as a stop between two launches.
        cancellationToken.ThrowIfCancellationRequested();

        if (outcome.Succeeded)
        {
            _resultWriter.WriteSuccess(manifest.BuildId, deployedFileName, manifest.Sha256, ResultsDir);
            _logger.LogInformation(
                "Build {BuildId} deployed as {DeployedFileName} and VRChat launched with it.",
                manifest.BuildId, deployedFileName);
        }
        else
        {
            _resultWriter.WriteFailure(
                manifest.BuildId, outcome.ErrorCode ?? ErrorCode.VrchatStartFailed, outcome.ErrorMessage!, ResultsDir);
        }

        _cleanupService.Cleanup(_options.VrchatWorldsDirectory, _options.RetainBuilds, deployedFileName);
    }

    /// <summary>
    /// Returns null when the file holds more than <paramref name="limit"/> bytes, so
    /// an oversized file is never fully buffered.
    /// </summary>
    private static byte[]? ReadBounded(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var buffer = new byte[limit + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total > limit ? null : buffer[..total];
    }

    /// <summary>
    /// Writes a fully flushed private copy and claims <paramref name="finalPath"/> with
    /// a non-overwriting rename, so a crash can never leave a half-written file under
    /// the committed name. Returns false when <paramref name="finalPath"/> already exists.
    /// </summary>
    private bool TryPublishPrivateFile(string finalPath, byte[] content)
    {
        var partialPath = NewPartialPath(finalPath);

        try
        {
            using (var stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(partialPath, finalPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            return false;
        }
        finally
        {
            SafeDelete(partialPath);
        }
    }

    /// <summary>
    /// Copies the shared artifact into a new private file, enforcing the size limit on
    /// the bytes actually read (the manifest's declared size is only a claim). The
    /// source is opened without write sharing, so it cannot change during the copy;
    /// the partial file is removed on every path that does not publish it.
    /// </summary>
    private SnapshotOutcome CopyIntoPrivate(
        string sourcePath, string destinationPath, long maxBytes, CancellationToken cancellationToken)
    {
        if (IsReparsePoint(sourcePath))
        {
            _logger.LogWarning("Not copying {Source}: links are never ingested from the share.", sourcePath);
            return SnapshotOutcome.Unavailable;
        }

        var partialPath = NewPartialPath(destinationPath);

        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[CopyBufferSize];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Compared before adding so the running total can never overflow.
                    if (read > maxBytes - total)
                    {
                        return SnapshotOutcome.TooLarge;
                    }

                    total += read;
                    destination.Write(buffer, 0, read);
                }

                destination.Flush(flushToDisk: true);
            }

            File.Move(partialPath, destinationPath, overwrite: false);
            return SnapshotOutcome.Copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy {Source} into private state.", sourcePath);
            return SnapshotOutcome.Unavailable;
        }
        finally
        {
            SafeDelete(partialPath);
        }
    }

    /// <summary>
    /// A short random name in the same directory rather than "{name}.part": the final
    /// name comes from the share, and a 251-character name plus ".part" would exceed
    /// NTFS's 255-character component limit and fail every attempt (Codex code review
    /// Phase 0, Round 1). Leftovers still match "*.part" for startup cleanup.
    /// </summary>
    private static string NewPartialPath(string finalPath) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, $"{Guid.NewGuid():N}{PartialSuffix}");

    /// <summary>
    /// A standard SMB account normally cannot create links, but a shared file that is
    /// one would make the Bridge read (and snapshot) whatever it points at, so links
    /// are never ingested (Codex code review Phase 0, Round 1).
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The subsequent open reports the real problem.
            return false;
        }
    }

    private static string GuessBuildId(string manifestFileName) =>
        manifestFileName.Replace(ProtocolConstants.ManifestExtension, string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// The guess comes from a shared filename rather than a validated manifest, so it
    /// must pass the same allow-list before it names a file in results/.
    /// </summary>
    private void WriteFailureForGuessedBuildId(string buildIdGuess, string errorCode, string errorMessage)
    {
        if (PackageValidator.ValidateBuildId(buildIdGuess) is not null)
        {
            _logger.LogWarning(
                "Not writing a result for unsafe build id guess {BuildIdGuess}: {Code} {Message}",
                buildIdGuess, errorCode, errorMessage);
            return;
        }

        _resultWriter.WriteFailure(buildIdGuess, errorCode, errorMessage, ResultsDir);
    }

    /// <summary>
    /// Only the manifest is needed for idempotency; the artifact already lives in the
    /// Worlds directory (bounded by RetainBuilds), so keeping a second copy here would
    /// only grow private state without limit. The snapshot is only removed once the
    /// manifest reached archive/ (otherwise recovery redeploys from it), and a failed
    /// move is reported to the caller instead of only logged: a cold start must not
    /// launch VRChat for a build that recovery would deploy again.
    /// </summary>
    private bool TryArchiveProcessed(string manifestPath, string artifactPath, string buildId)
    {
        var archivedManifest = Path.Combine(ArchiveDir, $"{buildId}{ProtocolConstants.ManifestExtension}");
        if (!SafeMove(manifestPath, archivedManifest))
        {
            return false;
        }

        SafeDelete(artifactPath);
        return true;
    }

    private void MoveToFailed(string manifestPath, string? artifactPath)
    {
        if (File.Exists(manifestPath))
        {
            SafeMove(manifestPath, Path.Combine(FailedDir, Path.GetFileName(manifestPath)));
        }

        // Failed artifacts are kept for inspection, but only the newest RetainBuilds of
        // them. Pruned only when one was added, so a flood of manifest-only failures
        // cannot turn every rejection into a scan of the growing failed/ directory
        // (Codex code review Phase 0, Round 1).
        if (artifactPath is not null && File.Exists(artifactPath))
        {
            SafeMove(artifactPath, Path.Combine(FailedDir, Path.GetFileName(artifactPath)));
            _cleanupService.CleanupFailedArtifacts(FailedDir, _options.RetainBuilds);
        }
    }

    /// <summary>Returns false (after logging) when the move did not happen.</summary>
    private bool SafeMove(string source, string destination)
    {
        try
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to move {Source} to {Destination}.", source, destination);
            return false;
        }
    }

    /// <summary>Returns false only when an existing file could not be deleted.</summary>
    private bool SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to delete {Path}.", path);
            return false;
        }
    }
}

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.Deployment;

/// <summary>
/// The Bridge's core loop. Deliberately a "file promoter", not an orchestration
/// service: watch a staging directory, validate, atomically install into VRChat's
/// Worlds directory, write one result file, archive or quarantine. No heartbeat, no
/// VRChat process interaction (deferred to Phase 4). Uses periodic polling as the
/// source of truth per spec section 51; a FileSystemWatcher can be layered on later
/// purely as a wake-up optimization without changing correctness.
/// </summary>
public sealed class StagingWatcher : BackgroundService, IBridgeWatcher
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly BridgeOptions _options;
    private readonly IPackageValidator _validator;
    private readonly IWorldInstaller _installer;
    private readonly IResultWriter _resultWriter;
    private readonly ICleanupService _cleanupService;
    private readonly ILogger<StagingWatcher> _logger;

    public StagingWatcher(
        IOptions<BridgeOptions> options,
        IPackageValidator validator,
        IWorldInstaller installer,
        IResultWriter resultWriter,
        ICleanupService cleanupService,
        ILogger<StagingWatcher> logger)
    {
        _options = options.Value;
        _validator = validator;
        _installer = installer;
        _resultWriter = resultWriter;
        _cleanupService = cleanupService;
        _logger = logger;
    }

    private string IncomingDir => Path.Combine(_options.StagingDirectory, "incoming");
    private string ProcessingDir => Path.Combine(_options.StagingDirectory, "processing");
    private string ArchiveDir => Path.Combine(_options.StagingDirectory, "archive");
    private string FailedDir => Path.Combine(_options.StagingDirectory, "failed");
    private string ResultsDir => Path.Combine(_options.StagingDirectory, "results");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureDirectoriesExist();

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
            await ClaimAndProcessAsync(readyFile, cancellationToken);
        }
    }

    internal async Task RecoverProcessingAsync(CancellationToken cancellationToken)
    {
        EnsureDirectoriesExist();

        if (!Directory.Exists(ProcessingDir))
        {
            return;
        }

        foreach (var readyFile in Directory.EnumerateFiles(ProcessingDir, $"*{ProtocolConstants.ManifestExtension}"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning("Recovering unfinished build left in processing/: {File}", readyFile);
            await ProcessClaimedManifestAsync(readyFile, cancellationToken);
        }
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(IncomingDir);
        Directory.CreateDirectory(ProcessingDir);
        Directory.CreateDirectory(ArchiveDir);
        Directory.CreateDirectory(FailedDir);
        Directory.CreateDirectory(ResultsDir);
    }

    private async Task ClaimAndProcessAsync(string readyFilePath, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(readyFilePath);
        var claimedPath = Path.Combine(ProcessingDir, fileName);

        try
        {
            // Atomic move = the claim. If two passes race (watcher + periodic scan),
            // only one succeeds; the other simply skips this file.
            File.Move(readyFilePath, claimedPath, overwrite: false);
        }
        catch (IOException)
        {
            return;
        }

        await ProcessClaimedManifestAsync(claimedPath, cancellationToken);
    }

    private async Task ProcessClaimedManifestAsync(string claimedManifestPath, CancellationToken cancellationToken)
    {
        var buildIdGuess = Path.GetFileName(claimedManifestPath)
            .Replace(ProtocolConstants.ManifestExtension, string.Empty, StringComparison.Ordinal);

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
            _resultWriter.WriteFailure(buildIdGuess, ErrorCode.ManifestInvalid, "Manifest could not be parsed.", ResultsDir);
            return;
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.BuildId))
        {
            MoveToFailed(claimedManifestPath, artifactPath: null);
            _resultWriter.WriteFailure(buildIdGuess, ErrorCode.ManifestInvalid, "Manifest missing buildId.", ResultsDir);
            return;
        }

        // Idempotency: never reprocess a build that already reached archive/.
        var archivedManifestPath = Path.Combine(ArchiveDir, $"{manifest.BuildId}{ProtocolConstants.ManifestExtension}");
        if (File.Exists(archivedManifestPath))
        {
            _logger.LogInformation("Build {BuildId} was already processed, skipping.", manifest.BuildId);
            _resultWriter.WriteFailure(manifest.BuildId, ErrorCode.BuildAlreadyProcessed, "This build was already processed.", ResultsDir);
            SafeDelete(claimedManifestPath);
            return;
        }

        var artifactPath = Path.Combine(ProcessingDir, manifest.FileName);
        var incomingArtifactPath = Path.Combine(IncomingDir, manifest.FileName);
        if (!File.Exists(artifactPath) && File.Exists(incomingArtifactPath))
        {
            try
            {
                File.Move(incomingArtifactPath, artifactPath, overwrite: false);
            }
            catch (IOException)
            {
                // Fall through; validator reports ArtifactNotFound if it truly never arrives.
            }
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

        try
        {
            var deployedFileName = _installer.Install(artifactPath, manifest.BuildId, _options.VrchatWorldsDirectory);
            _resultWriter.WriteSuccess(manifest.BuildId, deployedFileName, manifest.Sha256, ResultsDir);
            ArchiveProcessed(claimedManifestPath, artifactPath, manifest.BuildId);
            _cleanupService.Cleanup(_options.VrchatWorldsDirectory, _options.RetainBuilds);
            _logger.LogInformation("Build {BuildId} deployed as {DeployedFileName}.", manifest.BuildId, deployedFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Deploy failed for build {BuildId}.", manifest.BuildId);
            MoveToFailed(claimedManifestPath, artifactPath);
            _resultWriter.WriteFailure(manifest.BuildId, ErrorCode.DeployFailed, ex.Message, ResultsDir);
        }
    }

    private void ArchiveProcessed(string manifestPath, string artifactPath, string buildId)
    {
        var archivedManifest = Path.Combine(ArchiveDir, $"{buildId}{ProtocolConstants.ManifestExtension}");
        SafeMove(manifestPath, archivedManifest);

        if (File.Exists(artifactPath))
        {
            var archivedArtifact = Path.Combine(ArchiveDir, Path.GetFileName(artifactPath));
            SafeMove(artifactPath, archivedArtifact);
        }
    }

    private void MoveToFailed(string manifestPath, string? artifactPath)
    {
        if (File.Exists(manifestPath))
        {
            SafeMove(manifestPath, Path.Combine(FailedDir, Path.GetFileName(manifestPath)));
        }

        if (artifactPath is not null && File.Exists(artifactPath))
        {
            SafeMove(artifactPath, Path.Combine(FailedDir, Path.GetFileName(artifactPath)));
        }
    }

    private void SafeMove(string source, string destination)
    {
        try
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to move {Source} to {Destination}.", source, destination);
        }
    }

    private void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to delete {Path}.", path);
        }
    }
}

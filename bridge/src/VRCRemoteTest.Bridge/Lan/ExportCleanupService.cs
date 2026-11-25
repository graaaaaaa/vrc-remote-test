using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.Lan;

/// <summary>
/// Deletes the full-log exports this Bridge wrote to state/exports (POST /log-exports) once they
/// are a day old, when a host starts and every hour, so repeated runs do not fill the Windows
/// disk. Only files named as the export code names them (32 hex digits, .log or .part) are
/// touched; links, folders and anything else are left alone. An export the Mac has not fetched
/// within a day is gone: fetch exports right after asking for them, as the CLI does.
/// </summary>
public sealed class ExportCleanupService : BackgroundService
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private static readonly Regex ExportName = new("^[0-9a-f]{32}\\.(log|part)$", RegexOptions.CultureInvariant);

    private readonly string _directory;
    private readonly ILogger<ExportCleanupService> _logger;

    public ExportCleanupService(IOptions<BridgeOptions> options, ILogger<ExportCleanupService> logger)
    {
        _directory = Path.Combine(options.Value.StateDirectory, "exports");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CleanOnce(_directory, DateTime.UtcNow, _logger);
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Deletes the exports in <paramref name="directory"/> last written before now minus <see cref="MaxAge"/>; returns how many.</summary>
    internal static int CleanOnce(string directory, DateTime utcNow, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (!ExportName.IsMatch(file.Name)
                    || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || utcNow - file.LastWriteTimeUtc < MaxAge)
                {
                    continue;
                }

                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Could not delete the old export {Name}: {Message}", file.Name, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not list the exports to clean up: {Message}", ex.Message);
        }

        if (deleted > 0)
        {
            logger.LogInformation("Deleted {Count} log export(s) older than {Hours} h.", deleted, MaxAge.TotalHours);
        }

        return deleted;
    }
}

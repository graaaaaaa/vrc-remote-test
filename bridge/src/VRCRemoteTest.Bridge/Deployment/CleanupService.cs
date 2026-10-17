namespace VRCRemoteTest.Bridge.Deployment;

public sealed class CleanupService : ICleanupService
{
    private const string ToolOwnedFilePattern = "vrc-remote-*.vrcw";

    private const string FailedArtifactPattern = "*.vrcw";

    public void Cleanup(string worldsDirectory, int retainBuilds, string keepFileName) =>
        RetainNewest(worldsDirectory, ToolOwnedFilePattern, retainBuilds, keepFileName);

    public void CleanupFailedArtifacts(string failedDirectory, int retainBuilds) =>
        RetainNewest(failedDirectory, FailedArtifactPattern, retainBuilds, keepFileName: null);

    private static void RetainNewest(string directory, string pattern, int retainBuilds, string? keepFileName)
    {
        if (retainBuilds <= 0 || !Directory.Exists(directory))
        {
            return;
        }

        // Enumeration is best-effort too: a cleanup failure must never fail a deploy
        // that already succeeded, nor stop the Bridge's startup recovery (Codex code
        // review Phase 0, Round 2).
        FileInfo[] staleFiles;
        try
        {
            var files = Directory.GetFiles(directory, pattern).Select(path => new FileInfo(path)).ToList();

            // Ordering by timestamp alone could select the file just deployed when other
            // files carry later timestamps (e.g. copied in with a skewed clock).
            var retainOthers = retainBuilds;
            if (keepFileName is not null
                && files.RemoveAll(f => string.Equals(f.Name, keepFileName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                retainOthers--;
            }

            staleFiles = files
                .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
                .Skip(retainOthers)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var stale in staleFiles)
        {
            try
            {
                stale.Delete();
            }
            catch (IOException)
            {
                // Best-effort: a cleanup failure must never fail the deploy pipeline.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort, same as above.
            }
        }
    }
}

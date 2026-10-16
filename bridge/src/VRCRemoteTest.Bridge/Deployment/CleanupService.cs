namespace VRCRemoteTest.Bridge.Deployment;

public sealed class CleanupService : ICleanupService
{
    private const string ToolOwnedFilePattern = "vrc-remote-*.vrcw";

    private const string FailedArtifactPattern = "*.vrcw";

    public void Cleanup(string worldsDirectory, int retainBuilds) =>
        RetainNewest(worldsDirectory, ToolOwnedFilePattern, retainBuilds);

    public void CleanupFailedArtifacts(string failedDirectory, int retainBuilds) =>
        RetainNewest(failedDirectory, FailedArtifactPattern, retainBuilds);

    private static void RetainNewest(string directory, string pattern, int retainBuilds)
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
            staleFiles = Directory.GetFiles(directory, pattern)
                .Select(path => new FileInfo(path))
                .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
                .Skip(retainBuilds)
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

namespace VRCRemoteTest.Bridge.Deployment;

public sealed class CleanupService : ICleanupService
{
    private const string ToolOwnedFilePattern = "vrc-remote-*.vrcw";

    public void Cleanup(string worldsDirectory, int retainBuilds)
    {
        if (retainBuilds <= 0 || !Directory.Exists(worldsDirectory))
        {
            return;
        }

        var staleFiles = Directory.GetFiles(worldsDirectory, ToolOwnedFilePattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
            .Skip(retainBuilds);

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

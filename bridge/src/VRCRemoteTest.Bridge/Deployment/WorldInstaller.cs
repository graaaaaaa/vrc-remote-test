namespace VRCRemoteTest.Bridge.Deployment;

/// <summary>
/// Copies to a temp filename on the destination filesystem, then renames into place.
/// Never streams directly into the final `vrc-remote-{buildId}.vrcw` path, so
/// --watch-worlds can never observe a partial file.
/// </summary>
public sealed class WorldInstaller : IWorldInstaller
{
    public string Install(string validatedArtifactPath, string buildId, string worldsDirectory)
    {
        Directory.CreateDirectory(worldsDirectory);

        var deployedFileName = $"vrc-remote-{buildId}.vrcw";
        var tempPath = Path.Combine(worldsDirectory, $".{deployedFileName}.tmp");
        var finalPath = Path.Combine(worldsDirectory, deployedFileName);

        File.Copy(validatedArtifactPath, tempPath, overwrite: true);
        File.Move(tempPath, finalPath, overwrite: true);

        return deployedFileName;
    }
}

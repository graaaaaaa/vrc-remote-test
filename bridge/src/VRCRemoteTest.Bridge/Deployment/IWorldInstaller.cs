namespace VRCRemoteTest.Bridge.Deployment;

public interface IWorldInstaller
{
    /// <summary>
    /// Atomically installs an already-validated artifact into the VRChat Worlds
    /// directory under a unique, tool-prefixed filename. Returns the deployed filename.
    /// </summary>
    string Install(string validatedArtifactPath, string buildId, string worldsDirectory);
}

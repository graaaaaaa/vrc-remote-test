namespace VRCRemoteTest.Bridge.Deployment;

public interface ICleanupService
{
    /// <summary>
    /// Deletes all but the newest <paramref name="retainBuilds"/> files matching the
    /// `vrc-remote-*.vrcw` pattern. Never touches any other file in the directory.
    /// </summary>
    void Cleanup(string worldsDirectory, int retainBuilds);
}

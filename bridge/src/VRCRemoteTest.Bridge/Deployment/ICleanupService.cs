namespace VRCRemoteTest.Bridge.Deployment;

public interface ICleanupService
{
    /// <summary>
    /// Deletes all but the newest <paramref name="retainBuilds"/> files matching the
    /// `vrc-remote-*.vrcw` pattern. Never touches any other file in the directory.
    /// </summary>
    void Cleanup(string worldsDirectory, int retainBuilds);

    /// <summary>
    /// Deletes all but the newest <paramref name="retainBuilds"/> `*.vrcw` files in the
    /// Bridge-private failed/ directory; quarantined manifests are small and kept.
    /// </summary>
    void CleanupFailedArtifacts(string failedDirectory, int retainBuilds);
}

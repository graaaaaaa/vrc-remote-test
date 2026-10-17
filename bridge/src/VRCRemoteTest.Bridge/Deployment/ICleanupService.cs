namespace VRCRemoteTest.Bridge.Deployment;

public interface ICleanupService
{
    /// <summary>
    /// Deletes all but the newest <paramref name="retainBuilds"/> files matching the
    /// `vrc-remote-*.vrcw` pattern. Never touches any other file in the directory, and
    /// never deletes <paramref name="keepFileName"/> (the build just deployed, which a
    /// cold start also passed to VRChat's --url): it counts as one of the retained files
    /// even if its timestamp sorts it out of the newest ones.
    /// </summary>
    void Cleanup(string worldsDirectory, int retainBuilds, string keepFileName);

    /// <summary>
    /// Deletes all but the newest <paramref name="retainBuilds"/> `*.vrcw` files in the
    /// Bridge-private failed/ directory; quarantined manifests are small and kept.
    /// </summary>
    void CleanupFailedArtifacts(string failedDirectory, int retainBuilds);
}

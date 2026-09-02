using System.IO;

namespace VRCRemoteTest
{
    /// <summary>
    /// Resolved build artifact metadata. <see cref="FromSdkPath"/> replaces what
    /// was originally planned as a separate IBuildArtifactResolver interface
    /// plus a filesystem snapshot-diff fallback: the fallback was removed
    /// entirely (Codex plan review Round 2-3, confidence 0.91-0.92) because the
    /// VRChat SDK's Build() call already returns the artifact path directly, and
    /// the plan itself never confirmed a real case where the fallback would be
    /// needed. If such a case is observed in practice, add it back as a
    /// follow-up, not preemptively.
    /// </summary>
    public sealed class BuildArtifact
    {
        public string FullPath { get; }
        public string FileName { get; }
        public long Size { get; }
        public string Sha256 { get; set; }

        private BuildArtifact(string fullPath, string fileName, long size)
        {
            FullPath = fullPath;
            FileName = fileName;
            Size = size;
        }

        /// <summary>
        /// Validates the path VRChat SDK's Build() returned and extracts
        /// metadata. Throws RemoteBuildException(ARTIFACT_NOT_FOUND) if the
        /// path is missing, doesn't exist, or isn't a .vrcw file.
        /// </summary>
        public static BuildArtifact FromSdkPath(string sdkReturnedPath)
        {
            if (string.IsNullOrWhiteSpace(sdkReturnedPath))
            {
                throw new RemoteBuildException(
                    ErrorCode.ArtifactNotFound,
                    "VRChat SDK Build() did not return an artifact path.");
            }

            if (!File.Exists(sdkReturnedPath))
            {
                throw new RemoteBuildException(
                    ErrorCode.ArtifactNotFound,
                    $"Build artifact not found at: {sdkReturnedPath}");
            }

            if (Path.GetExtension(sdkReturnedPath) != ".vrcw")
            {
                throw new RemoteBuildException(
                    ErrorCode.ArtifactNotFound,
                    $"Build artifact is not a .vrcw file: {sdkReturnedPath}");
            }

            var fileName = Path.GetFileName(sdkReturnedPath);
            var size = new FileInfo(sdkReturnedPath).Length;

            return new BuildArtifact(sdkReturnedPath, fileName, size);
        }
    }
}

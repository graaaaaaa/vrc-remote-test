using System.IO;

namespace VRCRemoteTest
{
    /// <summary>
    /// Helpers for the `.part`-then-rename atomic write pattern used by the
    /// upload protocol (spec section 17). Both the temp and final path must be
    /// on the same volume for the rename to be truly atomic.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string finalPath, string content)
        {
            var partPath = finalPath + ".part";
            File.WriteAllText(partPath, content);
            ReplaceWithRename(partPath, finalPath);
        }

        public static void CopyAtomic(string sourcePath, string finalDestPath)
        {
            var partPath = finalDestPath + ".part";
            File.Copy(sourcePath, partPath, overwrite: true);
            ReplaceWithRename(partPath, finalDestPath);
        }

        private static void ReplaceWithRename(string partPath, string finalPath)
        {
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }

            File.Move(partPath, finalPath);
        }
    }
}

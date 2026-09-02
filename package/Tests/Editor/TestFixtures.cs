using System.IO;
using System.Runtime.CompilerServices;

namespace VRCRemoteTest.Tests
{
    /// <summary>
    /// Reads golden fixture files relative to this source file's own location
    /// via [CallerFilePath], rather than through Unity's AssetDatabase — this
    /// works regardless of whether the package is referenced by local path or
    /// installed as a VPM package with a different on-disk layout.
    /// </summary>
    internal static class TestFixtures
    {
        public static string Read(string fileName, [CallerFilePath] string sourceFilePath = "")
        {
            var testsEditorDir = Path.GetDirectoryName(sourceFilePath);
            // sourceFilePath points at Tests/Editor/TestFixtures.cs itself when
            // called with default caller info; callers in subfolders pass their
            // own path so we always resolve relative to Tests/Editor/fixtures.
            var fixturesDir = Path.Combine(FindTestsEditorRoot(testsEditorDir), "fixtures");
            return File.ReadAllText(Path.Combine(fixturesDir, fileName));
        }

        private static string FindTestsEditorRoot(string startDir)
        {
            var dir = startDir;
            while (dir != null && Path.GetFileName(dir) != "Editor")
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir ?? startDir;
        }
    }
}

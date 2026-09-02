using System.Text.RegularExpressions;

namespace VRCRemoteTest
{
    /// <summary>
    /// Filename validation mirroring bridge/src/VRCRemoteTest.Bridge/Deployment/PackageValidator.cs's
    /// allow-list regex, kept in sync so a manifest Unity writes will never be
    /// rejected by the Bridge for a reason Unity itself could have caught first.
    /// </summary>
    public static class PathUtility
    {
        private static readonly Regex SafeFileNamePattern =
            new Regex(@"^[A-Za-z0-9._-]+\.vrcw$", RegexOptions.Compiled);

        private static readonly string[] WindowsReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// Validates that a filename is a safe basename: no path separators, no
        /// traversal, no Windows reserved device names, ends with .vrcw.
        /// </summary>
        public static bool IsSafeBasename(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            if (fileName.Contains(".."))
            {
                return false;
            }

            if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':'))
            {
                return false;
            }

            if (!SafeFileNamePattern.IsMatch(fileName))
            {
                return false;
            }

            var stem = fileName.Substring(0, fileName.Length - ".vrcw".Length);
            foreach (var reserved in WindowsReservedNames)
            {
                if (string.Equals(stem, reserved, System.StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

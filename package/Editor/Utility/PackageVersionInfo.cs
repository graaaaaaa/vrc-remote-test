namespace VRCRemoteTest
{
    /// <summary>This package's version from its package.json, read once on the main thread.</summary>
    internal static class PackageVersionInfo
    {
        private static string _version;

        /// <summary>Empty when the package manager does not know this assembly (e.g. copied into Assets).</summary>
        internal static string Current
        {
            get
            {
                if (_version == null)
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(PackageVersionInfo).Assembly);
                    _version = info?.version ?? string.Empty;
                }

                return _version;
            }
        }
    }
}

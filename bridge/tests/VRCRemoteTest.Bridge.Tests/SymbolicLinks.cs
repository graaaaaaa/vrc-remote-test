namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Creating a symbolic link on Windows needs Developer Mode or the "Create symbolic links"
/// privilege, which a normal user running the tests usually lacks. The tests that check how
/// the Bridge treats links then have nothing to check and end early instead of failing
/// (xunit 2 has no dynamic skip). On macOS and Linux, where CI runs, they always run.
/// </summary>
internal static class SymbolicLinks
{
    public static bool TryCreateFile(string path, string target) =>
        TryCreate(() => File.CreateSymbolicLink(path, target));

    public static bool TryCreateDirectory(string path, string target) =>
        TryCreate(() => Directory.CreateSymbolicLink(path, target));

    private static bool TryCreate(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

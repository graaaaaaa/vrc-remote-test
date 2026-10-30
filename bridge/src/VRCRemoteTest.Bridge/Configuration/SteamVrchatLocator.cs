using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace VRCRemoteTest.Bridge.Configuration;

/// <summary>A VRChat.exe found in a Steam library, offered as a VrchatExecutable for a change through the LAN API.</summary>
/// <param name="Id">Stable for one listing: "steam-0", "steam-1"… in path order.</param>
public sealed record ExecutableCandidate(string Id, string Path);

/// <summary>
/// Finds VRChat.exe in the Steam libraries of the Windows user the Bridge runs as, so the
/// LAN API never takes a free-form executable path: a change can only pick one of these.
/// Steam's folder comes from HKCU\Software\Valve\Steam\SteamPath; the libraries from the
/// "path" entries of its steamapps\libraryfolders.vdf (plus Steam's own folder). The entries
/// are found by pattern, not by parsing VDF: a file that cannot be read gives only Steam's
/// own folder, and a malformed one whatever "path" entries it still has; never an error.
/// Every candidate must exist as a local drive path. Being listed does
/// not prove a file is VRChat: every candidate still passes BridgeOptionsValidator's
/// executable checks when it is chosen.
/// </summary>
public sealed partial class SteamVrchatLocator
{
    private const string RelativeExecutable = @"steamapps\common\VRChat\VRChat.exe";
    private const int MaxVdfBytes = 1024 * 1024;

    private readonly Func<string?> _steamPath;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _readText;

    public SteamVrchatLocator()
        : this(ReadSteamPath, File.Exists, ReadSmallText)
    {
    }

    internal SteamVrchatLocator(Func<string?> steamPath, Func<string, bool> fileExists, Func<string, string?> readText)
    {
        _steamPath = steamPath;
        _fileExists = fileExists;
        _readText = readText;
    }

    public IReadOnlyList<ExecutableCandidate> Find()
    {
        var steam = _steamPath();
        if (string.IsNullOrWhiteSpace(steam))
        {
            return [];
        }

        var libraries = new List<string> { Normalize(steam) };
        var vdf = _readText(Normalize(steam) + @"\steamapps\libraryfolders.vdf");
        if (vdf is not null)
        {
            libraries.AddRange(ParseLibraryPaths(vdf).Select(Normalize));
        }

        // Windows paths are built by hand so the same rules hold where the tests run.
        return libraries
            .Where(IsDriveAbsolute)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => p + @"\" + RelativeExecutable)
            .Where(_fileExists)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select((p, i) => new ExecutableCandidate($"steam-{i}", p))
            .ToList();
    }

    /// <summary>The "path" values of libraryfolders.vdf, with VDF's escaped backslashes undone.</summary>
    internal static IEnumerable<string> ParseLibraryPaths(string vdf) =>
        LibraryPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal));

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    /// <summary>"C:\…": a local drive path, never UNC or relative.</summary>
    private static bool IsDriveAbsolute(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static string? ReadSteamPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return ReadSteamPathFromRegistry();
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadSteamPathFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? ReadSmallText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxVdfBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

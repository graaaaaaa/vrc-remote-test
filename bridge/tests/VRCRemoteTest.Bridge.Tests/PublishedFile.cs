namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Reads a file the Bridge publishes on the share while a test polls it.
/// </summary>
internal static class PublishedFile
{
    /// <summary>
    /// Opened sharing read, write and delete access, as the Mac's SMB client does. A reader
    /// that does not share delete access makes PrivateSnapshotWriter skip the write on Windows
    /// (DestinationBusy) until the next poll, so a test polling with File.ReadAllText could
    /// itself keep a write that only a wake triggers from ever landing.
    /// </summary>
    public static string? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // not written yet, or being replaced
        }
    }
}

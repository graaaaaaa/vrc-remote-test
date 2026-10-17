using System.Text;
using Microsoft.Extensions.Logging;

namespace VRCRemoteTest.Bridge.VRChat;

internal enum SharedFileWriteOutcome
{
    /// <summary>The normal tmp-then-rename path: readers never see a partial file.</summary>
    Replaced,

    /// <summary>The destination was held open by a reader, so it was rewritten in place.</summary>
    OverwrittenInPlace,

    /// <summary>The destination was held open by a reader that does not share write access; nothing was written.</summary>
    DestinationBusy,
}

/// <summary>
/// Publishes a file that Unity polls over SMB from the Mac (logs/vrchat-latest.log,
/// status/vrchat-status.json), using the same tmp-then-rename pattern as ResultWriter
/// whenever it can.
///
/// That rename cannot replace a destination that is open: on Windows,
/// File.Move(overwrite: true) fails with UnauthorizedAccessException ("Access to the
/// path is denied.", no path) while any handle is open on the destination, even one
/// that shares Read|Write|Delete. The Mac's SMB client keeps exactly such a handle open
/// on the server for as long as Unity keeps polling the file (observed 2026-10-17: held
/// by PID 4 "System", i.e. the SMB server, sharing Read|Write|Delete; vrchat-latest.log
/// stopped updating for hours while an ERR with a stack trace was logged every 5s).
///
/// In that case the destination is rewritten in place instead. The write goes to the
/// same file the Mac's handle refers to, so reads through that handle see the new
/// content, and the conflicting write breaks the client's read-caching lease (a
/// POSIX-semantics rename would also succeed, but would leave that handle on the old,
/// unlinked file). A reader can see a
/// partially written file during that one write; both Unity readers already treat that
/// as a transient miss (IOException/JsonException -> null, retried on the next poll).
///
/// Outcome changes are logged once, not on every poll. Not thread-safe: each instance
/// belongs to a single polling loop.
/// </summary>
internal sealed class SharedFileWriter
{
    private const int ErrorSharingViolationHResult = unchecked((int)0x80070020);
    private const int ErrorLockViolationHResult = unchecked((int)0x80070021);

    /// <summary>Same encoding File.WriteAllText used before this class existed.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger;
    private SharedFileWriteOutcome _lastOutcome = SharedFileWriteOutcome.Replaced;

    public SharedFileWriter(ILogger logger)
    {
        _logger = logger;
    }

    public SharedFileWriteOutcome Write(string finalPath, string content)
    {
        var outcome = WriteCore(finalPath, Utf8NoBom.GetBytes(content));
        if (outcome != _lastOutcome)
        {
            LogOutcomeChange(finalPath, outcome);
            _lastOutcome = outcome;
        }

        return outcome;
    }

    private static SharedFileWriteOutcome WriteCore(string finalPath, byte[] bytes)
    {
        var tempPath = finalPath + ".tmp";

        File.WriteAllBytes(tempPath, bytes);
        try
        {
            File.Move(tempPath, finalPath, overwrite: true);
            return SharedFileWriteOutcome.Replaced;
        }
        catch (UnauthorizedAccessException) when (File.Exists(finalPath))
        {
            // Most likely held open (see the class comment). A genuine ACL or
            // read-only denial also fails the in-place open below and propagates.
        }

        File.Delete(tempPath);

        try
        {
            using var stream = new FileStream(
                finalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            // Write before truncating so a concurrent reader never sees an empty file.
            stream.Write(bytes);
            stream.SetLength(bytes.Length);
        }
        catch (IOException ex) when (ex.HResult is ErrorSharingViolationHResult or ErrorLockViolationHResult)
        {
            return SharedFileWriteOutcome.DestinationBusy;
        }

        return SharedFileWriteOutcome.OverwrittenInPlace;
    }

    private void LogOutcomeChange(string finalPath, SharedFileWriteOutcome outcome)
    {
        switch (outcome)
        {
            case SharedFileWriteOutcome.OverwrittenInPlace:
                _logger.LogInformation(
                    "{Path} is held open by a reader (typically the Mac's SMB client while Unity polls it); " +
                    "overwriting it in place until it is released.",
                    finalPath);
                break;
            case SharedFileWriteOutcome.DestinationBusy:
                _logger.LogWarning(
                    "{Path} is held open by a reader that does not allow writing; " +
                    "it will not be updated until that reader releases it.",
                    finalPath);
                break;
            default:
                _logger.LogInformation("{Path} is no longer held open; replacing it atomically again.", finalPath);
                break;
        }
    }
}

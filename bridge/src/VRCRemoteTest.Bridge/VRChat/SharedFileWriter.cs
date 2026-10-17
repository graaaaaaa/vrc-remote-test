using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace VRCRemoteTest.Bridge.VRChat;

internal enum SharedFileWriteOutcome
{
    /// <summary>The normal tmp-then-rename path.</summary>
    Replaced,

    /// <summary>The destination was held open by a reader, so the tmp file was renamed over it with POSIX semantics.</summary>
    ReplacedWhileHeldOpen,

    /// <summary>The destination was held open by a reader that does not share delete access; nothing was written.</summary>
    DestinationBusy,
}

/// <summary>
/// Publishes a file that Unity polls over SMB from the Mac (logs/vrchat-latest.log,
/// status/vrchat-status.json) with the same tmp-then-rename pattern as ResultWriter.
///
/// That rename cannot replace a destination that is open: on Windows,
/// File.Move(overwrite: true) fails with UnauthorizedAccessException ("Access to the
/// path is denied.", no path) while any handle is open on the destination, even one
/// that shares Read|Write|Delete. The Mac's SMB client can get into a state where it
/// keeps exactly such a handle open on the server for hours (observed 2026-10-17: held
/// by PID 4 "System", i.e. the SMB server, sharing Read|Write|Delete, until the share
/// was remounted on the Mac; vrchat-latest.log stopped updating while an ERR with a
/// stack trace was logged every 5s).
///
/// In that case the tmp file is renamed over the destination with POSIX semantics
/// (FileRenameInfoEx, REPLACE_IF_EXISTS | POSIX_SEMANTICS), which Windows allows when
/// every open handle shares delete access. The name then refers to a new file with a
/// new file ID, so a new open sees the new content, while the reader's existing handle
/// keeps the old, unlinked file. The replacement stays atomic.
///
/// Rewriting the destination in place was tried first and did not work: while the
/// Mac held the handle, its SMB client kept serving the old cached content, cut to the
/// new size and padded with NULs, so Unity could not parse vrchat-status.json. With a
/// new file, a client that keeps using its old handle sees the old content intact.
///
/// Outcome changes are logged once, not on every poll. Not thread-safe: each instance
/// belongs to a single polling loop.
/// </summary>
internal sealed class SharedFileWriter
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>File.WriteAllText's default encoding, as the writers used before.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger _logger;
    private SharedFileWriteOutcome _lastOutcome = SharedFileWriteOutcome.Replaced;

    public SharedFileWriter(ILogger logger)
    {
        _logger = logger;
    }

    public SharedFileWriteOutcome Write(string finalPath, string content)
    {
        var outcome = WriteCore(Path.GetFullPath(finalPath), content);
        if (outcome != _lastOutcome)
        {
            LogOutcomeChange(finalPath, outcome);
            _lastOutcome = outcome;
        }

        return outcome;
    }

    private static SharedFileWriteOutcome WriteCore(string finalPath, string content)
    {
        var tempPath = finalPath + ".tmp";

        File.WriteAllText(tempPath, content, Utf8NoBom);
        try
        {
            File.Move(tempPath, finalPath, overwrite: true);
            return SharedFileWriteOutcome.Replaced;
        }
        catch (UnauthorizedAccessException) when (File.Exists(finalPath))
        {
            // On Unix, rename(2) replaces an open destination, so this is a real denial there.
            if (!OperatingSystem.IsWindows())
            {
                throw;
            }

            return ReplaceWhileHeldOpen(tempPath, finalPath);
        }
    }

    /// <summary>
    /// Most likely held open (see the class comment). A read-only or ACL denial fails
    /// the POSIX rename too and is thrown from here.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SharedFileWriteOutcome ReplaceWhileHeldOpen(string tempPath, string finalPath)
    {
        var error = PosixRename.TryReplace(tempPath, finalPath);
        switch (error)
        {
            case 0:
                return SharedFileWriteOutcome.ReplacedWhileHeldOpen;
            case ErrorSharingViolation or ErrorLockViolation:
                File.Delete(tempPath);
                return SharedFileWriteOutcome.DestinationBusy;
            case ErrorAccessDenied:
                throw new UnauthorizedAccessException($"Access to the path '{finalPath}' is denied.");
            default:
                throw new IOException(
                    $"Could not replace '{finalPath}': {new Win32Exception(error).Message}",
                    unchecked((int)0x80070000) | error);
        }
    }

    private void LogOutcomeChange(string finalPath, SharedFileWriteOutcome outcome)
    {
        switch (outcome)
        {
            case SharedFileWriteOutcome.ReplacedWhileHeldOpen:
                _logger.LogInformation(
                    "{Path} is held open by a reader (typically the Mac's SMB client); " +
                    "replacing it with POSIX semantics so that new opens see the new file.",
                    finalPath);
                break;
            case SharedFileWriteOutcome.DestinationBusy:
                _logger.LogWarning(
                    "{Path} is held open by a reader that does not allow it to be replaced; " +
                    "it will not be updated until that reader releases it.",
                    finalPath);
                break;
            default:
                _logger.LogInformation("{Path} is no longer held open; replacing it normally again.", finalPath);
                break;
        }
    }

    [SupportedOSPlatform("windows")]
    private static class PosixRename
    {
        private const uint Delete = 0x00010000;
        private const uint Synchronize = 0x00100000;
        private const uint OpenExisting = 3;
        private const int FileRenameInfoEx = 22;
        private const uint FileRenameFlagReplaceIfExists = 0x1;
        private const uint FileRenameFlagPosixSemantics = 0x2;

        /// <summary>FILE_RENAME_INFO: Flags (DWORD union), RootDirectory (HANDLE), FileNameLength (DWORD), FileName (WCHAR[]).</summary>
        private static readonly int FileNameLengthOffset = IntPtr.Size == 8 ? 16 : 8;

        /// <summary>Returns 0 on success, otherwise the Win32 error code.</summary>
        public static int TryReplace(string sourcePath, string destinationPath)
        {
            using var source = CreateFileW(
                sourcePath,
                Delete | Synchronize,
                (uint)(FileShare.ReadWrite | FileShare.Delete),
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (source.IsInvalid)
            {
                return Marshal.GetLastPInvokeError();
            }

            var name = Encoding.Unicode.GetBytes(destinationPath);
            var size = FileNameLengthOffset + sizeof(int) + name.Length + sizeof(char);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, buffer, size);
                Marshal.WriteInt32(buffer, 0, (int)(FileRenameFlagReplaceIfExists | FileRenameFlagPosixSemantics));
                Marshal.WriteInt32(buffer, FileNameLengthOffset, name.Length);
                Marshal.Copy(name, 0, buffer + FileNameLengthOffset + sizeof(int), name.Length);

                return SetFileInformationByHandle(source, FileRenameInfoEx, buffer, (uint)size)
                    ? 0
                    : Marshal.GetLastPInvokeError();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle file, int fileInformationClass, IntPtr fileInformation, uint bufferSize);
    }
}

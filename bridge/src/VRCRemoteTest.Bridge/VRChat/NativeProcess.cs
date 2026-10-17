using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// The kernel32 calls Relaunch mode makes on a process handle the Bridge opens and holds
/// itself (multi-client plan section 13.3). While any handle to a process is open,
/// Windows does not reuse its PID, so everything done through the held handle -- reading
/// the creation time, checking for exit, terminating -- reaches the process that was
/// matched against the record, never one that took over the PID.
///
/// The handle asks only for what those calls need. Process.SafeHandle would open the
/// process with PROCESS_ALL_ACCESS, which includes memory read/write rights that a
/// game's anti-cheat may object to and that the Bridge never uses. Windows only; callers
/// check OperatingSystem.IsWindows() first.
/// </summary>
internal static class NativeProcess
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x0010_0000;

    private const uint ClientAccess = ProcessTerminate | ProcessQueryLimitedInformation | Synchronize;

    /// <summary>OpenProcess's error for a PID that names no process any more.</summary>
    private const int ErrorInvalidParameter = 87;

    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x0000_0102;

    private const uint TerminatedExitCode = 1;

    internal enum OpenOutcome
    {
        Opened,

        /// <summary>No process has this PID any more: it exited after enumeration.</summary>
        Gone,

        /// <summary>The process exists but cannot be opened (e.g. it runs elevated).</summary>
        Failed,
    }

    internal static OpenOutcome TryOpen(int processId, out SafeProcessHandle? handle, out int error)
    {
        var opened = OpenProcess(ClientAccess, inheritHandle: false, processId);
        if (!opened.IsInvalid)
        {
            handle = opened;
            error = 0;
            return OpenOutcome.Opened;
        }

        error = Marshal.GetLastPInvokeError();
        opened.Dispose();
        handle = null;
        return error == ErrorInvalidParameter ? OpenOutcome.Gone : OpenOutcome.Failed;
    }

    /// <summary>
    /// The FILETIME as UTC, at its full 100 ns precision. Process.StartTime reads the same
    /// value and converts it to local time, which ToUniversalTime converts back exactly.
    /// </summary>
    internal static DateTime? TryGetCreationTimeUtc(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out var creation, out _, out _, out _) ? DateTime.FromFileTimeUtc(creation) : null;

    /// <summary>Null when the wait itself failed, i.e. the handle cannot tell.</summary>
    internal static bool? HasExited(SafeProcessHandle handle) =>
        WaitForSingleObject(handle, 0) switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            _ => null,
        };

    internal static bool TryTerminate(SafeProcessHandle handle, out int error)
    {
        if (TerminateProcess(handle, TerminatedExitCode))
        {
            error = 0;
            return true;
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}

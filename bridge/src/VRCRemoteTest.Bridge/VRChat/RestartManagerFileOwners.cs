using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Asks the Windows Restart Manager (rstrtmgr.dll) which processes have one file open,
/// for <see cref="VrchatLogOwners"/>. Each file gets its own session, since RmGetList
/// reports the processes using any of a session's files without saying which file each
/// one uses, and every session is ended before returning: only 64 can be open on the
/// machine at once. Restart Manager works without elevation and lists the processes
/// that have a handle to the file whatever they let the caller do: a non-elevated
/// PowerShell got each running VRChat's own output_log back on real hardware
/// (docs/validation/multi-client-spike.md, PR #3 verification, G).
///
/// Restart Manager gives each process's PID and creation time. The image name is looked
/// up by PID afterwards (Process.ProcessName reads the system's process list and needs no
/// handle, so it also works for protected processes such as antivirus software). When the
/// process can also be opened, its start time must match the one Restart Manager gave,
/// or the PID was reused in between and the name is not reported.
///
/// Besides <see cref="VrchatProcessMonitor"/>, the only class looking up processes; it
/// never enumerates them. Exercised only on Windows: the real-hardware steps are in the
/// pull request that added it and in docs/setup-windows.md section 11.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RestartManagerFileOwners
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;

    /// <summary>CCH_RM_SESSION_KEY: a GUID as 32 hexadecimal digits.</summary>
    private const int SessionKeyLength = 32;

    private const int MaxAppNameLength = 255;
    private const int MaxServiceNameLength = 63;

    /// <summary>RmGetList asks for a bigger buffer when processes open the file between two calls.</summary>
    private const int MaxListAttempts = 3;

    /// <summary>The processes that have <paramref name="path"/> open; throws when Restart Manager fails.</summary>
    /// <exception cref="Win32Exception">A Restart Manager function returned an error.</exception>
    public static IReadOnlyList<VrchatLogOwners.Owner> Query(string path)
    {
        var sessionKey = new char[SessionKeyLength + 1];
        Check(RmStartSession(out var session, 0, sessionKey), nameof(RmStartSession));
        RmProcessInfo[] processes;
        try
        {
            Check(RmRegisterResources(session, 1, [path], 0, null, 0, null), nameof(RmRegisterResources));
            processes = GetList(session);
        }
        finally
        {
            RmEndSession(session);
        }

        return processes
            .Select(p =>
            {
                var startUtc = ToUtc(p.Process.ProcessStartTime);
                return new VrchatLogOwners.Owner(p.Process.ProcessId, startUtc, NameOf(p.Process.ProcessId, startUtc));
            })
            .ToList();
    }

    private static RmProcessInfo[] GetList(uint session)
    {
        RmProcessInfo[]? buffer = null;
        uint count = 0;
        for (var attempt = 0; attempt < MaxListAttempts; attempt++)
        {
            var result = RmGetList(session, out var needed, ref count, buffer, out _);
            if (result == ErrorSuccess)
            {
                return buffer is null ? [] : buffer[..(int)count];
            }

            if (result != ErrorMoreData)
            {
                Check(result, nameof(RmGetList));
            }

            buffer = new RmProcessInfo[needed];
            count = needed;
        }

        throw new Win32Exception(ErrorMoreData, "RmGetList kept reporting more processes than it had returned.");
    }

    /// <summary>Null when the process exited, its PID now names another process, or it cannot be looked up.</summary>
    private static string? NameOf(int processId, DateTime? startUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName;
            if (startUtc is { } expected)
            {
                try
                {
                    if (!VrchatProcessMonitor.IsSameCreationTime(process.StartTime.ToUniversalTime(), expected))
                    {
                        return null;
                    }
                }
                catch (Win32Exception)
                {
                    // Not allowed to open it (another user's or a protected process): the name stands.
                }
            }

            return name;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // ArgumentException: no process with this PID any more; InvalidOperationException: it exited meanwhile.
            return null;
        }
    }

    /// <summary>Null for a zero or out-of-range FILETIME, which Restart Manager leaves when it cannot read the time.</summary>
    private static DateTime? ToUtc(FileTime time)
    {
        var ticks = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
        if (ticks <= 0)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void Check(int result, string function)
    {
        if (result != ErrorSuccess)
        {
            throw new Win32Exception(result, $"{function} returned {result}: {new Win32Exception(result).Message}");
        }
    }

    /// <summary>RM_UNIQUE_PROCESS.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public FileTime ProcessStartTime;
    }

    /// <summary>RM_PROCESS_INFO.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxAppNameLength + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxServiceNameLength + 1)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TerminalServicesSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, [Out] char[] sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] fileNames,
        uint applicationCount,
        RmUniqueProcess[]? applications,
        uint serviceCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? serviceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfoCount,
        [In, Out] RmProcessInfo[]? affectedApps,
        out uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}

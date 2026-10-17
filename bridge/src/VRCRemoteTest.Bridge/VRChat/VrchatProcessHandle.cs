namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// A same-session VRChat process the Bridge holds an open handle to (Relaunch mode,
/// multi-client plan section 13.3). Matching against the record, closing, killing and
/// confirming the exit all go through this one object, which is never reopened from
/// its PID, so a PID that another process takes over can never be closed by mistake.
/// </summary>
public interface IVrchatProcessHandle : IDisposable
{
    int ProcessId { get; }

    /// <summary>The Windows session it runs in; only the Bridge's own session is ever opened.</summary>
    int SessionId { get; }

    string ProcessName { get; }

    /// <summary>Read through the held handle, UTC at full precision.</summary>
    DateTime CreationTimeUtc { get; }

    /// <summary>Through the held handle. Throws when the handle cannot tell.</summary>
    bool HasExited { get; }

    /// <summary>
    /// Asks the main window to close. False when the process has no usable main window
    /// (yet): VRChat creates it about a second after starting (spike gate 3).
    /// </summary>
    bool CloseMainWindow();

    /// <summary>Forgets the cached main window, so the next CloseMainWindow looks for it again.</summary>
    void Refresh();

    /// <summary>Terminates the process through the held handle. Throws when that fails.</summary>
    void Kill();
}

/// <summary>
/// One enumeration that opened a handle to every same-session VRChat it could confirm.
/// Like <see cref="VrchatClientSnapshot"/>, anything it could not confirm is counted,
/// never treated as "not running". Disposing closes every handle.
/// </summary>
public sealed class VrchatProcessHandleSnapshot : IDisposable
{
    public VrchatProcessHandleSnapshot(
        IReadOnlyList<IVrchatProcessHandle> handles, int unverifiedCount = 0, string? enumerationError = null)
    {
        Handles = handles;
        UnverifiedCount = unverifiedCount;
        EnumerationError = enumerationError;
    }

    public IReadOnlyList<IVrchatProcessHandle> Handles { get; }

    /// <summary>
    /// VRChat processes whose session could not be read, that could not be opened (e.g.
    /// running elevated), whose creation time could not be read through the handle, or
    /// that kept vanishing mid-read.
    /// </summary>
    public int UnverifiedCount { get; }

    /// <summary>Non-null when the process list itself could not be read; nothing else is known then.</summary>
    public string? EnumerationError { get; }

    public static VrchatProcessHandleSnapshot Failed(string error) => new([], 0, error);

    public void Dispose()
    {
        foreach (var handle in Handles)
        {
            handle.Dispose();
        }
    }
}

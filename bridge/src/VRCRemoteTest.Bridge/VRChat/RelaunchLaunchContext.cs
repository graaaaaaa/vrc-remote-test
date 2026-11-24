namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>How one slot of the current Relaunch batch was launched: the arguments a single relaunch repeats.</summary>
public sealed record SlotLaunchPlan(int Slot, string WorldPath, string RoomId, int Profile, bool NoVr);

/// <summary>The Bridge's most recent VRChat launch, for spacing the next one (<see cref="VrchatLaunchPacer"/>).</summary>
/// <param name="FilesBefore">The output_log files that existed just before it; null when the directory could not be listed.</param>
public sealed record LastLaunch(DateTime Utc, IReadOnlyList<string>? FilesBefore);

/// <summary>
/// What the Bridge remembers, in memory only, about the Relaunch batch it launched last, so
/// that the LAN API can close one client and start it again with the same arguments
/// (<see cref="ClientSlotControl"/>): the world, the room id, and each slot's profile and
/// VR flag. Only slots that were launched and recorded are listed, so a slot a batch never
/// started cannot be started on its own.
///
/// It is a singleton of the Bridge host, never saved: a configuration change or a restart
/// builds a new host, which starts with nothing, so no stale world or profile can ever be
/// launched; the next deploy fills it again. Every deploy's PrepareAsync clears it first.
///
/// It also carries two things the observation needs (client-observation plan C2):
/// <see cref="IsLaunching"/>, set from just before Process.Start until the client is
/// recorded, while which the roster leaves unrecorded newcomers unplaced; and the clients
/// started on their own (<see cref="IsSingleLaunch"/>), whose log is matched only by the file
/// Restart Manager shows they have open, never by the times in the names. Neither is ever a
/// reason to close or launch a process. Thread-safe: the deploy flow and the LAN API write
/// it inside the operation gate, and the observation loop reads it.
/// </summary>
public sealed class RelaunchLaunchContext
{
    private readonly object _gate = new();
    private readonly Dictionary<int, SlotLaunchPlan> _slots = [];
    private readonly HashSet<(int ProcessId, long CreationTicks)> _singleLaunches = [];
    private string? _worldPath;
    private string? _roomId;
    private LastLaunch? _lastLaunch;
    private int _launching;

    /// <summary>True from just before a client is started until it is recorded (or could not be).</summary>
    public bool IsLaunching
    {
        get { lock (_gate) return _launching > 0; }
    }

    public LastLaunch? LastLaunch
    {
        get { lock (_gate) return _lastLaunch; }
    }

    /// <summary>A new deploy: nothing of the previous batch may be launched again.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _slots.Clear();
            _singleLaunches.Clear();
            _worldPath = null;
            _roomId = null;
        }
    }

    /// <summary>A batch starts: its slots are added by <see cref="AddSlot"/> as each is recorded.</summary>
    public void BeginBatch(string worldPath, string roomId)
    {
        lock (_gate)
        {
            _slots.Clear();
            _singleLaunches.Clear();
            _worldPath = worldPath;
            _roomId = roomId;
        }
    }

    public void AddSlot(int slot, int profile, bool noVr)
    {
        lock (_gate)
        {
            if (_worldPath is null || _roomId is null)
            {
                throw new InvalidOperationException("No batch has begun.");
            }

            _slots[slot] = new SlotLaunchPlan(slot, _worldPath, _roomId, profile, noVr);
        }
    }

    public SlotLaunchPlan? PlanFor(int slot)
    {
        lock (_gate)
        {
            return _slots.GetValueOrDefault(slot);
        }
    }

    /// <summary>Marks a launch in progress until the returned object is disposed.</summary>
    public IDisposable BeginLaunch()
    {
        lock (_gate)
        {
            _launching++;
        }

        return new LaunchScope(this);
    }

    /// <summary>A client was started (recorded or not): the next launch is spaced from it.</summary>
    public void RecordLaunch(DateTime utc, IReadOnlyList<string>? filesBefore)
    {
        lock (_gate)
        {
            _lastLaunch = new LastLaunch(utc, filesBefore);
        }
    }

    public void MarkSingleLaunch(int processId, DateTime creationTimeUtc)
    {
        lock (_gate)
        {
            _singleLaunches.Add((processId, creationTimeUtc.Ticks));
        }
    }

    public bool IsSingleLaunch(int processId, DateTime? creationTimeUtc)
    {
        if (creationTimeUtc is not { } created)
        {
            return false;
        }

        lock (_gate)
        {
            return _singleLaunches.Contains((processId, created.Ticks));
        }
    }

    private void EndLaunch()
    {
        lock (_gate)
        {
            _launching--;
        }
    }

    private sealed class LaunchScope(RelaunchLaunchContext context) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                context.EndLaunch();
            }
        }
    }
}

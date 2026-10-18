using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>A client the roster gave a slot: its PID and full-precision creation time.</summary>
public sealed record RosterEntry(int Slot, int ProcessId, DateTime CreationTimeUtc)
{
    /// <summary>
    /// Until the Bridge keeps a launch counter (client-observation plan C1, P3), the
    /// generation of a slot's client is derived from its PID and creation time: the same
    /// process always has the same value, and another process in the slot another one.
    /// A positive 53-bit number, so the Mac's JSON readers (jq reads doubles) keep it exact.
    /// </summary>
    public long ClientGeneration => VrchatClientRoster.GenerationOf(ProcessId, CreationTimeUtc);
}

public enum RosterLiveness
{
    /// <summary>No client was ever given this slot.</summary>
    Empty,

    /// <summary>The slot's client is in the snapshot, with the same creation time.</summary>
    Running,

    /// <summary>The slot's client is confirmed gone: not listed while every VRChat was checked, or its PID now names another process.</summary>
    Exited,

    /// <summary>Whether the slot's client still runs cannot be told from this snapshot.</summary>
    Unknown,
}

/// <param name="Client">The snapshot's entry for the slot's client while it runs.</param>
public sealed record RosterSlot(int Slot, RosterEntry? Entry, RosterLiveness Liveness, VrchatClientInfo? Client);

/// <param name="NewGenerations">Slots that were given to a new client by this update.</param>
/// <param name="Unassigned">Running clients without a slot: unknown creation time, or more than four.</param>
public sealed record RosterView(
    IReadOnlyList<RosterSlot> Slots, IReadOnlySet<int> NewGenerations, int Unassigned)
{
    public RosterSlot? SlotOf(VrchatClientInfo client) =>
        Slots.FirstOrDefault(s => s.Liveness == RosterLiveness.Running && s.Client == client);
}

/// <summary>
/// Which slot (0-3) each VRChat client is observed in (client-observation plan C2):
/// StateDirectory\observation-roster.json, outside the share like the rest of the Bridge's
/// state. Observation only: it is never a reason to close, launch or control a process.
///
/// A client keeps its slot for as long as it runs, across Bridge restarts, and the entry of
/// a client that exited stays until a new client takes the slot. So after a Reload session's
/// slot 0 client exits and the Bridge restarts, the client still running stays in slot 1
/// instead of being renumbered. Only newly seen clients are placed, by creation time and
/// then PID: in Relaunch mode in the slot of their launch order in the Bridge's record
/// (managed-clients.json) when it is free, otherwise in the lowest free slot. A slot is
/// free when it is empty or its client is confirmed gone. A client whose creation time is
/// unknown gets no slot.
///
/// Observation epochs are never handed out twice, across restarts too: each is the larger of
/// the last one plus one and the current Unix time in milliseconds, so one that could not be
/// saved before a restart is still below every epoch after it (epochs are rare, far fewer
/// than one per millisecond). The file keeps the last one as well, against the clock going
/// back. A file that is missing or not one this Bridge wrote starts an empty roster. Failing
/// to save is logged and the roster carries on in memory.
///
/// Thread-safe, though only VrchatLogService's loop uses it.
/// </summary>
public sealed class VrchatClientRoster
{
    public const string FileName = "observation-roster.json";

    internal const int SchemaVersion = 1;
    internal const int SlotCount = BridgeOptions.MaxVrchatClientCount;
    internal const int MaxFileBytes = 64 * 1024;

    private const string CreationTimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";
    private const long GenerationMask = (1L << 53) - 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string? _filePath;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly RosterEntry?[] _entries = new RosterEntry?[SlotCount];
    private long _lastEpoch;
    private bool _loaded;
    private string? _lastSaveProblem;

    public VrchatClientRoster(IOptions<BridgeOptions> options, ILogger<VrchatClientRoster> logger)
        : this(Path.Combine(options.Value.StateDirectory, FileName), logger)
    {
    }

    /// <summary>Test seam; a null <paramref name="filePath"/> keeps the roster in memory only.</summary>
    internal VrchatClientRoster(string? filePath, ILogger? logger = null, Func<DateTimeOffset>? utcNow = null)
    {
        _filePath = filePath;
        _logger = logger ?? NullLogger.Instance;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal static long GenerationOf(int processId, DateTime creationTimeUtc)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{processId}:{creationTimeUtc.Ticks}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var value = BitConverter.ToInt64(hash, 0) & GenerationMask;
        return value == 0 ? 1 : value;
    }

    /// <param name="records">The Bridge's Relaunch record; empty in Reload mode.</param>
    public RosterView Update(VrchatClientSnapshot snapshot, IReadOnlyList<ManagedClient> records)
    {
        lock (_gate)
        {
            EnsureLoadedLocked();

            if (snapshot.EnumerationError is not null)
            {
                return ViewLocked(_ => RosterLiveness.Unknown, [], new HashSet<int>(), unassigned: 0);
            }

            var liveness = new RosterLiveness[SlotCount];
            var clients = new VrchatClientInfo?[SlotCount];
            for (var slot = 0; slot < SlotCount; slot++)
            {
                (liveness[slot], clients[slot]) = Classify(_entries[slot], snapshot);
            }

            var changed = false;
            var newGenerations = new HashSet<int>();

            // A recorded client that the roster placed elsewhere (seen between its launch and
            // the record's save) moves to its launch order when that slot is free.
            for (var slot = 0; slot < SlotCount; slot++)
            {
                if (liveness[slot] != RosterLiveness.Running
                    || RecordedSlot(clients[slot]!, records) is not { } target
                    || target == slot
                    || liveness[target] is RosterLiveness.Running or RosterLiveness.Unknown)
                {
                    continue;
                }

                _entries[target] = _entries[slot]! with { Slot = target };
                (liveness[target], clients[target]) = (RosterLiveness.Running, clients[slot]);
                _entries[slot] = null;
                (liveness[slot], clients[slot]) = (RosterLiveness.Empty, null);
                newGenerations.Add(target);
                changed = true;
            }

            var placed = new HashSet<VrchatClientInfo>(clients.Where(c => c is not null)!);
            var unassigned = 0;
            var newcomers = snapshot.Clients
                .Where(c => !placed.Contains(c))
                .OrderBy(c => c.CreationTimeUtc is null)
                .ThenBy(c => c.CreationTimeUtc)
                .ThenBy(c => c.ProcessId);
            foreach (var client in newcomers)
            {
                if (client.CreationTimeUtc is not { } created)
                {
                    unassigned++;
                    continue;
                }

                if (FreeSlot(liveness, RecordedSlot(client, records)) is not { } free)
                {
                    unassigned++;
                    continue;
                }

                _entries[free] = new RosterEntry(free, client.ProcessId, created);
                (liveness[free], clients[free]) = (RosterLiveness.Running, client);
                newGenerations.Add(free);
                changed = true;
            }

            if (changed)
            {
                SaveLocked();
            }

            return ViewLocked(s => liveness[s], clients, newGenerations, unassigned);
        }
    }

    /// <summary>
    /// A new epoch, higher than every one handed out before (also before a restart, saved or
    /// not), saved with the roster.
    /// </summary>
    public long AllocateEpoch()
    {
        lock (_gate)
        {
            EnsureLoadedLocked();
            _lastEpoch = Math.Max(_lastEpoch + 1, _utcNow().ToUnixTimeMilliseconds());
            SaveLocked();
            return _lastEpoch;
        }
    }

    /// <summary>The preferred slot when it is free, otherwise the lowest free one; a slot is free when empty or its client is gone.</summary>
    private static int? FreeSlot(RosterLiveness[] liveness, int? preferred)
    {
        static bool Free(RosterLiveness state) => state is RosterLiveness.Empty or RosterLiveness.Exited;

        if (preferred is { } slot && Free(liveness[slot]))
        {
            return slot;
        }

        for (var candidate = 0; candidate < liveness.Length; candidate++)
        {
            if (Free(liveness[candidate]))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (RosterLiveness, VrchatClientInfo?) Classify(RosterEntry? entry, VrchatClientSnapshot snapshot)
    {
        if (entry is null)
        {
            return (RosterLiveness.Empty, null);
        }

        var samePid = snapshot.Clients.Where(c => c.ProcessId == entry.ProcessId).ToList();
        var same = samePid.FirstOrDefault(c => c.CreationTimeUtc?.Ticks == entry.CreationTimeUtc.Ticks);
        if (same is not null)
        {
            return (RosterLiveness.Running, same);
        }

        if (samePid.Any(c => c.CreationTimeUtc is null) || (samePid.Count == 0 && snapshot.UnverifiedCount > 0))
        {
            return (RosterLiveness.Unknown, null);
        }

        // Not listed while every VRChat was checked, or the PID now names a process started at another time.
        return (RosterLiveness.Exited, null);
    }

    private static int? RecordedSlot(VrchatClientInfo client, IReadOnlyList<ManagedClient> records)
    {
        var record = records.FirstOrDefault(r =>
            r.ProcessId == client.ProcessId && client.CreationTimeUtc?.Ticks == r.CreationTimeUtc.Ticks);
        return record is { LaunchOrder: >= 0 and < SlotCount } ? record.LaunchOrder : null;
    }

    private RosterView ViewLocked(
        Func<int, RosterLiveness> liveness,
        IReadOnlyList<VrchatClientInfo?> clients,
        IReadOnlySet<int> newGenerations,
        int unassigned)
    {
        var slots = new RosterSlot[SlotCount];
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var entry = _entries[slot];
            var state = entry is null ? RosterLiveness.Empty : liveness(slot);
            slots[slot] = new RosterSlot(
                slot, entry, state, state == RosterLiveness.Running && clients.Count > slot ? clients[slot] : null);
        }

        return new RosterView(slots, newGenerations, unassigned);
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (_filePath is null)
        {
            return;
        }

        try
        {
            var file = Read(_filePath);
            if (file is null)
            {
                return;
            }

            _lastEpoch = file.LastObservationEpoch;
            foreach (var entry in file.Entries)
            {
                _entries[entry.Slot] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _logger.LogWarning(
                "{Path} is not an observation roster this Bridge wrote ({Problem}); starting with empty slots.",
                _filePath, ex.Message);
            Array.Clear(_entries);
            _lastEpoch = 0;
        }
    }

    private static RosterFileContent? Read(string path)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxFileBytes)
            {
                throw new InvalidDataException($"it is larger than {MaxFileBytes} bytes");
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var file = JsonSerializer.Deserialize<RosterFile>(bytes, SerializerOptions)
            ?? throw new InvalidDataException("it is empty");
        if (file.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException($"schemaVersion {file.SchemaVersion} is unknown");
        }

        if (file.LastObservationEpoch < 0 || file.Entries is not { Count: <= SlotCount } entries)
        {
            throw new InvalidDataException("it has a value out of range");
        }

        var parsed = new List<RosterEntry>();
        foreach (var entry in entries)
        {
            if (entry is null || entry.Slot is < 0 or >= SlotCount || entry.ProcessId <= 0
                || !DateTime.TryParseExact(
                    entry.CreationTimeUtc,
                    CreationTimeFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var created))
            {
                throw new InvalidDataException("an entry has a value out of range");
            }

            parsed.Add(new RosterEntry(entry.Slot, entry.ProcessId, created));
        }

        if (parsed.Select(e => e.Slot).Distinct().Count() != parsed.Count)
        {
            throw new InvalidDataException("a slot is listed twice");
        }

        return new RosterFileContent(file.LastObservationEpoch, parsed);
    }

    private void SaveLocked()
    {
        if (_filePath is null)
        {
            return;
        }

        var file = new RosterFile
        {
            SchemaVersion = SchemaVersion,
            LastObservationEpoch = _lastEpoch,
            Entries = _entries.Where(e => e is not null).Select(e => (RosterFileEntry?)new RosterFileEntry
            {
                Slot = e!.Slot,
                ProcessId = e.ProcessId,
                CreationTimeUtc = e.CreationTimeUtc.ToUniversalTime().ToString(CreationTimeFormat, CultureInfo.InvariantCulture),
            }).ToList(),
        };

        var temporaryPath = _filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(file, SerializerOptions));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            if (_lastSaveProblem is not null)
            {
                _logger.LogInformation("{Path} can be saved again.", _filePath);
                _lastSaveProblem = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex.Message != _lastSaveProblem)
            {
                _logger.LogWarning(
                    ex, "Could not save {Path}; slots are kept in memory until it can be saved.", _filePath);
                _lastSaveProblem = ex.Message;
            }
        }
    }

    private sealed record RosterFileContent(long LastObservationEpoch, IReadOnlyList<RosterEntry> Entries);

    internal sealed class RosterFile
    {
        [JsonPropertyName("schemaVersion")]
        [JsonRequired]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("lastObservationEpoch")]
        [JsonRequired]
        public long LastObservationEpoch { get; set; }

        [JsonPropertyName("entries")]
        [JsonRequired]
        public List<RosterFileEntry?>? Entries { get; set; }
    }

    internal sealed class RosterFileEntry
    {
        [JsonPropertyName("slot")]
        [JsonRequired]
        public int Slot { get; set; }

        [JsonPropertyName("processId")]
        [JsonRequired]
        public int ProcessId { get; set; }

        /// <summary>UTC, "yyyy-MM-ddTHH:mm:ss.fffffffZ".</summary>
        [JsonPropertyName("creationTimeUtc")]
        [JsonRequired]
        public string? CreationTimeUtc { get; set; }
    }
}

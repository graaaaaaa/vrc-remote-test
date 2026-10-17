using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// One VRChat client the Bridge launched in Relaunch mode. A running process is this
/// client only when its PID, its creation time (compared at full precision), its session
/// and its name all match (multi-client plan section 13.3).
/// </summary>
/// <param name="LaunchOrder">Zero-based position in its launch batch; the Log Viewer shows the earliest living one.</param>
public sealed record ManagedClient(
    int ProcessId, DateTime CreationTimeUtc, int SessionId, int Profile, int LaunchOrder);

public enum ManagedClientLoadStatus
{
    /// <summary>No file yet: nothing was ever launched in Relaunch mode. Normal on first use.</summary>
    Missing,

    Loaded,

    /// <summary>The file exists but is not a record this Bridge wrote; no client counts as launched by the Bridge.</summary>
    Corrupt,

    /// <summary>The file could not be read (access denied, I/O error); nothing may be closed or deployed.</summary>
    Unreadable,
}

public sealed class ManagedClientLoadResult
{
    private ManagedClientLoadResult(
        ManagedClientLoadStatus status, IReadOnlyList<ManagedClient> clients, string? problem)
    {
        Status = status;
        Clients = clients;
        Problem = problem;
    }

    public ManagedClientLoadStatus Status { get; }

    /// <summary>Empty unless <see cref="Status"/> is <see cref="ManagedClientLoadStatus.Loaded"/>.</summary>
    public IReadOnlyList<ManagedClient> Clients { get; }

    /// <summary>Why the file was corrupt or unreadable.</summary>
    public string? Problem { get; }

    public static ManagedClientLoadResult Missing() => new(ManagedClientLoadStatus.Missing, [], null);

    public static ManagedClientLoadResult Loaded(IReadOnlyList<ManagedClient> clients) =>
        new(ManagedClientLoadStatus.Loaded, clients, null);

    public static ManagedClientLoadResult Corrupt(string problem) => new(ManagedClientLoadStatus.Corrupt, [], problem);

    public static ManagedClientLoadResult Unreadable(string problem) =>
        new(ManagedClientLoadStatus.Unreadable, [], problem);
}

public interface IManagedClientStore
{
    string FilePath { get; }

    /// <summary>Reads the file; never throws.</summary>
    ManagedClientLoadResult Load();

    /// <summary>
    /// Replaces the file with a fully flushed new one. Throws IOException or
    /// UnauthorizedAccessException when it cannot; the previous file is then unchanged.
    /// </summary>
    void Save(IReadOnlyList<ManagedClient> clients);

    /// <summary>
    /// The clients last loaded or saved, for readers outside the deploy flow (the Log
    /// Viewer). Reads the file once if nothing was loaded yet; empty when it cannot.
    /// </summary>
    IReadOnlyList<ManagedClient> Known { get; }
}

/// <summary>
/// <c>StateDirectory\managed-clients.json</c>: the Relaunch-mode record of the VRChat
/// clients the Bridge launched, kept outside the share like the rest of the Bridge's state
/// (StateDirectory cannot be configured, see BridgeOptions). Only StagingWatcher's deploy
/// flow writes it.
///
/// Anything but a well-formed record of this schema is treated as corrupt, and a corrupt
/// record means no client is the Bridge's: with VRChat running, the next deploy then stops
/// with VRCHAT_UNMANAGED_RUNNING instead of closing a process on the strength of a record
/// it cannot trust. A file that cannot be read at all is reported separately, since one
/// that cannot be read probably cannot be written either (multi-client plan section 13.3).
/// </summary>
public sealed class ManagedClientStore : IManagedClientStore
{
    public const string FileName = "managed-clients.json";

    internal const int SchemaVersion = 1;

    /// <summary>Four entries take well under a kilobyte; the cap only bounds what is ever buffered.</summary>
    internal const int MaxFileBytes = 64 * 1024;

    /// <summary>Round-trip UTC with all seven fractional digits, i.e. the full 100 ns precision of DateTime.</summary>
    private const string CreationTimeFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private IReadOnlyList<ManagedClient>? _known;

    public ManagedClientStore(IOptions<BridgeOptions> options)
        : this(options.Value.StateDirectory)
    {
    }

    internal ManagedClientStore(string stateDirectory)
    {
        FilePath = Path.Combine(stateDirectory, FileName);
    }

    public string FilePath { get; }

    /// <summary>A fixed name: only one Bridge writes (BridgeInstanceLock), and a leftover is simply overwritten.</summary>
    private string TemporaryPath => FilePath + ".tmp";

    public IReadOnlyList<ManagedClient> Known
    {
        get
        {
            lock (_gate)
            {
                if (_known is null)
                {
                    LoadLocked();
                }

                return _known ?? [];
            }
        }
    }

    public ManagedClientLoadResult Load()
    {
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    public void Save(IReadOnlyList<ManagedClient> clients)
    {
        var file = new StateFile
        {
            SchemaVersion = SchemaVersion,
            Clients = clients.Select(c => (ClientEntry?)new ClientEntry
            {
                ProcessId = c.ProcessId,
                CreationTimeUtc = c.CreationTimeUtc.ToUniversalTime().ToString(CreationTimeFormat, CultureInfo.InvariantCulture),
                SessionId = c.SessionId,
                Profile = c.Profile,
                LaunchOrder = c.LaunchOrder,
            }).ToList(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, SerializerOptions);

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var stream = new FileStream(TemporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(TemporaryPath, FilePath, overwrite: true);
            }
            catch
            {
                TryDeleteTemporary();
                throw;
            }

            _known = clients.ToArray();
        }
    }

    private ManagedClientLoadResult LoadLocked()
    {
        var result = Read();
        switch (result.Status)
        {
            case ManagedClientLoadStatus.Loaded:
            case ManagedClientLoadStatus.Missing:
            case ManagedClientLoadStatus.Corrupt:
                _known = result.Clients;
                break;
            case ManagedClientLoadStatus.Unreadable:
                break; // keep what was known; a later read may succeed
        }

        return result;
    }

    private ManagedClientLoadResult Read()
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var buffer = new byte[MaxFileBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            if (total > MaxFileBytes)
            {
                return ManagedClientLoadResult.Corrupt($"the file is larger than {MaxFileBytes} bytes");
            }

            bytes = buffer[..total];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ManagedClientLoadResult.Missing();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ManagedClientLoadResult.Unreadable(ex.Message);
        }

        // Tolerated, not written: an editor may add one if someone opens the file.
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith(Utf8Bom))
        {
            json = json[Utf8Bom.Length..];
        }

        StateFile? file;
        try
        {
            file = JsonSerializer.Deserialize<StateFile>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return ManagedClientLoadResult.Corrupt($"it is not a valid record ({ex.Message})");
        }

        return Validate(file);
    }

    /// <summary>Whether a parsed file is a record this Bridge could have written.</summary>
    private static ManagedClientLoadResult Validate(StateFile? file)
    {
        if (file is null)
        {
            return ManagedClientLoadResult.Corrupt("it is empty");
        }

        if (file.SchemaVersion != SchemaVersion)
        {
            return ManagedClientLoadResult.Corrupt($"schemaVersion {file.SchemaVersion} is unknown");
        }

        if (file.Clients is not { } entries)
        {
            return ManagedClientLoadResult.Corrupt("clients is missing");
        }

        if (entries.Count > BridgeOptions.MaxVrchatClientCount)
        {
            return ManagedClientLoadResult.Corrupt(
                $"it lists {entries.Count} clients, more than {BridgeOptions.MaxVrchatClientCount}");
        }

        var clients = new List<ManagedClient>();
        foreach (var entry in entries)
        {
            if (entry is null)
            {
                return ManagedClientLoadResult.Corrupt("a client entry is null");
            }

            if (entry.ProcessId <= 0
                || entry.SessionId < 0
                || entry.Profile is < 0 or > BridgeOptions.MaxVrchatProfile
                || entry.LaunchOrder is < 0 or >= BridgeOptions.MaxVrchatClientCount)
            {
                return ManagedClientLoadResult.Corrupt($"the entry for PID {entry.ProcessId} has a value out of range");
            }

            if (entry.CreationTimeUtc is null
                || !DateTime.TryParseExact(
                    entry.CreationTimeUtc,
                    CreationTimeFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var creationTimeUtc))
            {
                return ManagedClientLoadResult.Corrupt($"the entry for PID {entry.ProcessId} has no valid creationTimeUtc");
            }

            clients.Add(new ManagedClient(
                entry.ProcessId, creationTimeUtc, entry.SessionId, entry.Profile, entry.LaunchOrder));
        }

        if (clients.Select(c => c.ProcessId).Distinct().Count() != clients.Count)
        {
            return ManagedClientLoadResult.Corrupt("a PID is listed twice");
        }

        if (clients.Select(c => c.LaunchOrder).Distinct().Count() != clients.Count)
        {
            return ManagedClientLoadResult.Corrupt("a launch order is listed twice");
        }

        return ManagedClientLoadResult.Loaded(clients);
    }

    private void TryDeleteTemporary()
    {
        try
        {
            File.Delete(TemporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next save.
        }
    }

    internal sealed class StateFile
    {
        [JsonPropertyName("schemaVersion")]
        [JsonRequired]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("clients")]
        [JsonRequired]
        public List<ClientEntry?>? Clients { get; set; }
    }

    internal sealed class ClientEntry
    {
        [JsonPropertyName("processId")]
        [JsonRequired]
        public int ProcessId { get; set; }

        /// <summary>UTC, "yyyy-MM-ddTHH:mm:ss.fffffffZ".</summary>
        [JsonPropertyName("creationTimeUtc")]
        [JsonRequired]
        public string? CreationTimeUtc { get; set; }

        [JsonPropertyName("sessionId")]
        [JsonRequired]
        public int SessionId { get; set; }

        [JsonPropertyName("profile")]
        [JsonRequired]
        public int Profile { get; set; }

        [JsonPropertyName("launchOrder")]
        [JsonRequired]
        public int LaunchOrder { get; set; }
    }
}

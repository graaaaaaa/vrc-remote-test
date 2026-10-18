using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>A slot's markers file to write; acknowledged with <see cref="VrchatClientObserver.AcknowledgeMarkers"/> once written.</summary>
public sealed record MarkerPublication(int Slot, string Content, object Key);

/// <summary>
/// The previous client's markers for slot-{n}.markers.prev.log, to write before the slot's
/// new markers file: <paramref name="Content"/> when this Bridge followed that client, or
/// null when it did not (it ran before a restart), in which case the published file is
/// moved there as it is. Acknowledged with <see cref="VrchatClientObserver.AcknowledgePrevious"/>.
/// </summary>
public sealed record PreviousMarkers(int Slot, string? Content);

/// <summary>What one observation produced for VrchatLogService to publish.</summary>
/// <param name="MarkerFiles">The markers files whose content changed and was not acknowledged yet.</param>
/// <param name="PreviousMarkers">The slots whose previous client's markers still have to be kept.</param>
/// <param name="NewGenerations">Slots that a new client took in this observation.</param>
/// <param name="SlotNotices">Lines to put above a slot's log (Logging not Full, login maybe expired).</param>
public sealed record VrchatObservation(
    VrchatClients Clients,
    IReadOnlyList<MarkerPublication> MarkerFiles,
    IReadOnlyList<PreviousMarkers> PreviousMarkers,
    IReadOnlySet<int> NewGenerations,
    IReadOnlyDictionary<int, IReadOnlyList<string>> SlotNotices);

/// <summary>
/// Follows each slot's client through its log (client-observation plan F1 and F3): the
/// files VrchatLogReader resolved are read incrementally by <see cref="VrchatLogScanner"/>,
/// every complete line goes to the slot's <see cref="VrchatClientStateTracker"/> and
/// <see cref="VrchatMarkerLog"/>, and the result is vrchat-clients.json and the markers files.
///
/// Each slot's client gets an observation epoch when this Bridge starts following it, and a
/// new one whenever its log is read again from its start: when the client gets another
/// file, when the file shrinks or is replaced (another creation time), and when the marker
/// prefixes change. Past lines read again are not new events: the epoch tells readers that
/// seq values start over. Reading is bounded per poll (<see cref="PerClientBudgetBytes"/> per
/// client and <see cref="TotalBudgetBytes"/> in all); a client whose log is not read to its
/// end, or could not be read this time, is catchingUp. A client confirmed to have exited is
/// read once more to the end of its file, and so is one whose slot a new client takes in the
/// same poll, so its last lines are not lost; its markers then become the slot's .prev.
///
/// Markers files and .prev files count as published only once VrchatLogService acknowledges
/// them, so one that could not be written is offered again on the next poll.
///
/// Only VrchatLogService's loop uses it.
/// </summary>
public sealed class VrchatClientObserver
{
    internal const long PerClientBudgetBytes = 4L * 1024 * 1024;
    internal const long TotalBudgetBytes = 16L * 1024 * 1024;

    /// <summary>How long after a client's start the absence of a line counts (Logging level, login).</summary>
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    private const int SlotCount = VrchatClientRoster.SlotCount;

    private readonly BridgeOptions _options;
    private readonly VrchatClientRoster _roster;
    private readonly IManagedClientStore? _managedClients;
    private readonly Func<string, DateTime> _creationTimeUtc;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger _logger;
    private readonly SlotObservation?[] _slots = new SlotObservation?[SlotCount];
    private readonly PreviousMarkers?[] _pendingPrevious = new PreviousMarkers?[SlotCount];

    public VrchatClientObserver(
        IOptions<BridgeOptions> options,
        VrchatClientRoster roster,
        IManagedClientStore managedClients,
        ILogger<VrchatClientObserver> logger)
        : this(options.Value, roster, managedClients, File.GetCreationTimeUtc, () => DateTimeOffset.UtcNow, logger)
    {
    }

    /// <summary>Test seam: file creation times and the clock.</summary>
    internal VrchatClientObserver(
        BridgeOptions options,
        VrchatClientRoster roster,
        IManagedClientStore? managedClients,
        Func<string, DateTime> creationTimeUtc,
        Func<DateTimeOffset> utcNow,
        ILogger? logger = null)
    {
        _options = options;
        _roster = roster;
        _managedClients = managedClients;
        _creationTimeUtc = creationTimeUtc;
        _utcNow = utcNow;
        _logger = logger ?? NullLogger.Instance;
    }

    private IReadOnlyList<string> Prefixes => _options.ResolvedLogMarkerPrefixes;

    public VrchatObservation Observe(VrchatLogReadResult read, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        var budget = TotalBudgetBytes;
        var markerFiles = new List<MarkerPublication>();
        var notices = new Dictionary<int, IReadOnlyList<string>>();
        var newGenerations = new HashSet<int>();
        var entries = new List<VrchatClientEntry>();
        var records = _options.IsRelaunchMode && _managedClients is not null ? _managedClients.Known : [];

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var rosterSlot = read.Roster.Slots[slot];
            var log = read.Slots[slot];
            var entry = rosterSlot.Entry;
            var observation = _slots[slot];
            var isNewGeneration = read.Roster.NewGenerations.Contains(slot);
            if (observation is not null && (isNewGeneration || observation.Entry != entry))
            {
                // The slot's previous client, which may have exited in the same poll: what it
                // wrote last is read before its markers are kept as .prev.
                if (observation.Scanner is not null && !observation.Drained)
                {
                    budget -= ReadInto(observation, Math.Min(budget, PerClientBudgetBytes), exited: true, cancellationToken);
                }

                _pendingPrevious[slot] = new PreviousMarkers(slot, observation.RenderMarkers(slot));
                observation = null;
                _slots[slot] = null;
            }

            if (entry is null)
            {
                continue;
            }

            if (isNewGeneration)
            {
                newGenerations.Add(slot);
                _pendingPrevious[slot] ??= new PreviousMarkers(slot, null);
            }

            // An exited client seen first now (e.g. before a Bridge restart) is not read: its
            // file is unknown, and its markers file stays as it was published.
            if (observation is null && rosterSlot.Liveness != RosterLiveness.Exited)
            {
                observation = new SlotObservation(entry, Prefixes, _roster.AllocateEpoch());
                _slots[slot] = observation;
            }

            if (observation is not null)
            {
                budget -= Scan(observation, rosterSlot, log, Math.Min(budget, PerClientBudgetBytes), cancellationToken);
                if (observation.MarkersChanged)
                {
                    markerFiles.Add(new MarkerPublication(slot, observation.RenderMarkers(slot), observation.CurrentKey));
                }
            }

            var record = records.FirstOrDefault(r =>
                r.ProcessId == entry.ProcessId && r.CreationTimeUtc.Ticks == entry.CreationTimeUtc.Ticks);
            var (clientEntry, slotNotices) = Describe(slot, entry, rosterSlot, log, observation, record, now);
            entries.Add(clientEntry);
            if (slotNotices.Count > 0)
            {
                notices[slot] = slotNotices;
            }
        }

        var clients = new VrchatClients
        {
            LogMarkerPrefixes = Prefixes,
            ProcessListReadable = read.Snapshot.EnumerationError is null,
            UnassignedClients = read.Roster.Unassigned + read.Snapshot.UnverifiedCount,
            Clients = entries,
            UpdatedAtUtc = now,
        };

        var previous = _pendingPrevious.Where(p => p is not null).Select(p => p!).ToList();
        return new VrchatObservation(clients, markerFiles, previous, newGenerations, notices);
    }

    /// <summary>The slot's markers file was written; it is not offered again until it changes.</summary>
    public void AcknowledgeMarkers(int slot, object key) => _slots[slot]?.Acknowledge(key);

    /// <summary>The previous client's markers were kept as .prev.</summary>
    public void AcknowledgePrevious(PreviousMarkers previous)
    {
        if (ReferenceEquals(_pendingPrevious[previous.Slot], previous))
        {
            _pendingPrevious[previous.Slot] = null;
        }
    }

    /// <summary>Reads what the slot's file gained, restarting the epoch when the file is another one. Returns the bytes read.</summary>
    private long Scan(
        SlotObservation observation, RosterSlot rosterSlot, SlotLogResult log, long budget, CancellationToken cancellationToken)
    {
        var exited = rosterSlot.Liveness == RosterLiveness.Exited;
        string? path = null;
        if (log.Status == SlotLogStatus.Shown)
        {
            path = log.Path;
        }
        else if (exited && observation.Scanner is { } last && !observation.Drained)
        {
            path = last.Path;
        }

        // A file that just disappeared is released by the reader on its next poll; it is not
        // a reason to start a new epoch.
        if (path is null || budget <= 0 || !File.Exists(path))
        {
            return 0;
        }

        DateTime created;
        try
        {
            created = _creationTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            observation.ReadFailed = true;
            observation.CatchingUp = true;
            return 0;
        }

        if (observation.Scanner is null)
        {
            observation.StartReading(new VrchatLogScanner(path, created), Prefixes, observation.Epoch);
            _logger.LogInformation(
                "Reading {Path} for VRChat client {ProcessId} in slot {Slot} (observation epoch {Epoch}).",
                path, observation.Entry.ProcessId, observation.Entry.Slot, observation.Epoch);
        }
        else if (!string.Equals(observation.Scanner.Path, path, StringComparison.OrdinalIgnoreCase)
            || observation.Scanner.FileCreationUtc != created
            || !observation.Prefixes.SequenceEqual(Prefixes))
        {
            Restart(observation, path, created, "another file or other marker prefixes");
        }

        return ReadInto(observation, budget, exited, cancellationToken, path, created);
    }

    /// <summary>
    /// Reads from the observation's scanner and passes every line read on, including those
    /// read before a read failed (the scanner has moved past them).
    /// </summary>
    private long ReadInto(
        SlotObservation observation,
        long budget,
        bool exited,
        CancellationToken cancellationToken,
        string? path = null,
        DateTime created = default)
    {
        var result = observation.Scanner!.Read(budget, cancellationToken);
        if (result.Outcome == VrchatLogScanOutcome.Shrunk && path is not null)
        {
            Restart(observation, path, created, "the file shrank");
            result = observation.Scanner!.Read(budget, cancellationToken);
        }

        foreach (var line in result.Lines)
        {
            observation.Tracker.OnLine(line);
            observation.Markers.OnLine(line);
        }

        observation.Markers.Flush();
        observation.ReadFailed = result.Outcome != VrchatLogScanOutcome.Read;
        observation.CatchingUp = observation.ReadFailed || !result.ReachedEnd;
        if (exited && result.Outcome == VrchatLogScanOutcome.Read && result.ReachedEnd)
        {
            observation.Drained = true;
        }

        return result.BytesRead;
    }

    private void Restart(SlotObservation observation, string path, DateTime created, string reason)
    {
        var epoch = _roster.AllocateEpoch();
        observation.StartReading(new VrchatLogScanner(path, created), Prefixes, epoch);
        _logger.LogInformation(
            "Reading {Path} from its start for VRChat client {ProcessId} in slot {Slot} (observation epoch {Epoch}, {Reason}).",
            path, observation.Entry.ProcessId, observation.Entry.Slot, epoch, reason);
    }

    private (VrchatClientEntry Entry, IReadOnlyList<string> Notices) Describe(
        int slot,
        RosterEntry entry,
        RosterSlot rosterSlot,
        SlotLogResult log,
        SlotObservation? observation,
        ManagedClient? record,
        DateTimeOffset now)
    {
        var tracker = observation?.Tracker;
        var started = new DateTimeOffset(entry.CreationTimeUtc, TimeSpan.Zero);
        var pastGrace = now - started >= StartupGrace;
        var catchingUp = observation is { Scanner: not null } && observation.CatchingUp;
        var loggingLevel = LoggingLevel(observation, pastGrace, catchingUp);
        var loggingReadable = loggingLevel is not ("off" or "errorsOnly");

        string? state;
        var useLog = rosterSlot.Liveness == RosterLiveness.Running && log.Status == SlotLogStatus.Shown && tracker is not null;
        switch (rosterSlot.Liveness)
        {
            case RosterLiveness.Exited:
                state = "left";
                break;
            case RosterLiveness.Unknown:
                state = "unknown";
                break;
            default:
                state = log.Status switch
                {
                    SlotLogStatus.Shown when observation is { ReadFailed: true } => "unknown",
                    SlotLogStatus.Shown when !loggingReadable => null,
                    SlotLogStatus.Shown => StateName(tracker?.State ?? VrchatClientLogState.Starting),
                    SlotLogStatus.NotYet => "starting",
                    _ => "unknown",
                };
                break;
        }

        var inAttempt = useLog && loggingReadable && state is "joining" or "joined";
        var warnings = new List<string>();
        var notices = new List<string>();
        if (rosterSlot.Liveness == RosterLiveness.Running && log.Status is not (SlotLogStatus.Shown or SlotLogStatus.NotRunning))
        {
            warnings.Add(LogRedactor.Redact(log.Content.Content));
        }

        if (rosterSlot.Liveness == RosterLiveness.Running && observation is { ReadFailed: true })
        {
            warnings.Add("The Bridge could not read this client's log this time; it tries again on the next poll.");
        }

        if (rosterSlot.Liveness == RosterLiveness.Running && !loggingReadable)
        {
            var notice =
                $"VRChat's Logging is {(loggingLevel == "off" ? "Off" : "errors only")} for this client, so its state, " +
                "players and master cannot be read. Set Settings > Debug > Logging to Full in VRChat on Windows.";
            warnings.Add(notice);
            notices.Add(notice);
        }
        else if (rosterSlot.Liveness == RosterLiveness.Running && loggingLevel == "full" && pastGrace && !catchingUp
            && tracker is { Authenticated: false })
        {
            var profile = record is not null ? $"profile {record.Profile}" : "this client's profile";
            var notice =
                $"No \"User Authenticated\" line {StartupGrace.TotalSeconds:0} s after this client started: the login of " +
                $"{profile} may have expired. Start VRChat with that profile on Windows and log in again.";
            warnings.Add(notice);
            notices.Add(notice);
        }

        var clientEntry = new VrchatClientEntry
        {
            Slot = slot,
            ClientGeneration = entry.ClientGeneration,
            ObservationEpoch = observation?.Epoch,
            ProcessId = entry.ProcessId,
            CreationTimeUtc = started,
            Running = rosterSlot.Liveness switch
            {
                RosterLiveness.Running => true,
                RosterLiveness.Exited => false,
                _ => null,
            },
            WatchWorlds = rosterSlot.Client?.WatchWorldsDetected,
            Profile = record?.Profile,
            LogStatus = StatusName(log.Status),
            State = state,
            RequestedInstance = inAttempt ? tracker!.RequestedInstance : null,
            Instance = inAttempt && state == "joined" ? tracker!.Instance : null,
            InErrorWorld = inAttempt && state == "joined" ? tracker!.InErrorWorld : null,
            PlayersInInstance = inAttempt ? tracker!.Players?.Select(LogRedactor.Redact).ToList() : null,
            IsMaster = inAttempt ? tracker!.IsMaster : null,
            LoggingLevel = loggingLevel,
            Authenticated = tracker is { Authenticated: true } ? true : null,
            JoinAttemptSeq = tracker?.JoinAttemptSeq,
            JoinedSeq = tracker?.JoinedSeq,
            ObservedThroughSeq = tracker?.ObservedThroughSeq ?? 0,
            CatchingUp = catchingUp,
            DroppedLines = observation?.Scanner?.TruncatedLines ?? 0,
            LastEvents = tracker?.Events.Select(e => new VrchatClientEvent
            {
                Kind = e.Kind,
                Seq = e.Seq,
                AtUtc = VrchatLogLine.ToUtc(e.LocalTime, TimeZoneInfo.Local),
                Player = e.Player is null ? null : LogRedactor.Redact(e.Player),
                Instance = e.Instance,
            }).ToList() ?? [],
            Warnings = warnings,
        };

        return (clientEntry, notices);
    }

    /// <summary>
    /// full once a Debug line was read. Past the startup grace, with the log read to its end:
    /// off for a file still empty, errorsOnly for Error lines alone (S5a B). Otherwise unknown.
    /// </summary>
    private static string LoggingLevel(SlotObservation? observation, bool pastGrace, bool catchingUp)
    {
        if (observation is not { Scanner: { } scanner } || observation.Tracker is not { } tracker)
        {
            return "unknown";
        }

        if (tracker.SawDebug)
        {
            return "full";
        }

        if (!pastGrace || catchingUp)
        {
            return "unknown";
        }

        if (scanner.LastLength == 0)
        {
            return "off";
        }

        return tracker.SawError && !tracker.SawWarning ? "errorsOnly" : "unknown";
    }

    private static string StateName(VrchatClientLogState state) => state switch
    {
        VrchatClientLogState.Starting => "starting",
        VrchatClientLogState.Joining => "joining",
        VrchatClientLogState.Joined => "joined",
        _ => "unknown",
    };

    private static string StatusName(SlotLogStatus status) => status switch
    {
        SlotLogStatus.Shown => "shown",
        SlotLogStatus.NotYet => "notYet",
        SlotLogStatus.Unmatched => "unmatched",
        SlotLogStatus.CannotCheck => "cannotCheck",
        SlotLogStatus.NotRunning => "notRunning",
        _ => "unavailable",
    };

    private sealed class SlotObservation(RosterEntry entry, IReadOnlyList<string> prefixes, long epoch)
    {
        private object? _acknowledged;

        public RosterEntry Entry { get; } = entry;

        public IReadOnlyList<string> Prefixes { get; private set; } = prefixes;

        public long Epoch { get; private set; } = epoch;

        public VrchatLogScanner? Scanner { get; private set; }

        public VrchatClientStateTracker Tracker { get; private set; } = new();

        public VrchatMarkerLog Markers { get; private set; } = new(prefixes);

        public bool CatchingUp { get; set; }

        /// <summary>The last read failed; the next poll tries again.</summary>
        public bool ReadFailed { get; set; }

        /// <summary>The exited client's file was read to its end.</summary>
        public bool Drained { get; set; }

        /// <summary>Until the log is read, the markers file says it may be incomplete.</summary>
        private bool HeaderCatchingUp => Scanner is null || CatchingUp;

        public object CurrentKey => (Epoch, HeaderCatchingUp, Markers.Version);

        public bool MarkersChanged => !Equals(_acknowledged, CurrentKey);

        public void Acknowledge(object key) => _acknowledged = key;

        /// <summary>Starts reading <paramref name="scanner"/>'s file from its start, with fresh state, under <paramref name="epoch"/>.</summary>
        public void StartReading(VrchatLogScanner scanner, IReadOnlyList<string> prefixes, long epoch)
        {
            Scanner = scanner;
            Prefixes = prefixes;
            Epoch = epoch;
            Tracker = new VrchatClientStateTracker();
            Markers = new VrchatMarkerLog(prefixes);
            CatchingUp = true;
            ReadFailed = false;
            Drained = false;
        }

        public string RenderMarkers(int slot) => Markers.Render(new MarkerFileHeader
        {
            Slot = slot,
            ClientGeneration = Entry.ClientGeneration,
            ObservationEpoch = Epoch,
            CatchingUp = HeaderCatchingUp,
            DroppedRecords = Markers.DroppedRecords,
            DroppedThroughSeq = Markers.DroppedThroughSeq,
            Prefixes = Prefixes,
        });
    }
}

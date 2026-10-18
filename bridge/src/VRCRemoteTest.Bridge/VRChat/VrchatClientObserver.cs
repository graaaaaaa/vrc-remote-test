using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>What one observation produced for VrchatLogService to publish.</summary>
/// <param name="MarkerFiles">The markers files whose content changed, by slot.</param>
/// <param name="NewGenerations">
/// Slots that a new client took: the published markers file is the previous client's and
/// moves to slot-{n}.markers.prev.log before anything new is written for the slot.
/// </param>
/// <param name="SlotNotices">Lines to put above a slot's log (Logging not Full, login maybe expired).</param>
public sealed record VrchatObservation(
    VrchatClients Clients,
    IReadOnlyDictionary<int, string> MarkerFiles,
    IReadOnlySet<int> NewGenerations,
    IReadOnlyDictionary<int, IReadOnlyList<string>> SlotNotices);

/// <summary>
/// Follows each slot's client through its log (client-observation plan F1 and F3): the
/// files VrchatLogReader resolved are read incrementally by <see cref="VrchatLogScanner"/>,
/// every complete line goes to the slot's <see cref="VrchatClientStateTracker"/> and
/// <see cref="VrchatMarkerLog"/>, and the result is vrchat-clients.json and the markers files.
///
/// A slot's log is read again from its start, under a new observation epoch, when the
/// Bridge starts, when the slot's client gets another file, when the file shrinks or is
/// replaced (another creation time), and when the marker prefixes change. Past lines read
/// again are not new events: the epoch tells readers that seq values start over. Reading is
/// bounded per poll (<see cref="PerClientBudgetBytes"/> per client and
/// <see cref="TotalBudgetBytes"/> in all); a client whose log is not read to its end is
/// catchingUp. A client confirmed to have exited is read once more to the end of its file,
/// so its last lines are not lost.
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
        var markerFiles = new Dictionary<int, string>();
        var notices = new Dictionary<int, IReadOnlyList<string>>();
        var newGenerations = new HashSet<int>();
        var entries = new List<VrchatClientEntry>();
        var records = _options.IsRelaunchMode && _managedClients is not null ? _managedClients.Known : [];

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var rosterSlot = read.Roster.Slots[slot];
            var log = read.Slots[slot];
            if (rosterSlot.Entry is not { } entry)
            {
                _slots[slot] = null;
                continue;
            }

            var observation = _slots[slot];
            var isNewGeneration = read.Roster.NewGenerations.Contains(slot);
            if (observation is not null && (isNewGeneration || observation.Entry != entry))
            {
                observation = null;
            }

            if (isNewGeneration)
            {
                newGenerations.Add(slot);
            }

            // An exited client seen first now (e.g. before a Bridge restart) is not read: its
            // file is unknown, and its markers file stays as it was published.
            if (observation is null && rosterSlot.Liveness != RosterLiveness.Exited)
            {
                observation = new SlotObservation(entry, Prefixes);
                _slots[slot] = observation;
            }

            if (observation is not null)
            {
                budget -= Scan(observation, rosterSlot, log, Math.Min(budget, PerClientBudgetBytes), cancellationToken);
                if (observation.MarkersChanged)
                {
                    markerFiles[slot] = observation.RenderMarkers(slot);
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

        return new VrchatObservation(clients, markerFiles, newGenerations, notices);
    }

    /// <summary>Reads what the slot's file gained, restarting the epoch when the file is another one. Returns the bytes read.</summary>
    private long Scan(
        SlotObservation observation, RosterSlot rosterSlot, SlotLogResult log, long budget, CancellationToken cancellationToken)
    {
        string? path = null;
        if (log.Status == SlotLogStatus.Shown)
        {
            path = log.Path;
        }
        else if (rosterSlot.Liveness == RosterLiveness.Exited && observation.Scanner is { } last && !observation.Drained)
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
            return 0;
        }

        if (observation.Scanner is not { } scanner
            || !string.Equals(scanner.Path, path, StringComparison.OrdinalIgnoreCase)
            || scanner.FileCreationUtc != created
            || !observation.Prefixes.SequenceEqual(Prefixes))
        {
            Restart(observation, path, created, observation.Scanner is null ? "first read" : "another file or marker prefixes");
        }

        var result = observation.Scanner!.Read(budget, cancellationToken);
        if (result.Outcome == VrchatLogScanOutcome.Shrunk)
        {
            Restart(observation, path, created, "the file shrank");
            result = observation.Scanner!.Read(budget, cancellationToken);
        }

        if (result.Outcome == VrchatLogScanOutcome.Unreadable)
        {
            return result.BytesRead;
        }

        foreach (var line in result.Lines)
        {
            observation.Tracker.OnLine(line);
            observation.Markers.OnLine(line);
        }

        observation.CatchingUp = !result.ReachedEnd;
        if (rosterSlot.Liveness == RosterLiveness.Exited && result.ReachedEnd)
        {
            observation.Drained = true;
        }

        return result.BytesRead;
    }

    private void Restart(SlotObservation observation, string path, DateTime created, string reason)
    {
        var epoch = _roster.AllocateEpoch();
        observation.Restart(new VrchatLogScanner(path, created), Prefixes, epoch);
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
            ObservationEpoch = observation?.Scanner is null ? null : observation.Epoch,
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

    private sealed class SlotObservation(RosterEntry entry, IReadOnlyList<string> prefixes)
    {
        public RosterEntry Entry { get; } = entry;

        public IReadOnlyList<string> Prefixes { get; private set; } = prefixes;

        public long Epoch { get; private set; }

        public VrchatLogScanner? Scanner { get; private set; }

        public VrchatClientStateTracker Tracker { get; private set; } = new();

        public VrchatMarkerLog Markers { get; private set; } = new(prefixes);

        public bool CatchingUp { get; set; }

        /// <summary>The exited client's file was read to its end.</summary>
        public bool Drained { get; set; }

        private (long Epoch, bool CatchingUp, long Version)? _published;

        public bool MarkersChanged => Scanner is not null && _published != (Epoch, CatchingUp, Markers.Version);

        public void Restart(VrchatLogScanner scanner, IReadOnlyList<string> prefixes, long epoch)
        {
            Scanner = scanner;
            Prefixes = prefixes;
            Epoch = epoch;
            Tracker = new VrchatClientStateTracker();
            Markers = new VrchatMarkerLog(prefixes);
            CatchingUp = true;
            Drained = false;
        }

        public string RenderMarkers(int slot)
        {
            _published = (Epoch, CatchingUp, Markers.Version);
            return Markers.Render(new MarkerFileHeader
            {
                Slot = slot,
                ClientGeneration = Entry.ClientGeneration,
                ObservationEpoch = Epoch,
                CatchingUp = CatchingUp,
                DroppedRecords = Markers.DroppedRecords,
                DroppedThroughSeq = Markers.DroppedThroughSeq,
                Prefixes = Prefixes,
            });
        }
    }
}

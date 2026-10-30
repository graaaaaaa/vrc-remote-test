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
/// read on to the end of its file, so its last lines are not lost. So is one whose slot a new
/// client takes, over as many polls as that needs (giving up only when its file is gone or
/// keeps failing for <see cref="MaxRetiringFailures"/> polls): its markers are kept as the
/// slot's .prev as they grow and once more at the end, and the new client's markers file
/// waits until then. A restart in between thus leaves at least what was read in .prev; a
/// .prev that could not be written before a restart is lost (observation only).
///
/// An epoch is used only once the roster has saved it; when it cannot, the client is not
/// observed (or its log not read again) until it can.
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

    /// <summary>A replaced client's file that fails this many polls in a row is given up, and its .prev kept as it is.</summary>
    internal const int MaxRetiringFailures = 12;

    private readonly BridgeOptions _options;
    private readonly VrchatClientRoster _roster;
    private readonly IManagedClientStore? _managedClients;
    private readonly Func<string, DateTime> _creationTimeUtc;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly ILogger _logger;
    private readonly SlotObservation?[] _slots = new SlotObservation?[SlotCount];
    private readonly PreviousMarkers?[] _pendingPrevious = new PreviousMarkers?[SlotCount];

    /// <summary>Clients whose slot a new client took, still being read to the end of their file.</summary>
    private readonly SlotObservation?[] _retiring = new SlotObservation?[SlotCount];

    private string? _lastEpochProblem;

    public VrchatClientObserver(
        IOptions<BridgeOptions> options,
        VrchatClientRoster roster,
        IManagedClientStore managedClients,
        AccountDigest accountDigest,
        ProfileAccountStore profileAccounts,
        ILogger<VrchatClientObserver> logger)
        : this(options.Value, roster, managedClients, File.GetCreationTimeUtc, () => DateTimeOffset.UtcNow, logger,
            accountDigest.Of, profileAccounts)
    {
    }

    /// <summary>Test seam: file creation times and the clock.</summary>
    internal VrchatClientObserver(
        BridgeOptions options,
        VrchatClientRoster roster,
        IManagedClientStore? managedClients,
        Func<string, DateTime> creationTimeUtc,
        Func<DateTimeOffset> utcNow,
        ILogger? logger = null,
        Func<string, string>? accountDigest = null,
        ProfileAccountStore? profileAccounts = null)
    {
        _options = options;
        _roster = roster;
        _managedClients = managedClients;
        _creationTimeUtc = creationTimeUtc;
        _utcNow = utcNow;
        _logger = logger ?? NullLogger.Instance;
        _accountDigest = accountDigest;
        _profileAccounts = profileAccounts;
    }

    /// <summary>Turns a usr_ ID into its digest where the log is read (<see cref="AccountDigest"/>).</summary>
    private readonly Func<string, string>? _accountDigest;

    private readonly ProfileAccountStore? _profileAccounts;

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
                // wrote last is read before its markers are kept as .prev. Two replacements
                // before the first is read to its end keep only the later one, as .prev does.
                _retiring[slot] = observation.Scanner is null ? null : observation;
                if (_retiring[slot] is null)
                {
                    _pendingPrevious[slot] = new PreviousMarkers(slot, observation.RenderMarkers(slot));
                }

                observation = null;
                _slots[slot] = null;
            }

            if (_retiring[slot] is { } retiring)
            {
                budget -= Retire(slot, retiring, Math.Min(budget, PerClientBudgetBytes), cancellationToken);
            }

            if (entry is null)
            {
                continue;
            }

            if (isNewGeneration)
            {
                newGenerations.Add(slot);
                if (_retiring[slot] is null)
                {
                    _pendingPrevious[slot] ??= new PreviousMarkers(slot, null);
                }
            }

            // An exited client seen first now (e.g. before a Bridge restart) is not read: its
            // file is unknown, and its markers file stays as it was published.
            if (observation is null && rosterSlot.Liveness != RosterLiveness.Exited
                && TryAllocateEpoch() is { } epoch)
            {
                observation = new SlotObservation(entry, Prefixes, epoch);
                _slots[slot] = observation;
            }

            if (observation is not null)
            {
                budget -= Scan(observation, rosterSlot, log, Math.Min(budget, PerClientBudgetBytes), cancellationToken);

                // The previous client's file stays until its .prev is in place.
                if (observation.MarkersChanged && _retiring[slot] is null)
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

    /// <summary>
    /// Reads a replaced client's file on toward its end; once there (or once it is gone or
    /// keeps failing), its markers become the slot's pending .prev. Returns the bytes read.
    /// </summary>
    private long Retire(int slot, SlotObservation retiring, long budget, CancellationToken cancellationToken)
    {
        long read = 0;
        var path = retiring.Scanner!.Path;
        if (!File.Exists(path))
        {
            retiring.RetiringFailures = MaxRetiringFailures;
        }
        else if (budget > 0)
        {
            read = ReadInto(retiring, budget, exited: true, cancellationToken);
            retiring.RetiringFailures = retiring.ReadFailed ? retiring.RetiringFailures + 1 : 0;
        }

        var done = retiring.Drained || retiring.RetiringFailures >= MaxRetiringFailures;
        if (done && !retiring.Drained)
        {
            _logger.LogWarning(
                "Gave up reading {Path} to its end for the VRChat client that had slot {Slot}; its markers are kept as they are.",
                path, slot);
        }

        // Kept as .prev while it is still read too, so a restart in between does not lose it,
        // and once more when done.
        if (done || retiring.MarkersChanged)
        {
            _pendingPrevious[slot] = new PreviousMarkers(slot, retiring.RenderMarkers(slot));
            retiring.Acknowledge(retiring.CurrentKey);
        }

        if (done)
        {
            _retiring[slot] = null;
        }

        return read;
    }

    /// <summary>A saved epoch, or null when the roster cannot save one now (logged once per problem).</summary>
    private long? TryAllocateEpoch()
    {
        try
        {
            var epoch = _roster.AllocateEpoch();
            _lastEpochProblem = null;
            return epoch;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex.Message != _lastEpochProblem)
            {
                _logger.LogWarning(ex, "No observation epoch can be saved; clients are not observed until it can.");
                _lastEpochProblem = ex.Message;
            }

            return null;
        }
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

        if (path is null)
        {
            return 0;
        }

        if (!File.Exists(path))
        {
            // Released by the reader on its next poll, and not a reason for a new epoch; until
            // then what was read is not current.
            if (exited)
            {
                observation.Drained = true;
            }
            else
            {
                observation.ReadFailed = true;
                observation.CatchingUp = true;
            }

            return 0;
        }

        if (budget <= 0)
        {
            // Not read this time: it may be behind.
            observation.CatchingUp = true;
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
            observation.StartReading(new VrchatLogScanner(path, created), Prefixes, observation.Epoch, _accountDigest);
            _logger.LogInformation(
                "Reading {Path} for VRChat client {ProcessId} in slot {Slot} (observation epoch {Epoch}).",
                path, observation.Entry.ProcessId, observation.Entry.Slot, observation.Epoch);
        }
        else if ((observation.RestartRequired
                || !string.Equals(observation.Scanner.Path, path, StringComparison.OrdinalIgnoreCase)
                || observation.Scanner.FileCreationUtc != created
                || !observation.Prefixes.SequenceEqual(Prefixes))
            && !Restart(observation, path, created, "another file, a shorter one, or other marker prefixes"))
        {
            return 0;
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
        if (result.Outcome == VrchatLogScanOutcome.Shrunk && path is not null
            && Restart(observation, path, created, "the file shrank"))
        {
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

    /// <summary>False when no epoch could be saved: the log is read again once one can, and nothing is current until then.</summary>
    private bool Restart(SlotObservation observation, string path, DateTime created, string reason)
    {
        if (TryAllocateEpoch() is not { } epoch)
        {
            // Remembered, so the file is read from its start once an epoch can be saved,
            // even if it has grown back past the old position by then.
            observation.RestartRequired = true;
            observation.ReadFailed = true;
            observation.CatchingUp = true;
            return false;
        }

        observation.StartReading(new VrchatLogScanner(path, created), Prefixes, epoch, _accountDigest);
        _logger.LogInformation(
            "Reading {Path} from its start for VRChat client {ProcessId} in slot {Slot} (observation epoch {Epoch}, {Reason}).",
            path, observation.Entry.ProcessId, observation.Entry.Slot, epoch, reason);

        return true;
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

        var running = rosterSlot.Liveness == RosterLiveness.Running;
        var logReadToEnd = useLog && observation is { ReadFailed: false } && !catchingUp;
        var loginState = tracker is { Authenticated: true } && running
            ? "authenticated"
            : running && logReadToEnd && loggingLevel == "full"
                ? pastGrace ? "likelyLoggedOut" : "notSeen"
                : "unknown";

        // A login time that the local time zone cannot place (a skipped hour) gives no account.
        VrchatClientAccount? account = null;
        if (tracker?.Account is { } seen && VrchatLogLine.ToUtc(seen.LocalTime, TimeZoneInfo.Local) is { } authenticatedAt)
        {
            account = new VrchatClientAccount
            {
                DisplayName = LogRedactor.Redact(seen.Name),
                IdDigest = seen.IdDigest,
                AuthenticatedAtUtc = authenticatedAt,
            };

            if (record is not null)
            {
                _profileAccounts?.Record(new ProfileAccount
                {
                    Profile = record.Profile,
                    DisplayName = account.DisplayName,
                    IdDigest = account.IdDigest,
                    AuthenticatedAtUtc = account.AuthenticatedAtUtc,
                    Slot = slot,
                    ClientGeneration = entry.ClientGeneration,
                });
            }
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
            LoginState = loginState,
            Account = account,
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

        /// <summary>Polls in a row that a replaced client's file could not be read.</summary>
        public int RetiringFailures { get; set; }

        /// <summary>The log must be read again from its start before anything else is read.</summary>
        public bool RestartRequired { get; set; }

        /// <summary>Until the log is read, the markers file says it may be incomplete.</summary>
        private bool HeaderCatchingUp => Scanner is null || CatchingUp;

        public object CurrentKey => (Epoch, HeaderCatchingUp, Markers.Version);

        public bool MarkersChanged => !Equals(_acknowledged, CurrentKey);

        public void Acknowledge(object key) => _acknowledged = key;

        /// <summary>Starts reading <paramref name="scanner"/>'s file from its start, with fresh state, under <paramref name="epoch"/>.</summary>
        public void StartReading(
            VrchatLogScanner scanner, IReadOnlyList<string> prefixes, long epoch, Func<string, string>? accountDigest)
        {
            Scanner = scanner;
            Prefixes = prefixes;
            Epoch = epoch;
            Tracker = new VrchatClientStateTracker(accountDigest);
            Markers = new VrchatMarkerLog(prefixes);
            CatchingUp = true;
            ReadFailed = false;
            Drained = false;
            RestartRequired = false;
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

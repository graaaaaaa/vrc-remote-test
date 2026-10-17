using Microsoft.Extensions.Logging;
using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Spaces the launches of one batch of VRChat clients, shared by the Reload and Relaunch
/// flows so both start their clients the same way (multi-client plan section 12.5).
/// Clients whose logging starts within the same second write to one output_log file
/// (spike option B), so each launch waits at least <see cref="LaunchInterval"/> and also
/// for the previous client's file (<see cref="WaitBeforeNextLaunchAsync"/>).
/// </summary>
internal sealed class VrchatLaunchPacer
{
    /// <summary>The SDK's interval, kept as the shortest one.</summary>
    internal static readonly TimeSpan LaunchInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The most a batch waits for output_log files in total, counted from just before the
    /// first launch. VRChat created its file 0.70-0.84 s after the process start without
    /// load, but 11-22 s after it with the CPU at about 100%
    /// (docs/validation/multi-client-spike.md, PR #2 verification, item 12). The same
    /// length as <see cref="VrchatLogMatcher.MaxRangeLength"/>: a first client's file that
    /// appears in time can still be matched. It also bounds how much the waiting delays
    /// the batch's result.
    /// </summary>
    internal static readonly TimeSpan LogWaitBudget = VrchatLogMatcher.MaxRangeLength;

    internal static readonly TimeSpan LogPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<IReadOnlyList<VrchatLogMatcher.Candidate>?> _listLogFiles;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeZoneInfo _timeZone;

    /// <summary>
    /// The time is read only through <paramref name="utcNow"/> and waited only through
    /// <paramref name="delay"/>, so a test can drive the pacing with a fake clock and a
    /// fake log directory.
    /// </summary>
    public VrchatLaunchPacer(
        ILogger logger,
        Func<TimeSpan, CancellationToken, Task> delay,
        Func<IReadOnlyList<VrchatLogMatcher.Candidate>?> listLogFiles,
        Func<DateTime> utcNow,
        TimeZoneInfo timeZone)
    {
        _logger = logger;
        _delay = delay;
        _listLogFiles = listLogFiles;
        _utcNow = utcNow;
        _timeZone = timeZone;
    }

    /// <summary>
    /// Lists the files that already exist, so files created after the first launch can
    /// be told apart. A batch of one client never waits; a directory that cannot be listed
    /// leaves only the fixed interval.
    /// </summary>
    public Batch StartBatch(int count)
    {
        if (count <= 1)
        {
            return new Batch(count, null);
        }

        var deadlineUtc = _utcNow() + LogWaitBudget;
        var existing = _listLogFiles();
        if (existing is null)
        {
            _logger.LogWarning(
                "Could not list the VRChat log directory; launching VRChat clients {Interval}s apart without waiting " +
                "for their output_log files. The Log Viewer may not be able to tell their logs apart.",
                LaunchInterval.TotalSeconds);
            return new Batch(count, null);
        }

        return new Batch(
            count, new LogWatch(existing.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase), deadlineUtc));
    }

    /// <summary>
    /// Waits <see cref="LaunchInterval"/> after the previous launch, and then until every
    /// client launched so far has created an output_log file that did not exist before
    /// the batch, and until the next client's start is late enough that the newest of
    /// those files stays out of its matching range (<see cref="VrchatLogMatcher.EarliestNextStartUtc"/>).
    /// Each client's logging then starts in a later second than the previous one's, so
    /// every client gets its own file and the first client's file can be matched
    /// (multi-client plan section 13.2).
    ///
    /// This only spaces the launches: it never decides which file is whose, so a file
    /// that appears for another reason (e.g. a VRChat started by hand meanwhile) can at
    /// worst end the wait early, and <see cref="VrchatLogMatcher"/> still refuses any file
    /// it cannot attribute. Without load the files exist by the end of the interval, so
    /// the clients start 3 s apart. The total wait is bounded by <see cref="LogWaitBudget"/>;
    /// past it, the remaining clients start 3 s apart.
    /// </summary>
    public async Task WaitBeforeNextLaunchAsync(
        Batch batch, int launched, DateTime previousLaunchUtc, CancellationToken cancellationToken)
    {
        await DelayUntilAsync(previousLaunchUtc + LaunchInterval, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Watch is not { GaveUp: false } watch)
        {
            return;
        }

        var count = batch.Count;
        var reportedWait = false;
        while (true)
        {
            var newFiles = _listLogFiles()?.Where(c => !watch.Existing.Contains(c.Path)).ToList();
            if (newFiles is not null && newFiles.Count >= launched)
            {
                var newest = newFiles
                    .Select(c => (File: c, Time: VrchatLogMatcher.TrustedNameUtc(c, _timeZone) ?? c.CreationTimeUtc))
                    .MaxBy(c => c.Time);

                // Logged for every client, so the Bridge log records how long VRChat took.
                _logger.LogInformation(
                    "New output_log {File} (created {CreationTimeUtc:O}, {Seconds:F1}s after VRChat client " +
                    "{Index}/{Count} was launched); launching the next client.",
                    Path.GetFileName(newest.File.Path),
                    newest.File.CreationTimeUtc,
                    (newest.File.CreationTimeUtc - previousLaunchUtc).TotalSeconds,
                    launched,
                    count);

                // A name time from a wrong clock must not hold the batch past its deadline.
                var earliest = VrchatLogMatcher.EarliestNextStartUtc(newest.Time);
                await DelayUntilAsync(earliest < watch.DeadlineUtc ? earliest : watch.DeadlineUtc, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var now = _utcNow();
            if (now >= watch.DeadlineUtc)
            {
                watch.GaveUp = true;
                _logger.LogWarning(
                    "VRChat client {Index}/{Count} has not created an output_log file within {Budget}s of the first " +
                    "launch; launching the remaining clients {Interval}s apart. Their logs may end up in one shared " +
                    "file, which the Log Viewer does not show as the first client's log.",
                    launched, count, LogWaitBudget.TotalSeconds, LaunchInterval.TotalSeconds);
                return;
            }

            if (!reportedWait)
            {
                _logger.LogInformation(
                    "Waiting for VRChat client {Index}/{Count} to create its output_log file before launching the next " +
                    "client (up to {Remaining:F0}s more), so that their logs stay apart.",
                    launched, count, (watch.DeadlineUtc - now).TotalSeconds);
                reportedWait = true;
            }

            var remaining = watch.DeadlineUtc - now;
            await _delay(remaining < LogPollInterval ? remaining : LogPollInterval, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// Null when the directory cannot be listed or holds too many files to list. A
    /// directory that does not exist yet is empty: VRChat creates it on its first start.
    /// </summary>
    public static IReadOnlyList<VrchatLogMatcher.Candidate>? ListLogFiles(BridgeOptions options)
    {
        var directory = VrchatLogDirectory.Resolve(options);
        if (directory is null)
        {
            return null;
        }

        try
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            var (candidates, scanCapped) = VrchatLogDirectory.ListCandidates(directory, File.GetCreationTimeUtc);
            return scanCapped ? null : candidates;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task DelayUntilAsync(DateTime utc, CancellationToken cancellationToken)
    {
        var wait = utc - _utcNow();
        if (wait > TimeSpan.Zero)
        {
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One launch batch: its size and, unless the log directory could not be listed, its log watch.</summary>
    internal sealed class Batch(int count, LogWatch? watch)
    {
        public int Count { get; } = count;

        public LogWatch? Watch { get; } = watch;
    }

    /// <summary>One launch batch's files from before its first launch, and its deadline for waiting on new ones.</summary>
    internal sealed class LogWatch(HashSet<string> existing, DateTime deadlineUtc)
    {
        public HashSet<string> Existing { get; } = existing;

        public DateTime DeadlineUtc { get; } = deadlineUtc;

        /// <summary>Set once the deadline passes; the rest of the batch then uses the fixed interval.</summary>
        public bool GaveUp { get; set; }
    }
}

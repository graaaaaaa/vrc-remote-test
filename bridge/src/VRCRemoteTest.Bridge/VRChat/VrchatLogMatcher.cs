using System.Globalization;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Decides which output_log file belongs to one VRChat client (multi-client plan
/// section 13.2). output_log carries no PID, so the only link is time: VRChat names the
/// file after the time it starts logging, local time to the second, and with clients
/// whose logging starts at least a second apart each client gets its own file (spike
/// option B).
///
/// This is a best-effort match that assumes correct clocks, not proof of ownership. A
/// file is used only when its name time, read in the current time zone, is within
/// <see cref="MaxNameToCreationSkew"/> of the NTFS creation time (kept in UTC): a larger
/// gap means the time zone or the clock changed after the file was created, and the
/// name can no longer be trusted. A client is matched only when exactly one file falls in its range
/// and that file falls in no other client's range; anything else is "no match" rather
/// than a pick between candidates.
///
/// On real hardware VRChat named and created its file 0.70-0.84 s after the process
/// start without load, but 11-22 s after it with the CPU at about 100%, and then clients
/// started 3 s apart named their files in the same second and wrote to one file
/// (docs/validation/multi-client-spike.md, PR #2 verification, item 12). No rule here can
/// split such a file, so <see cref="VrchatClientCoordinator"/> launches each client only
/// after the previous client's file has appeared, at <see cref="EarliestNextStartUtc"/>
/// or later. Clients started some other way (start-vrchat-dev.ps1, by hand) get no such
/// spacing: a late file then leaves the client unmatched, and a file delayed beyond the
/// launch interval can still be matched to the next client if an earlier client that
/// owned it has exited.
///
/// On Windows, <see cref="VrchatLogOwners"/> comes first: Restart Manager ties a client to
/// the file that only it has open, late or not. This match is the fallback when that
/// settles nothing, and <see cref="VrchatLogReader"/> still refuses its result when Restart
/// Manager shows that another VRChat has the file open.
/// </summary>
internal static class VrchatLogMatcher
{
    internal const string FileNamePrefix = "output_log_";
    internal const string FileNameSuffix = ".txt";
    private const string FileNameTimeFormat = "yyyy-MM-dd_HH-mm-ss";

    /// <summary>
    /// A time zone change moves the name by at least 15 minutes. Under heavy load VRChat
    /// created a file in the second after the one in its name (named 18-04-10, created
    /// at 18:04:11), so a few seconds of lag are normal and must not drop the file.
    /// </summary>
    internal static readonly TimeSpan MaxNameToCreationSkew = TimeSpan.FromSeconds(10);

    /// <summary>The longest time after a client's start that its file can be named in and still match.</summary>
    internal static readonly TimeSpan MaxRangeLength = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan RangeLead = TimeSpan.FromSeconds(1);

    /// <summary>One output_log file; <paramref name="NameTimeLocal"/> is null when the name has no timestamp.</summary>
    internal sealed record Candidate(string Path, DateTime? NameTimeLocal, DateTime CreationTimeUtc);

    /// <summary>Why <see cref="Evaluate"/> found no file; <see cref="None"/> when it found one.</summary>
    internal enum MatchFailure
    {
        None,

        /// <summary>This client's or another client's creation time is unknown, so the ranges are too.</summary>
        StartTimeUnknown,

        /// <summary>No usable file is named at or after the start of the client's range: VRChat has not created it yet.</summary>
        NoFileYet,

        /// <summary>Files named after the client's start exist, but none in its range (e.g. named after the next client's start).</summary>
        NoFileInRange,

        /// <summary>Several files are in the range, or the one file is also in another client's range.</summary>
        Ambiguous,
    }

    internal readonly record struct MatchResult(string? Path, MatchFailure Failure);

    /// <summary>The local time in "output_log_YYYY-MM-DD_HH-MM-SS.txt", or null.</summary>
    public static DateTime? ParseNameTime(string fileName)
    {
        if (!fileName.StartsWith(FileNamePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(FileNameSuffix, StringComparison.Ordinal))
        {
            return null;
        }

        var stamp = fileName[FileNamePrefix.Length..^FileNameSuffix.Length];
        return DateTime.TryParseExact(
            stamp, FileNameTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? local
            : null;
    }

    /// <summary>Path of the one file that belongs to <paramref name="client"/>, or null.</summary>
    /// <param name="clients">Every same-session client, including those without --watch-worlds.</param>
    public static string? Match(
        VrchatClientInfo client,
        IReadOnlyList<VrchatClientInfo> clients,
        IReadOnlyList<Candidate> candidates,
        TimeZoneInfo timeZone) =>
        Evaluate(client, clients, candidates, timeZone).Path;

    /// <summary><see cref="Match"/>, with the reason when no file matches.</summary>
    public static MatchResult Evaluate(
        VrchatClientInfo client,
        IReadOnlyList<VrchatClientInfo> clients,
        IReadOnlyList<Candidate> candidates,
        TimeZoneInfo timeZone)
    {
        if (client.CreationTimeUtc is null)
        {
            return new MatchResult(null, MatchFailure.StartTimeUnknown);
        }

        // Without another client's creation time, its range is unknown and a file in the
        // first client's range might be its log.
        var others = clients.Where(c => c != client).ToList();
        if (others.Any(c => c.CreationTimeUtc is null))
        {
            return new MatchResult(null, MatchFailure.StartTimeUnknown);
        }

        var usable = candidates
            .Select(c => (c.Path, NameUtc: TrustedNameUtc(c, timeZone)))
            .Where(c => c.NameUtc is not null)
            .Select(c => (c.Path, NameUtc: c.NameUtc!.Value))
            .ToList();

        var creationTimes = clients.Select(c => c.CreationTimeUtc!.Value).ToList();
        var (lower, upper) = RangeOf(client.CreationTimeUtc.Value, creationTimes);
        var inRange = usable.Where(c => c.NameUtc >= lower && c.NameUtc < upper).ToList();
        if (inRange.Count == 0)
        {
            return new MatchResult(
                null, usable.Any(c => c.NameUtc >= lower) ? MatchFailure.NoFileInRange : MatchFailure.NoFileYet);
        }

        if (inRange.Count > 1)
        {
            return new MatchResult(null, MatchFailure.Ambiguous);
        }

        var match = inRange[0];
        foreach (var other in others)
        {
            var (otherLower, otherUpper) = RangeOf(other.CreationTimeUtc!.Value, creationTimes);
            if (match.NameUtc >= otherLower && match.NameUtc < otherUpper)
            {
                return new MatchResult(null, MatchFailure.Ambiguous);
            }
        }

        return new MatchResult(match.Path, MatchFailure.None);
    }

    /// <summary>
    /// From one second before the creation time (truncated to the second, the name's
    /// resolution) up to the next client's creation time (also truncated) or
    /// <see cref="MaxRangeLength"/> after its own, whichever comes first.
    /// </summary>
    private static (DateTime Lower, DateTime Upper) RangeOf(DateTime creationUtc, IReadOnlyList<DateTime> allCreationTimesUtc)
    {
        var lower = TruncateToSecond(creationUtc) - RangeLead;
        var upper = creationUtc + MaxRangeLength;

        var later = allCreationTimesUtc.Where(t => t > creationUtc).ToList();
        if (later.Count > 0)
        {
            var next = TruncateToSecond(later.Min());
            if (next < upper)
            {
                upper = next;
            }
        }

        return (lower, upper);
    }

    /// <summary>
    /// Null for a name without a timestamp, a local time that daylight saving time made
    /// ambiguous or skipped, or a name that disagrees with the file's creation time.
    /// </summary>
    public static DateTime? TrustedNameUtc(Candidate candidate, TimeZoneInfo timeZone)
    {
        if (candidate.NameTimeLocal is not { } local)
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local) || timeZone.IsAmbiguousTime(local))
        {
            return null;
        }

        var nameUtc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return (nameUtc - candidate.CreationTimeUtc).Duration() > MaxNameToCreationSkew ? null : nameUtc;
    }

    /// <summary>
    /// The earliest creation time of a client whose range begins after
    /// <paramref name="nameUtc"/>, so a file named then stays in the earlier client's
    /// range only (that range ends at this client's creation time, truncated).
    /// </summary>
    public static DateTime EarliestNextStartUtc(DateTime nameUtc) =>
        TruncateToSecond(nameUtc) + RangeLead + TimeSpan.FromSeconds(1);

    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}

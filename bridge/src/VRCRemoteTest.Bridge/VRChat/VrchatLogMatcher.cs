using System.Globalization;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Decides which output_log file belongs to one VRChat client (multi-client plan
/// section 13.2). output_log carries no PID, so the only link is time: VRChat names the
/// file after its local start time, to the second, and with clients started at least a
/// second apart each client gets its own file (spike option B).
///
/// This is a best-effort match that assumes correct clocks, not proof of ownership. A
/// file is used only when its name time, read in the current time zone, is within
/// <see cref="MaxNameToCreationSkew"/> of the NTFS creation time (kept in UTC): a larger
/// gap means the time zone or the clock changed after the file was created, and the
/// name can no longer be trusted. A client is matched only when exactly one file falls
/// in its range and that file falls in no other client's range; anything else is "no
/// match", never a guess, so another client's log is never shown as the first one's.
/// </summary>
internal static class VrchatLogMatcher
{
    internal const string FileNamePrefix = "output_log_";
    internal const string FileNameSuffix = ".txt";
    private const string FileNameTimeFormat = "yyyy-MM-dd_HH-mm-ss";

    internal static readonly TimeSpan MaxNameToCreationSkew = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RangeLead = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRangeLength = TimeSpan.FromSeconds(60);

    /// <summary>One output_log file; <paramref name="NameTimeLocal"/> is null when the name has no timestamp.</summary>
    internal sealed record Candidate(string Path, DateTime? NameTimeLocal, DateTime CreationTimeUtc);

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
        TimeZoneInfo timeZone)
    {
        if (client.CreationTimeUtc is null)
        {
            return null;
        }

        // Without another client's creation time, its range is unknown and a file in the
        // first client's range might be its log.
        var others = clients.Where(c => c != client).ToList();
        if (others.Any(c => c.CreationTimeUtc is null))
        {
            return null;
        }

        var usable = candidates
            .Select(c => (c.Path, NameUtc: ToTrustedUtc(c, timeZone)))
            .Where(c => c.NameUtc is not null)
            .Select(c => (c.Path, NameUtc: c.NameUtc!.Value))
            .ToList();

        var creationTimes = clients.Select(c => c.CreationTimeUtc!.Value).ToList();
        var (lower, upper) = RangeOf(client.CreationTimeUtc.Value, creationTimes);
        var inRange = usable.Where(c => c.NameUtc >= lower && c.NameUtc < upper).ToList();
        if (inRange.Count != 1)
        {
            return null;
        }

        var match = inRange[0];
        foreach (var other in others)
        {
            var (otherLower, otherUpper) = RangeOf(other.CreationTimeUtc!.Value, creationTimes);
            if (match.NameUtc >= otherLower && match.NameUtc < otherUpper)
            {
                return null;
            }
        }

        return match.Path;
    }

    /// <summary>
    /// From one second before the creation time (truncated to the second, the name's
    /// resolution) up to the next client's creation time (also truncated) or 60 seconds
    /// after its own, whichever comes first.
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
    private static DateTime? ToTrustedUtc(Candidate candidate, TimeZoneInfo timeZone)
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

    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
}

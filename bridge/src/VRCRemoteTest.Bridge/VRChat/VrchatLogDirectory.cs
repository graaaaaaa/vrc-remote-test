using VRCRemoteTest.Bridge.Configuration;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// VRChat's output_log directory, shared by <see cref="VrchatLogReader"/> and the launch
/// pacing of <see cref="VrchatClientCoordinator"/>.
/// </summary>
internal static class VrchatLogDirectory
{
    internal const string OutputLogPattern = VrchatLogMatcher.FileNamePrefix + "*" + VrchatLogMatcher.FileNameSuffix;

    /// <summary>Directory enumeration cap (Codex plan review Phase 5, Round 2, confidence 0.87).</summary>
    internal const int MaxDirectoryScan = 512;

    /// <summary>
    /// Derived from VrchatWorldsDirectory's parent (...\VRChat\VRChat\Worlds ->
    /// ...\VRChat\VRChat, where output_log_*.txt lives), matching the derivation
    /// approach considered (and left unimplemented) in Phase 4.1. No new
    /// BridgeOptions property is added; failure to derive degrades gracefully.
    /// </summary>
    public static string? Resolve(BridgeOptions options) =>
        string.IsNullOrWhiteSpace(options.VrchatWorldsDirectory)
            ? null
            : Directory.GetParent(options.VrchatWorldsDirectory)?.FullName;

    /// <summary>
    /// Bounded scan: more than <see cref="MaxDirectoryScan"/> files reports "capped"
    /// instead of a partial list. Names without a timestamp are skipped, and so are files
    /// whose creation time cannot be read now (the next scan looks again).
    /// </summary>
    public static (List<VrchatLogMatcher.Candidate> Candidates, bool ScanCapped) ListCandidates(
        string logDirectory, Func<string, DateTime> creationTimeUtc)
    {
        var candidates = new List<VrchatLogMatcher.Candidate>();
        var scanned = 0;

        foreach (var path in Directory.EnumerateFiles(logDirectory, OutputLogPattern))
        {
            if (scanned == MaxDirectoryScan)
            {
                return ([], true);
            }

            scanned++;

            var nameTime = VrchatLogMatcher.ParseNameTime(Path.GetFileName(path));
            if (nameTime is null)
            {
                continue;
            }

            try
            {
                candidates.Add(new VrchatLogMatcher.Candidate(path, nameTime, creationTimeUtc(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable now; the next scan looks again.
            }
        }

        return (candidates, false);
    }
}

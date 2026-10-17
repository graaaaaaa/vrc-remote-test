namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// Decides which output_log file belongs to one VRChat client from the processes that
/// have each file open, as the Windows Restart Manager reports them
/// (<see cref="RestartManagerFileOwners"/>). Each VRChat keeps its own output_log open
/// while it runs, so this links a client to its file by PID and creation time, where
/// <see cref="VrchatLogMatcher"/> can only compare times. On real hardware
/// (docs/validation/multi-client-spike.md, PR #3 verification, G), RmGetList returned
/// exactly one PID for each client's own file, none for a closed session's file, and
/// several for a file that several clients wrote to. With the CPU at about 100%, the
/// first client's file appeared 60 s to about 2 min after its start, too late for the
/// name match, but that client was still the only one holding it.
///
/// The rules keep the matcher's guarantee of never showing another client's log as this
/// client's:
/// - A file is this client's when this client (same PID and creation time) is the only
///   VRChat process that has it open. Processes that are not VRChat (the Bridge, an
///   editor, antivirus software) do not count.
/// - A file that another VRChat process has open is never this client's log: either it
///   is the other client's, or it is mixed (<see cref="Decision.Refused"/>).
/// - Anything that cannot be told (this client's creation time is unknown, an owner
///   cannot be identified) settles nothing, and neither does this client having no file
///   open, which is also what Restart Manager reports if VRChat ever stops keeping its log
///   open. The caller then falls back to the name match, still refusing what is refused here.
///
/// Pure, for tests: the owners come from <see cref="RestartManagerFileOwners"/>, which
/// only runs on Windows.
/// </summary>
internal static class VrchatLogOwners
{
    /// <summary>
    /// Only files created at most this long before the client's start are asked about. A
    /// client opens the file named after the second its logging starts, and VRChat created
    /// every file observed at or after the time in its name (the name is never later than
    /// the creation), so a client's file is created less than a second before the client
    /// starts at the earliest. The rest is a margin for clock adjustments; it also covers
    /// every file <see cref="VrchatLogMatcher"/> can match (named at most 1 s before the
    /// start and created at most <see cref="VrchatLogMatcher.MaxNameToCreationSkew"/>
    /// before its name time).
    /// </summary>
    internal static readonly TimeSpan QueryLead = TimeSpan.FromMinutes(1);

    /// <summary>
    /// One process that has a file open. <paramref name="StartTimeUtc"/> is null when
    /// Restart Manager could not read it. <paramref name="ProcessName"/> (without ".exe")
    /// is null when the process could not be looked up, for example because it exited or
    /// its PID now names a process with another start time.
    /// </summary>
    internal sealed record Owner(int ProcessId, DateTime? StartTimeUtc, string? ProcessName);

    /// <summary>The processes that had one file open, from one Restart Manager session.</summary>
    internal sealed record FileOwners(string Path, IReadOnlyList<Owner> Owners);

    internal enum OwnerKind
    {
        /// <summary>The client being matched: its PID and creation time.</summary>
        Client,

        /// <summary>Another VRChat process: another client in the snapshot, or a process named VRChat.</summary>
        OtherVrchat,

        /// <summary>A process with another name, such as the Bridge, an editor or antivirus software.</summary>
        NotVrchat,

        /// <summary>The client's PID with another or no start time, or a process whose name could not be read.</summary>
        Unknown,
    }

    internal enum Verdict
    {
        /// <summary>The client is the only VRChat process that has <see cref="Decision.Path"/> open.</summary>
        Owned,

        /// <summary>The client has <see cref="Decision.Path"/> open together with another VRChat process.</summary>
        Shared,

        /// <summary>The client has more than one of the files open.</summary>
        Several,

        /// <summary>The client has none of the files open.</summary>
        NotOwned,

        /// <summary>Whether the client has a file open, or who else has it open, cannot be told.</summary>
        Inconclusive,
    }

    /// <param name="Path">The client's file, for <see cref="Verdict.Owned"/> and <see cref="Verdict.Shared"/>.</param>
    /// <param name="Refused">Files that a VRChat process other than the client has open, whatever the verdict.</param>
    /// <param name="Mixed">Files that two or more VRChat processes have open, the client or not.</param>
    /// <param name="AnyVrchatOwner">Whether any VRChat process has any of the files open.</param>
    internal sealed record Decision(
        Verdict Verdict,
        string? Path,
        IReadOnlySet<string> Refused,
        IReadOnlySet<string> Mixed,
        bool AnyVrchatOwner);

    /// <param name="clients">Every same-session client of the snapshot, the matched one included.</param>
    public static Decision Decide(
        VrchatClientInfo client, IReadOnlyList<VrchatClientInfo> clients, IReadOnlyList<FileOwners> files)
    {
        var refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mixed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clientFiles = new List<(string Path, bool WithOther, bool WithUnknown)>();
        var anyVrchatOwner = false;
        var clientUnconfirmed = false;

        foreach (var file in files)
        {
            var owners = file.Owners
                .DistinctBy(o => o.ProcessId)
                .Select(o => (Owner: o, Kind: Classify(o, client, clients)))
                .ToList();
            var withClient = owners.Any(o => o.Kind == OwnerKind.Client);
            var others = owners.Count(o => o.Kind == OwnerKind.OtherVrchat);

            if (others > 0)
            {
                refused.Add(file.Path);
            }

            if (others + (withClient ? 1 : 0) >= 2)
            {
                mixed.Add(file.Path);
            }

            anyVrchatOwner |= withClient || others > 0;

            if (withClient)
            {
                clientFiles.Add((file.Path, others > 0, owners.Any(o => o.Kind == OwnerKind.Unknown)));
            }
            else if (owners.Any(o => o.Owner.ProcessId == client.ProcessId))
            {
                // The client's PID without a matching start time: maybe the client, maybe not.
                clientUnconfirmed = true;
            }
        }

        if (client.CreationTimeUtc is null || clientUnconfirmed)
        {
            return new Decision(Verdict.Inconclusive, null, refused, mixed, anyVrchatOwner);
        }

        (Verdict Verdict, string? Path) result = clientFiles switch
        {
            [] => (Verdict.NotOwned, null),
            [{ WithOther: true } only] => (Verdict.Shared, only.Path),
            [{ WithUnknown: true }] => (Verdict.Inconclusive, null),
            [var only] => (Verdict.Owned, only.Path),
            _ => (Verdict.Several, null),
        };

        return new Decision(result.Verdict, result.Path, refused, mixed, anyVrchatOwner);
    }

    internal static OwnerKind Classify(Owner owner, VrchatClientInfo client, IReadOnlyList<VrchatClientInfo> clients)
    {
        if (owner.ProcessId == client.ProcessId)
        {
            return client.CreationTimeUtc is { } created
                && owner.StartTimeUtc is { } started
                && VrchatProcessMonitor.IsSameCreationTime(created, started)
                ? OwnerKind.Client
                : OwnerKind.Unknown;
        }

        // Another PID is another process. A PID the snapshot saw as a VRChat client counts as
        // VRChat even if it was reused since: that can only refuse a file, never show one.
        if (clients.Any(c => c.ProcessId == owner.ProcessId))
        {
            return OwnerKind.OtherVrchat;
        }

        if (owner.ProcessName is not { } name)
        {
            return OwnerKind.Unknown;
        }

        return string.Equals(name, VrchatProcessMonitor.ProcessName, StringComparison.OrdinalIgnoreCase)
            ? OwnerKind.OtherVrchat
            : OwnerKind.NotVrchat;
    }
}

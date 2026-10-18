using System.Text.RegularExpressions;

namespace VRCRemoteTest.Bridge.VRChat;

internal enum VrchatClientLogState
{
    /// <summary>No Joining yet.</summary>
    Starting,

    /// <summary>Joining was seen and the join has not finished, or the client left the room (OnLeftRoom, Rejoining).</summary>
    Joining,

    /// <summary>"Finished entering world." followed the latest Joining.</summary>
    Joined,

    /// <summary>The latest Joining named an instance in a form the Bridge does not know.</summary>
    Unknown,
}

/// <summary>One event from a client's log, for vrchat-clients.json's lastEvents.</summary>
internal sealed record VrchatClientLogEvent(string Kind, long Seq, DateTime LocalTime, string? Player, string? Instance);

/// <summary>
/// Follows one client's log line by line and keeps what vrchat-clients.json reports about it
/// (client-observation plan F1). The rules come from the lines seen on real hardware
/// (docs/validation/multi-client-spike.md, S5a and S5b):
///
/// - A join is "[Behaviour] Joining local:&lt;id&gt;" (no ":1"), then "Successfully joined room",
///   "I am MASTER" or "I am *NOT* MASTER", "[Behaviour] Finished entering world.", and
///   "OnPlayerJoined" for every player, this client included. A new Joining replaces the
///   previous attempt. Rejoining ("Rejoining local world: &lt;id&gt;:1") and leaving the room
///   (OnLeftRoom) go back to joining until the next Joining.
/// - "Finished entering world." is also written when the client lands in the Error World
///   (local:error_&lt;digits&gt;), so it is joined but inErrorWorld.
/// - Only Debug lines whose whole message matches an event's grammar count: no lone LF in
///   the line, no truncated line, no partial match (Joining never matches "Joining or
///   Creating Room", and "Finished entering world. x" is not the end of a join). The one
///   exception is VRChat's own continuation of its master lines, "I am MASTER" and
///   "I am *NOT* MASTER" followed by a lone LF and "Actor Nr: &lt;n&gt;" and nothing else. A world can
///   still forge a whole line after a CRLF, and its Start runs before the real "Finished
///   entering world." (S5b B'), so the state is an observation that assumes the world under
///   test does not forge it; it never authorizes anything.
/// - Players come from OnPlayerJoined / OnPlayerLeft "&lt;name&gt; (usr_…)" after the latest
///   Joining, the name being everything before the last " (usr_". Several clients on one
///   account have the same name, so the list keeps duplicates.
/// - Master: "I am MASTER" / "I am *NOT* MASTER"; after OnMasterClientSwitched VRChat does not
///   say who became master (S5a A2), so it is unknown unless this client is alone, and it
///   becomes true once this client is left alone.
/// </summary>
internal sealed partial class VrchatClientStateTracker
{
    internal const int MaxEvents = 20;

    /// <summary>
    /// A world can write any number of OnPlayerJoined lines. Past this many players the list
    /// and the master flag are given up until the next Joining rather than kept growing.
    /// </summary>
    internal const int MaxPlayers = 256;

    /// <summary>Longer names and IDs are not VRChat's, so such a line is not an event.</summary>
    internal const int MaxNameLength = 128;
    internal const int MaxUserIdLength = 64;

    private const string Behaviour = "[Behaviour] ";
    private const string LocalPrefix = "local:";
    private const string ErrorWorldPrefix = "local:error_";

    private readonly List<(string Name, string UserId)> _players = [];
    private readonly LinkedList<VrchatClientLogEvent> _events = new();

    /// <summary>A Joining was seen and no Rejoining or OnLeftRoom since: players and master belong to it.</summary>
    private bool _inAttempt;

    /// <summary>The latest attempt has not finished yet.</summary>
    private bool _awaitingFinish;

    /// <summary>More than <see cref="MaxPlayers"/> joined in this attempt.</summary>
    private bool _playersOverflowed;

    public VrchatClientLogState State { get; private set; } = VrchatClientLogState.Starting;

    /// <summary>local:&lt;id&gt; without the trailing ":&lt;n&gt;".</summary>
    public string? RequestedInstance { get; private set; }

    public string? Instance { get; private set; }

    public bool? InErrorWorld { get; private set; }

    public bool? IsMaster
    {
        get => _playersOverflowed ? null : _isMaster;
        private set => _isMaster = value;
    }

    private bool? _isMaster;

    public long? JoinAttemptSeq { get; private set; }

    public long? JoinedSeq { get; private set; }

    public long ObservedThroughSeq { get; private set; }

    /// <summary>True once "User Authenticated: &lt;name&gt; (usr_…)" was seen; never false, since its absence proves nothing.</summary>
    public bool Authenticated { get; private set; }

    public bool SawDebug { get; private set; }

    public bool SawWarning { get; private set; }

    public bool SawError { get; private set; }

    /// <summary>The players after the latest Joining, or null between attempts and once there were too many.</summary>
    public IReadOnlyList<string>? Players =>
        _inAttempt && !_playersOverflowed ? _players.Select(p => p.Name).ToList() : null;

    public IReadOnlyCollection<VrchatClientLogEvent> Events => _events;

    public void OnLine(VrchatLogPhysicalLine line)
    {
        ObservedThroughSeq = line.Seq;
        if (line.Truncated || !VrchatLogLine.TryParseHeader(line.Text, out var header))
        {
            return;
        }

        switch (header.Level)
        {
            case VrchatLogLevel.Debug:
                SawDebug = true;
                break;
            case VrchatLogLevel.Warning:
                SawWarning = true;
                break;
            case VrchatLogLevel.Error:
                SawError = true;
                break;
        }

        var body = VrchatLogLine.Body(line.Text);
        if (header.Level == VrchatLogLevel.Error && body.StartsWith(UdonExceptionPrefix, StringComparison.Ordinal))
        {
            // Followed by a lone LF and the exception, which are not reported.
            AddEvent("udonException", line.Seq, header.LocalTime);
            return;
        }

        if (header.Level != VrchatLogLevel.Debug)
        {
            return;
        }

        if (body.Contains('\n'))
        {
            // VRChat writes its master lines with one continuation of its own (P1 verification
            // on Windows: all 341 since 2026-10-17); no other event line has one.
            if (TrySplitMasterLine(body) is not { } master)
            {
                return;
            }

            body = master;
        }

        OnDebugMessage(body, line.Seq, header.LocalTime);
    }

    /// <summary>
    /// "[Behaviour] I am MASTER" or "[Behaviour] I am *NOT* MASTER", then a lone LF and exactly
    /// "Actor Nr: &lt;digits&gt;": the master line without its continuation, or null.
    /// </summary>
    internal static string? TrySplitMasterLine(string body)
    {
        var newline = body.IndexOf('\n');
        var first = body[..newline];
        var rest = body.AsSpan(newline + 1);
        if (first is not (Behaviour + "I am MASTER" or Behaviour + "I am *NOT* MASTER")
            || !rest.StartsWith(ActorNumberPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var number = rest[ActorNumberPrefix.Length..];
        return number.Length is > 0 and <= 9 && number.IndexOfAnyExceptInRange('0', '9') < 0 ? first : null;
    }

    private const string ActorNumberPrefix = "Actor Nr: ";

    private const string UdonExceptionPrefix = "[UdonBehaviour] An exception occurred during Udon execution";

    private void OnDebugMessage(string body, long seq, DateTime time)
    {
        if (body.StartsWith(UserAuthenticatedPrefix, StringComparison.Ordinal)
            && TrySplitUser(body[UserAuthenticatedPrefix.Length..], out _, out _))
        {
            Authenticated = true;
            AddEvent("UserAuthenticated", seq, time);
            return;
        }

        if (!body.StartsWith(Behaviour, StringComparison.Ordinal))
        {
            return;
        }

        var message = body[Behaviour.Length..];
        switch (message)
        {
            case "Finished entering world.":
                OnFinishedEnteringWorld(seq, time);
                return;
            case "OnLeftRoom":
                LeaveAttempt();
                AddEvent("OnLeftRoom", seq, time);
                return;
            case "OnPlayerLeftRoom":
                AddEvent("OnPlayerLeftRoom", seq, time);
                return;
            case "I am MASTER":
                if (_inAttempt)
                {
                    IsMaster = true;
                }

                return;
            case "I am *NOT* MASTER":
                if (_inAttempt)
                {
                    IsMaster = false;
                }

                return;
            case "OnMasterClientSwitched":
                if (_inAttempt)
                {
                    IsMaster = _players.Count == 1 ? true : null;
                }

                AddEvent("OnMasterClientSwitched", seq, time);
                return;
        }

        if (message.StartsWith(JoiningPrefix, StringComparison.Ordinal))
        {
            var value = message[JoiningPrefix.Length..];
            if (value.Length > 0 && !value.Contains(' '))
            {
                OnJoining(value, seq, time);
            }

            return;
        }

        if (message.StartsWith(RejoiningPrefix, StringComparison.Ordinal))
        {
            var value = message[RejoiningPrefix.Length..];
            LeaveAttempt();
            RequestedInstance = NormalizeInstance(value);
            State = RequestedInstance is null ? VrchatClientLogState.Unknown : VrchatClientLogState.Joining;
            AddEvent("Rejoining", seq, time, instance: RequestedInstance);
            return;
        }

        var joined = message.StartsWith(PlayerJoinedPrefix, StringComparison.Ordinal);
        if ((joined || message.StartsWith(PlayerLeftPrefix, StringComparison.Ordinal))
            && TrySplitUser(message[(joined ? PlayerJoinedPrefix : PlayerLeftPrefix).Length..], out var name, out var userId))
        {
            if (joined)
            {
                if (_inAttempt && !_playersOverflowed)
                {
                    if (_players.Count == MaxPlayers)
                    {
                        _playersOverflowed = true;
                        _players.Clear();
                    }
                    else
                    {
                        _players.Add((name, userId));
                    }
                }

                AddEvent("OnPlayerJoined", seq, time, player: name);
            }
            else
            {
                if (_inAttempt && !_playersOverflowed)
                {
                    var index = _players.FindIndex(p => p.Name == name && p.UserId == userId);
                    if (index >= 0)
                    {
                        _players.RemoveAt(index);
                    }

                    if (_players.Count == 1 && State == VrchatClientLogState.Joined && IsMaster is null)
                    {
                        // Alone in the instance, this client is its master.
                        IsMaster = true;
                    }
                }

                AddEvent("OnPlayerLeft", seq, time, player: name);
            }
        }
    }

    private const string JoiningPrefix = "Joining ";
    private const string RejoiningPrefix = "Rejoining local world: ";
    private const string PlayerJoinedPrefix = "OnPlayerJoined ";
    private const string PlayerLeftPrefix = "OnPlayerLeft ";
    private const string UserAuthenticatedPrefix = "User Authenticated: ";
    private const string UserIdOpen = " (usr_";

    private void OnJoining(string value, long seq, DateTime time)
    {
        _players.Clear();
        _playersOverflowed = false;
        _inAttempt = true;
        IsMaster = null;
        Instance = null;
        InErrorWorld = null;
        JoinedSeq = null;
        JoinAttemptSeq = seq;

        // Joining carries the ID without ":1"; any other form is not one the rules know.
        RequestedInstance = IsLocalInstance(value) ? value : null;
        State = RequestedInstance is null ? VrchatClientLogState.Unknown : VrchatClientLogState.Joining;
        _awaitingFinish = RequestedInstance is not null;
        AddEvent("Joining", seq, time, instance: RequestedInstance);
    }

    private void OnFinishedEnteringWorld(long seq, DateTime time)
    {
        if (!_awaitingFinish || State != VrchatClientLogState.Joining || RequestedInstance is null)
        {
            return;
        }

        _awaitingFinish = false;
        State = VrchatClientLogState.Joined;
        Instance = RequestedInstance;
        InErrorWorld = Instance.StartsWith(ErrorWorldPrefix, StringComparison.Ordinal);
        JoinedSeq = seq;
        AddEvent("FinishedEnteringWorld", seq, time, instance: Instance);
    }

    /// <summary>Rejoining and OnLeftRoom: back to joining until the next Joining, which starts the next attempt.</summary>
    private void LeaveAttempt()
    {
        _inAttempt = false;
        _playersOverflowed = false;
        _awaitingFinish = false;
        _players.Clear();
        IsMaster = null;
        Instance = null;
        InErrorWorld = null;
        JoinedSeq = null;
        if (State is VrchatClientLogState.Joined)
        {
            State = VrchatClientLogState.Joining;
        }
    }

    private void AddEvent(string kind, long seq, DateTime time, string? player = null, string? instance = null)
    {
        _events.AddLast(new VrchatClientLogEvent(kind, seq, time, player, instance));
        if (_events.Count > MaxEvents)
        {
            _events.RemoveFirst();
        }
    }

    /// <summary>
    /// local:&lt;64 hex digits&gt; or local:error_&lt;digits&gt;, the forms Joining carries; values
    /// from Destination set and Rejoining carry a trailing ":&lt;n&gt;", which is dropped first.
    /// Null for anything else.
    /// </summary>
    internal static string? NormalizeInstance(string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon > LocalPrefix.Length - 1 && colon < value.Length - 1
            && value.AsSpan(colon + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            value = value[..colon];
        }

        return IsLocalInstance(value) ? value : null;
    }

    internal static bool IsLocalInstance(string value) => LocalInstancePattern().IsMatch(value);

    [GeneratedRegex(@"\Alocal:(?:[0-9a-f]{64}|error_[0-9]{1,32})\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalInstancePattern();

    /// <summary>
    /// "&lt;name&gt; (usr_&lt;id&gt;)": the name runs to the last " (usr_", so a name with
    /// parentheses of its own stays whole. Searched from the end without a regex, so a long
    /// line costs one pass.
    /// </summary>
    internal static bool TrySplitUser(string text, out string name, out string userId)
    {
        name = string.Empty;
        userId = string.Empty;
        var open = text.LastIndexOf(UserIdOpen, StringComparison.Ordinal);
        if (open <= 0 || text[^1] != ')')
        {
            return false;
        }

        var id = text.AsSpan(open + 2, text.Length - open - 3);
        if (id.Length <= "usr_".Length || id.Length > MaxUserIdLength || open > MaxNameLength
            || id["usr_".Length..].ContainsAnyExcept(UserIdCharacters))
        {
            return false;
        }

        name = text[..open];
        userId = id.ToString();
        return true;
    }

    private static readonly System.Buffers.SearchValues<char> UserIdCharacters =
        System.Buffers.SearchValues.Create("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ-");
}

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.Protocol;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Client-observation plan F1 and F3 end to end: VrchatLogReader resolves real files for a
/// fake process monitor, and the observer reads them as VrchatLogService would every poll.
/// </summary>
public class VrchatClientObserverTests : IDisposable
{
    private const string Id = "dab8e405667a4de729f91623db09e31f57071efe70ea4b1ee764bd3563dd5383";
    private static readonly DateTime T0 = new(2026, 10, 17, 6, 0, 0, 100, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly string _logDir;
    private readonly string _worldsDir;
    private readonly string _stateDir;
    private readonly FakeVrchatProcessMonitor _monitor = new();
    private DateTimeOffset _now = new(T0.AddSeconds(10));

    private VrchatClientRoster _roster;
    private VrchatLogReader _reader;
    private VrchatClientObserver _observer;

    public VrchatClientObserverTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-observer-").FullName;
        _logDir = Path.Combine(_tempDir, "VRChat", "VRChat");
        _worldsDir = Path.Combine(_logDir, "Worlds");
        _stateDir = Path.Combine(_tempDir, "state");
        Directory.CreateDirectory(_worldsDir);
        (_roster, _reader, _observer) = StartBridge();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private BridgeOptions Options => new() { VrchatWorldsDirectory = _worldsDir, LogMarkerPrefixes = ["[SPK003]"] };

    /// <summary>A Bridge (re)start: a new reader and observer over the same state directory.</summary>
    private (VrchatClientRoster, VrchatLogReader, VrchatClientObserver) StartBridge()
    {
        var roster = new VrchatClientRoster(Path.Combine(_stateDir, VrchatClientRoster.FileName));
        var reader = new VrchatLogReader(
            Microsoft.Extensions.Options.Options.Create(Options),
            _monitor,
            NullLogger<VrchatLogReader>.Instance,
            CreationTimeFromName,
            queryOwners: null,
            managedClients: null,
            roster);
        var observer = new VrchatClientObserver(Options, roster, null, CreationTimeFromName, () => _now);
        return (roster, reader, observer);
    }

    private void RestartBridge() => (_roster, _reader, _observer) = StartBridge();

    /// <summary>One poll, acknowledging what it offered as VrchatLogService does once the writes succeed.</summary>
    private VrchatObservation Poll(bool published = true)
    {
        var observation = _observer.Observe(_reader.Read(), CancellationToken.None);
        if (published)
        {
            foreach (var previous in observation.PreviousMarkers)
            {
                _observer.AcknowledgePrevious(previous);
            }

            foreach (var markers in observation.MarkerFiles)
            {
                _observer.AcknowledgeMarkers(markers.Slot, markers.Key);
            }
        }

        return observation;
    }

    private static string MarkerFile(VrchatObservation observation, int slot) =>
        observation.MarkerFiles.Single(m => m.Slot == slot).Content;

    private static DateTime CreationTimeFromName(string path) =>
        VrchatLogMatcher.ParseNameTime(Path.GetFileName(path)) is { } local
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local)
            : DateTime.MinValue;

    private string LogPath(DateTime startedUtc) =>
        Path.Combine(_logDir, $"output_log_{startedUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt");

    private static string Lines(params string[] messages) =>
        string.Concat(messages.Select(m => "2026.10.18 13:34:37 Debug      -  " + m + "\r\n"));

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static string JoinLines(string name, string user) => Lines(
        $"User Authenticated: {name} ({user})",
        $"[Behaviour] Joining local:{Id}",
        "[Behaviour] I am MASTER",
        "[Behaviour] Finished entering world.",
        $"[Behaviour] OnPlayerJoined {name} ({user})");

    private static VrchatClientEntry Entry(VrchatObservation observation, int slot) =>
        observation.Clients.Clients.Single(c => c.Slot == slot);

    [Fact]
    public void Each_client_shows_its_account_as_a_digest_and_the_id_is_never_published()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000"));
        File.WriteAllText(LogPath(T0.AddSeconds(3)), JoinLines("Bob", "usr_bbbbbbbb-0000"));
        var digest = AccountDigest.ForKey(new byte[32]);
        var roster = new VrchatClientRoster(Path.Combine(_stateDir, VrchatClientRoster.FileName));
        var reader = new VrchatLogReader(
            Microsoft.Extensions.Options.Options.Create(Options), _monitor, NullLogger<VrchatLogReader>.Instance,
            CreationTimeFromName, queryOwners: null, managedClients: null, roster);
        var observer = new VrchatClientObserver(
            Options, roster, null, CreationTimeFromName, () => _now, accountDigest: digest.Of);

        var observation = observer.Observe(reader.Read(), CancellationToken.None);

        var alice = Entry(observation, 0);
        alice.LoginState.Should().Be("authenticated");
        alice.Account!.DisplayName.Should().Be("Alice");
        alice.Account.IdDigest.Should().Be(digest.Of("usr_aaaaaaaa-0000"));
        Entry(observation, 1).Account!.IdDigest.Should().NotBe(alice.Account.IdDigest);
        var published = JsonSerializer.Serialize(observation.Clients);
        published.Should().NotContain("usr_aaaaaaaa").And.NotContain("usr_bbbbbbbb");
    }

    private static (JsonElement Header, string[] Records) Markers(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return (JsonDocument.Parse(lines[0][2..]).RootElement.Clone(), lines[1..]);
    }

    [Fact]
    public void Two_clients_are_followed_in_their_own_slots()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000") + Lines("[SPK003] PLAYER_JOINED id=1"));
        File.WriteAllText(LogPath(T0.AddSeconds(3)), JoinLines("Bob", "usr_bbbbbbbb-0000") + Lines("[SPK003] PLAYER_JOINED id=2"));

        var observation = Poll();

        foreach (var (slot, name, id) in new[] { (0, "Alice", "id=1"), (1, "Bob", "id=2") })
        {
            var entry = Entry(observation, slot);
            entry.State.Should().Be("joined");
            entry.Instance.Should().Be($"local:{Id}");
            entry.PlayersInInstance.Should().Equal(name);
            entry.IsMaster.Should().BeTrue();
            entry.LoggingLevel.Should().Be("full");
            entry.Authenticated.Should().BeTrue();
            entry.CatchingUp.Should().BeFalse();
            entry.Running.Should().BeTrue();
            entry.ObservedThroughSeq.Should().Be(6);
            var (header, records) = Markers(MarkerFile(observation, slot));
            header.GetProperty("slot").GetInt32().Should().Be(slot);
            header.GetProperty("clientGeneration").GetInt64().Should().Be(entry.ClientGeneration);
            header.GetProperty("observationEpoch").GetInt64().Should().Be(entry.ObservationEpoch!.Value);
            records.Should().ContainSingle().Which.Should().StartWith("6\t").And.EndWith(id);
        }

        observation.NewGenerations.Should().BeEquivalentTo([0, 1]);
        observation.Clients.Clients.Select(c => c.ObservationEpoch).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Appended_lines_add_to_the_markers_without_a_new_epoch()
    {
        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000") + Lines("[SPK003] PLAYER_LEFT id=1"));
        var first = Poll();

        Append(path, Lines("[Behaviour] OnLeftRoom", "[SPK003] PLAYER_RESTORED id=1"));
        var second = Poll();
        var third = Poll();

        Entry(second, 0).ObservationEpoch.Should().Be(Entry(first, 0).ObservationEpoch);
        Markers(MarkerFile(second, 0)).Records.Should().HaveCount(2);
        Entry(second, 0).State.Should().Be("joining", "OnLeftRoom goes back to joining until the next Joining");
        Entry(second, 0).Instance.Should().BeNull();
        third.MarkerFiles.Should().BeEmpty("nothing changed, so nothing is rewritten");
        second.NewGenerations.Should().BeEmpty();
    }

    [Fact]
    public void A_Bridge_restart_reads_again_under_a_new_epoch_with_the_same_slots_and_seq()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000"));
        File.WriteAllText(LogPath(T0.AddSeconds(3)), JoinLines("Bob", "usr_bbbbbbbb-0000") + Lines("[SPK003] x"));
        var before = Poll();

        RestartBridge();
        var after = Poll();

        foreach (var slot in new[] { 0, 1 })
        {
            Entry(after, slot).ProcessId.Should().Be(Entry(before, slot).ProcessId);
            Entry(after, slot).ClientGeneration.Should().Be(Entry(before, slot).ClientGeneration);
            Entry(after, slot).ObservationEpoch.Should().BeGreaterThan(Entry(before, 1).ObservationEpoch!.Value);
            Entry(after, slot).JoinedSeq.Should().Be(Entry(before, slot).JoinedSeq);
        }

        after.NewGenerations.Should().BeEmpty("the same clients keep their markers; nothing moves to .prev");
        Markers(MarkerFile(after, 1)).Records.Should().Equal(Markers(MarkerFile(before, 1)).Records);
    }

    [Fact]
    public void A_Reload_client_left_alone_stays_in_slot_1_after_a_Bridge_restart()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000"));
        File.WriteAllText(LogPath(T0.AddSeconds(3)), JoinLines("Bob", "usr_bbbbbbbb-0000"));
        Poll();

        _monitor.Clients.RemoveAt(0);
        RestartBridge();
        var after = Poll();

        Entry(after, 1).ProcessId.Should().Be(200);
        Entry(after, 1).State.Should().Be("joined");
        Entry(after, 0).Running.Should().BeFalse();
        Entry(after, 0).State.Should().Be("left");
        after.MarkerFiles.Should().NotContain(m => m.Slot == 0, "the exited client's markers file stays as it was");
    }

    [Fact]
    public void A_shrunk_file_is_read_again_and_replaces_the_markers()
    {
        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000") + Lines("[SPK003] one", "[SPK003] two"));
        var before = Poll();

        File.WriteAllText(path, Lines("[SPK003] after"));
        var after = Poll();

        Entry(after, 0).ObservationEpoch.Should().BeGreaterThan(Entry(before, 0).ObservationEpoch!.Value);
        Markers(MarkerFile(after, 0)).Records.Should().Equal("1\t2026.10.18 13:34:37 Debug      -  [SPK003] after");
        Entry(after, 0).State.Should().Be("starting");
    }

    [Fact]
    public void Only_a_new_client_in_the_slot_moves_the_markers_to_prev()
    {
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000"));
        Poll().NewGenerations.Should().BeEquivalentTo([0]);
        Poll().NewGenerations.Should().BeEmpty();

        _monitor.Clients.Clear();
        Poll().NewGenerations.Should().BeEmpty("an exit alone keeps the slot's markers");
        _monitor.AddClient(300, T0.AddMinutes(10));
        File.WriteAllText(LogPath(T0.AddMinutes(10)), JoinLines("Alice", "usr_aaaaaaaa-0000"));
        _now = new DateTimeOffset(T0.AddMinutes(10).AddSeconds(5));

        var replaced = Poll();
        replaced.NewGenerations.Should().BeEquivalentTo([0]);
        Entry(replaced, 0).ProcessId.Should().Be(300);
    }

    [Fact]
    public void An_exited_client_is_read_to_the_end_of_its_file()
    {
        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000"));
        Poll();

        Append(path, Lines("[SPK003] last words"));
        _monitor.Clients.Clear();
        var exited = Poll();

        Entry(exited, 0).State.Should().Be("left");
        Markers(MarkerFile(exited, 0)).Records.Should().ContainSingle().Which.Should().EndWith("last words");
    }

    [Fact]
    public void Errors_only_logging_is_reported_past_a_minute_and_hides_the_state()
    {
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), "2026.10.18 13:34:37 Error      -  StyleEngine: Missing resource reference:Atlas\r\n");

        Entry(Poll(), 0).LoggingLevel.Should().Be("unknown", "within the first minute nothing is concluded");

        _now = new DateTimeOffset(T0.AddSeconds(61));
        var observation = Poll();
        var entry = Entry(observation, 0);

        entry.LoggingLevel.Should().Be("errorsOnly");
        entry.State.Should().BeNull();
        entry.PlayersInInstance.Should().BeNull();
        entry.Warnings.Should().ContainSingle().Which.Should().Contain("Settings > Debug > Logging to Full");
        observation.SlotNotices[0].Should().ContainSingle().Which.Should().Contain("Logging");
    }

    [Fact]
    public void An_empty_log_past_a_minute_means_Logging_is_off()
    {
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), string.Empty);
        _now = new DateTimeOffset(T0.AddSeconds(61));

        Entry(Poll(), 0).LoggingLevel.Should().Be("off");
    }

    [Fact]
    public void A_missing_login_line_is_null_and_warned_about_past_a_minute()
    {
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), Lines($"[Behaviour] Joining local:{Id}"));

        Entry(Poll(), 0).Warnings.Should().BeEmpty();
        _now = new DateTimeOffset(T0.AddSeconds(61));
        var entry = Entry(Poll(), 0);

        entry.Authenticated.Should().BeNull();
        entry.Warnings.Should().ContainSingle().Which.Should().Contain("may have expired");
    }

    [Fact]
    public void A_log_larger_than_the_budget_is_caught_up_over_several_polls()
    {
        _monitor.AddClient(100, T0);
        var builder = new StringBuilder(Lines("[SPK003] first"));
        var filler = Lines(new string('x', 1000));
        while (builder.Length < 5 * 1024 * 1024)
        {
            builder.Append(filler);
        }

        builder.Append(Lines("[SPK003] last"));
        File.WriteAllText(LogPath(T0), builder.ToString());

        var first = Poll();
        var second = Poll();

        Entry(first, 0).CatchingUp.Should().BeTrue();
        Markers(MarkerFile(first, 0)).Header.GetProperty("catchingUp").GetBoolean().Should().BeTrue();
        Entry(second, 0).CatchingUp.Should().BeFalse();
        Markers(MarkerFile(second, 0)).Records.Should().HaveCount(2).And.Subject.Last().Should().EndWith("last");
        Entry(second, 0).ObservationEpoch.Should().Be(Entry(first, 0).ObservationEpoch);
    }

    [Fact]
    public void Names_are_masked_and_the_vrchat_clients_json_shape_is_stable()
    {
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), JoinLines("Alice 192.168.1.2", "usr_aaaaaaaa-0000"));

        var json = JsonSerializer.Serialize(Poll().Clients);

        json.Should().NotContain("192.168").And.NotContain("usr_aaaa");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("clients")[0].GetProperty("playersInInstance")[0].GetString()
            .Should().Be("Alice <ip>");
        document.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("logMarkerPrefixes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("[SPK003]");
        var client = document.RootElement.GetProperty("clients")[0];
        foreach (var property in new[]
        {
            "slot", "clientGeneration", "observationEpoch", "processId", "running", "state", "requestedInstance",
            "instance", "inErrorWorld", "playersInInstance", "isMaster", "loggingLevel", "authenticated",
            "joinAttemptSeq", "joinedSeq", "observedThroughSeq", "catchingUp", "lastEvents",
        })
        {
            client.TryGetProperty(property, out _).Should().BeTrue(property);
        }
    }

    [Fact]
    public void A_client_that_exits_as_its_successor_appears_keeps_its_last_markers_in_prev()
    {
        // Codex review, perspective A: the exit and the successor fell into one poll.
        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000"));
        Poll();

        Append(path, Lines("[SPK003] last words"));
        _monitor.Clients.Clear();
        _monitor.AddClient(300, T0.AddMinutes(10));
        File.WriteAllText(LogPath(T0.AddMinutes(10)), JoinLines("Alice", "usr_aaaaaaaa-0000"));
        _now = new DateTimeOffset(T0.AddMinutes(10).AddSeconds(5));
        var replaced = Poll();

        var previous = replaced.PreviousMarkers.Should().ContainSingle().Subject;
        previous.Slot.Should().Be(0);
        Markers(previous.Content!).Records.Should().ContainSingle().Which.Should().EndWith("last words");
        Entry(replaced, 0).ProcessId.Should().Be(300);
        Markers(MarkerFile(replaced, 0)).Header.GetProperty("clientGeneration").GetInt64()
            .Should().Be(Entry(replaced, 0).ClientGeneration);
    }

    [Fact]
    public void A_new_client_replaces_the_markers_file_before_its_log_appears()
    {
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000") + Lines("[SPK003] one"));
        Poll();

        _monitor.Clients.Clear();
        _monitor.AddClient(300, T0.AddMinutes(10));
        _now = new DateTimeOffset(T0.AddMinutes(10).AddSeconds(2));
        var replaced = Poll();

        Markers(replaced.PreviousMarkers.Single().Content!).Records.Should().ContainSingle();
        var (header, records) = Markers(MarkerFile(replaced, 0));
        header.GetProperty("clientGeneration").GetInt64().Should().Be(Entry(replaced, 0).ClientGeneration);
        header.GetProperty("catchingUp").GetBoolean().Should().BeTrue("its log has not been read yet");
        records.Should().BeEmpty();
        Entry(replaced, 0).State.Should().NotBe("joined", "nothing of the new client's log was read");
    }

    [Fact]
    public void Markers_that_were_not_written_are_offered_again()
    {
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000") + Lines("[SPK003] one"));

        var failed = Poll(published: false);
        var retried = Poll(published: false);
        Poll();
        var done = Poll();

        MarkerFile(retried, 0).Should().Be(MarkerFile(failed, 0));
        retried.PreviousMarkers.Should().ContainSingle("the .prev of the new generation is offered again too");
        done.MarkerFiles.Should().BeEmpty();
        done.PreviousMarkers.Should().BeEmpty();
    }

    [Fact]
    public void A_log_that_cannot_be_read_is_unknown_and_catching_up()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes make the file unreadable for this test.
        }

        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000"));
        Entry(Poll(), 0).State.Should().Be("joined");

        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var entry = Entry(Poll(), 0);

            entry.State.Should().Be("unknown");
            entry.CatchingUp.Should().BeTrue();
            entry.Warnings.Should().Contain(w => w.Contains("could not read this client's log"));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Entry(Poll(), 0).State.Should().Be("joined");
    }

    [Fact]
    public void A_replaced_client_with_a_large_backlog_is_read_to_its_end_before_its_prev_is_kept()
    {
        // Codex review, round 2: one bounded read could stop before the last marker.
        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000"));
        Poll();

        var backlog = new StringBuilder();
        var filler = Lines(new string('x', 1000));
        while (backlog.Length < 5 * 1024 * 1024)
        {
            backlog.Append(filler);
        }

        Append(path, backlog + Lines("[SPK003] last words"));
        _monitor.Clients.Clear();
        _monitor.AddClient(300, T0.AddMinutes(10));
        File.WriteAllText(LogPath(T0.AddMinutes(10)), JoinLines("Alice", "usr_aaaaaaaa-0000"));
        _now = new DateTimeOffset(T0.AddMinutes(10).AddSeconds(5));

        var first = Poll();
        Markers(first.PreviousMarkers.Single().Content!).Header.GetProperty("catchingUp").GetBoolean()
            .Should().BeTrue("what was read so far is kept as .prev while the rest is read");
        first.MarkerFiles.Should().NotContain(m => m.Slot == 0, "its markers file stays until its .prev is complete");

        var second = Poll();
        Markers(second.PreviousMarkers.Single().Content!).Records.Should().ContainSingle().Which.Should().EndWith("last words");
        MarkerFile(second, 0).Should().Contain("\"clientGeneration\":" + Entry(second, 0).ClientGeneration);
    }

    [Fact]
    public void A_log_that_disappears_is_no_longer_reported_as_current()
    {
        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000"));
        Poll();
        var log = _reader.Read();

        File.Delete(path);
        var entry = _observer.Observe(log, CancellationToken.None).Clients.Clients.Single();

        entry.State.Should().Be("unknown");
        entry.CatchingUp.Should().BeTrue();
    }

    [Fact]
    public void No_client_is_observed_while_no_epoch_can_be_saved()
    {
        var blocked = Path.Combine(_tempDir, "blocked");
        File.WriteAllText(blocked, string.Empty);
        var roster = new VrchatClientRoster(Path.Combine(blocked, VrchatClientRoster.FileName));
        var reader = new VrchatLogReader(
            Microsoft.Extensions.Options.Options.Create(Options), _monitor, NullLogger<VrchatLogReader>.Instance,
            CreationTimeFromName, queryOwners: null, managedClients: null, roster);
        var observer = new VrchatClientObserver(Options, roster, null, CreationTimeFromName, () => _now);
        _monitor.AddClient(100, T0);
        File.WriteAllText(LogPath(T0), JoinLines("Alice", "usr_aaaaaaaa-0000") + Lines("[SPK003] one"));

        var observation = observer.Observe(reader.Read(), CancellationToken.None);

        observation.MarkerFiles.Should().BeEmpty();
        observation.Clients.Clients.Single().ObservationEpoch.Should().BeNull();
    }

    [Fact]
    public void A_shrunk_log_is_read_from_its_start_once_an_epoch_can_be_saved_again()
    {
        // Codex review, round 3: a failed re-read was forgotten once the file grew back.
        _monitor.AddClient(100, T0);
        var path = LogPath(T0);
        File.WriteAllText(path, JoinLines("Alice", "usr_aaaaaaaa-0000") + Lines("[SPK003] one"));
        var before = Poll();
        var length = new FileInfo(path).Length;

        Directory.Delete(_stateDir, recursive: true);
        File.WriteAllText(_stateDir, "a file where the state directory was: nothing can be saved");
        File.WriteAllText(path, Lines("[SPK003] two"));
        Entry(Poll(), 0).State.Should().Be("unknown");
        File.WriteAllText(path, Lines("[SPK003] three") + Lines(new string('x', (int)length)));
        Entry(Poll(), 0).State.Should().Be("unknown", "the file grew back, but it was never read from its start");

        File.Delete(_stateDir);
        Directory.CreateDirectory(_stateDir);
        var after = Poll();

        Entry(after, 0).ObservationEpoch.Should().BeGreaterThan(Entry(before, 0).ObservationEpoch!.Value);
        Markers(MarkerFile(after, 0)).Records.Should().ContainSingle().Which.Should().EndWith("[SPK003] three");
    }
}

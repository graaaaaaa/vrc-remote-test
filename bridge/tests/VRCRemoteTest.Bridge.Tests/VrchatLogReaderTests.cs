using System.ComponentModel;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Real filesystem tests (no mocking), matching this project's established
/// Bridge test strategy. VrchatWorldsDirectory is pointed at a "Worlds"
/// subfolder so its parent (the log directory VrchatLogReader derives) is a
/// separate, controllable temp directory. The running VRChat clients come from a
/// fake process monitor, and the processes that have each file open from a fake Restart
/// Manager; the matching rules themselves are covered by VrchatLogMatcherTests and
/// VrchatLogOwnersTests. Readers made by CreateReader have no Restart Manager, as on a
/// machine that is not Windows, so they show the name match alone, as before.
/// </summary>
public class VrchatLogReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logDir;
    private readonly string _worldsDir;
    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly CapturingLogger<VrchatLogReader> _logger = new();

    /// <summary>What the fake Restart Manager reports, by file name; files not listed have no owner.</summary>
    private readonly Dictionary<string, VrchatLogOwners.Owner[]> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file names the fake Restart Manager was asked about, in order.</summary>
    private readonly List<string> _queried = [];

    /// <summary>When set, the fake Restart Manager throws this instead of answering.</summary>
    private Exception? _ownerQueryError;

    public VrchatLogReaderTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-logreader-").FullName;
        _logDir = Path.Combine(_tempDir, "VRChat", "VRChat");
        _worldsDir = Path.Combine(_logDir, "Worlds");
        Directory.CreateDirectory(_worldsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A start time that is not near a daylight saving transition in common time zones.</summary>
    private static readonly DateTime T0 = new(2026, 10, 17, 6, 0, 0, 100, DateTimeKind.Utc);

    /// <summary>
    /// Each file's creation time is read from its name, as if VRChat had just created
    /// it, so matching depends neither on the clock nor on whether the filesystem can
    /// record or set creation times.
    /// </summary>
    private VrchatLogReader CreateReader(Func<string, DateTime>? creationTimeUtc = null) =>
        new(
            Options.Create(new BridgeOptions { VrchatWorldsDirectory = _worldsDir }),
            _monitor,
            NullLogger<VrchatLogReader>.Instance,
            creationTimeUtc ?? CreationTimeFromName);

    /// <summary>A reader whose Restart Manager reports the owners set with <see cref="Open"/>.</summary>
    private VrchatLogReader CreateOwnerAwareReader(Func<string, DateTime>? creationTimeUtc = null) =>
        new(
            Options.Create(new BridgeOptions { VrchatWorldsDirectory = _worldsDir }),
            _monitor,
            _logger,
            creationTimeUtc ?? CreationTimeFromName,
            QueryOwners);

    private IReadOnlyList<VrchatLogOwners.Owner> QueryOwners(string path)
    {
        _queried.Add(Path.GetFileName(path));
        if (_ownerQueryError is not null)
        {
            throw _ownerQueryError;
        }

        return _owners.TryGetValue(Path.GetFileName(path), out var owners) ? owners : [];
    }

    /// <summary>Reports that the monitor's clients <paramref name="processIds"/> have <paramref name="path"/> open.</summary>
    private void Open(string path, params int[] processIds) =>
        SetOwners(path, processIds
            .Select(pid => new VrchatLogOwners.Owner(
                pid, _monitor.Clients.Single(c => c.ProcessId == pid).CreationTimeUtc, "VRChat"))
            .ToArray());

    private void SetOwners(string path, params VrchatLogOwners.Owner[] owners) =>
        _owners[Path.GetFileName(path)] = owners;

    private static DateTime CreationTimeFromName(string path) =>
        VrchatLogMatcher.ParseNameTime(Path.GetFileName(path)) is { } local
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local)
            : DateTime.MinValue;

    /// <summary>
    /// Writes an output_log named after <paramref name="startedUtc"/> in the machine's time
    /// zone, as VRChat does, with each "\n" of <paramref name="content"/> as the CRLF that
    /// ends VRChat's lines.
    /// </summary>
    private string WriteLogFor(DateTime startedUtc, string content)
    {
        var path = Path.Combine(_logDir, $"output_log_{startedUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt");
        File.WriteAllText(path, content.Replace("\n", "\r\n"));
        return path;
    }

    [Fact]
    public void ReadSnapshot_returns_empty_when_log_directory_does_not_exist()
    {
        var options = Options.Create(new BridgeOptions
        {
            VrchatWorldsDirectory = Path.Combine(_tempDir, "does-not-exist", "Worlds"),
        });
        var reader = new VrchatLogReader(
            options,
            _monitor,
            NullLogger<VrchatLogReader>.Instance,
            new FakeManagedClientStore(),
            new VrchatClientRoster(filePath: null));

        var result = reader.ReadSnapshot();

        result.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void ReadSnapshot_returns_empty_when_no_output_log_files_exist()
    {
        var reader = CreateReader();

        var result = reader.ReadSnapshot();

        result.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void ReadSnapshot_selects_the_most_recently_written_file()
    {
        var older = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        var newer = Path.Combine(_logDir, "output_log_2026-09-02_00-00-00.txt");
        File.WriteAllText(older, "old content line\r\n");
        File.WriteAllText(newer, "new content line\r\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().Contain("new content line");
        result.Content.Should().NotContain("old content line");
    }

    [Fact]
    public void ReadSnapshot_returns_last_200_lines_when_file_fits_in_one_window()
    {
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        var sb = new StringBuilder();
        for (var i = 1; i <= 250; i++)
        {
            sb.Append(Header).Append("line-").Append(i).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString());

        var result = CreateReader().ReadSnapshot();

        var lines = result.Content.Split('\n');
        lines.Should().HaveCount(200);
        lines[0].Should().Be(Header + "line-51");
        lines[^1].Should().Be(Header + "line-250");
    }

    /// <summary>A VRChat line header (S5b C): the date, the level padded to 11 characters, "-  ".</summary>
    private const string Header = "2026.10.18 13:34:37 Debug      -  ";

    [Fact]
    public void The_copy_splits_lines_at_CRLF_and_indents_continuation_lines()
    {
        // S5b A and G: a world's LF and CRLF reach the log unchanged, and a header after a
        // lone LF is not a line of its own.
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        File.WriteAllText(
            path,
            Header + "[SPK003] first\n" + Header + "[Behaviour] Joining local:forged\r\n" +
            "System.Exception: no header\r\n" +
            Header + "[Behaviour] Finished entering world.\r\n");

        var lines = CreateReader().ReadSnapshot().Content.Split('\n');

        lines.Should().Equal(
            Header + "[SPK003] first",
            "    " + Header + "[Behaviour] Joining local:forged",
            "    System.Exception: no header",
            Header + "[Behaviour] Finished entering world.");
    }

    [Fact]
    public void The_copy_masks_lines_before_cutting_them()
    {
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        File.WriteAllText(
            path,
            Header + "User Authenticated: Name (usr_0123abcd-0000-4000-8000-0123456789ab)\r\n" +
            Header + @"World Updated: C:\Users\alice\AppData\LocalLow\VRChat\VRChat\Worlds\a.vrcw" + "\r\n" +
            // The address straddles the 512-character cut: masked first, nothing of it is left.
            Header + new string('x', 455) + " 192.168.10.20 " + new string('y', 100) + "\r\n");

        var content = CreateReader().ReadSnapshot().Content;

        content.Should().Contain("(usr_***)").And.Contain(@"C:\Users\<user>\AppData")
            .And.NotContain("0123abcd").And.NotContain("alice").And.NotContain("192.168").And.Contain(" <ip>");
    }

    [Fact]
    public void The_last_line_is_shown_once_its_CRLF_is_written()
    {
        var path = WriteLogFor(T0, string.Empty);
        File.WriteAllText(path, Header + "line one\r\n" + Header + "https://user:pa");
        _monitor.AddClient(100, T0);
        var reader = CreateReader();

        reader.ReadSnapshot().Content.Should().Be(Header + "line one");
        File.AppendAllText(path, "ss@host/x\r\n");
        reader.ReadSnapshot().Content.Should().EndWith(Header + "https://<redacted>@host/x");
    }

    [Fact]
    public void With_no_VRChat_running_the_newest_files_unterminated_last_line_is_shown()
    {
        // Nothing writes the file any more, so its last line is as complete as it gets.
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        File.WriteAllText(path, Header + "normal\r\nfinal crash detail");

        CreateReader().ReadSnapshot().Content.Should().EndWith("    final crash detail");
    }

    [Fact]
    public void A_line_is_masked_whole_before_it_is_split_at_its_lone_LFs()
    {
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        File.WriteAllText(path, Header + "[VRCRT] {\"password\":\n  \"hunter2\"}\r\n");

        CreateReader().ReadSnapshot().Content.Should().NotContain("hunter2").And.Contain("<redacted>");
    }

    [Fact]
    public void ReadSnapshot_discards_the_partial_first_line_when_seeking_mid_file()
    {
        // Three ~150KB lines -> total > the 256KB tail window, so the window
        // starts partway through line B. The (possibly-partial) first decoded
        // line must be dropped entirely -- no fragment of "B" should leak into
        // the published output.
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        var lineA = new string('A', 150_000);
        var lineB = new string('B', 150_000);
        var lineC = new string('C', 150_000);
        File.WriteAllText(path, $"{lineA}\r\n{lineB}\r\n{lineC}\r\n");

        var result = CreateReader().ReadSnapshot();

        result.Content.Should().NotContain("B");
        result.Content.Should().Contain("C");
    }

    [Fact]
    public void ReadSnapshot_truncates_lines_longer_than_512_characters()
    {
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        File.WriteAllText(path, new string('X', 1000) + "\r\n");

        var result = CreateReader().ReadSnapshot();

        result.Content.Should().Contain("... [truncated]");
        result.Content.Length.Should().BeLessThan(1000);
    }

    [Fact]
    public void ReadSnapshot_never_exceeds_the_384KiB_bridge_publish_cap()
    {
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        var sb = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            sb.Append(new string('L', 512)).Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString());

        var result = CreateReader().ReadSnapshot();

        Encoding.UTF8.GetByteCount(result.Content).Should().BeLessOrEqualTo(384 * 1024);
    }

    [Fact]
    public void ReadSnapshot_reports_a_diagnostic_when_the_directory_scan_cap_is_exceeded()
    {
        for (var i = 0; i < 513; i++)
        {
            File.WriteAllText(Path.Combine(_logDir, $"output_log_test_{i:D4}.txt"), string.Empty);
        }

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().Contain("scan cap");
    }

    [Fact]
    public void ReadSnapshot_shows_the_first_clients_log_rather_than_the_newest_file()
    {
        WriteLogFor(T0, "first client line\n");
        var second = WriteLogFor(T0.AddSeconds(3), "second client line\n");
        File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(1));
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().Contain("first client line");
        result.Content.Should().NotContain("second client line");
    }

    private VrchatLogReader CreateRelaunchReader(FakeManagedClientStore store) =>
        new(
            Options.Create(new BridgeOptions { VrchatWorldsDirectory = _worldsDir, VrchatClientMode = "Relaunch" }),
            _monitor,
            NullLogger<VrchatLogReader>.Instance,
            CreationTimeFromName,
            store);

    [Fact]
    public void In_Relaunch_mode_the_first_client_is_the_running_one_launched_first_according_to_the_record()
    {
        WriteLogFor(T0, "older client line\n");
        WriteLogFor(T0.AddSeconds(3), "first launched line\n");
        _monitor.AddClient(100, T0, watchWorlds: false);
        _monitor.AddClient(200, T0.AddSeconds(3), watchWorlds: false);
        var store = new FakeManagedClientStore
        {
            Clients = [new ManagedClient(200, T0.AddSeconds(3), 1, Profile: 1, LaunchOrder: 0), new ManagedClient(100, T0, 1, 0, 1)],
        };

        var result = CreateRelaunchReader(store).ReadSnapshot();

        result.Content.Should().Contain("first launched line").And.NotContain("older client line");
    }

    [Fact]
    public void In_Relaunch_mode_a_recorded_client_that_exited_passes_the_log_to_the_next_one_in_launch_order()
    {
        WriteLogFor(T0, "second launched line\n");
        WriteLogFor(T0.AddSeconds(3), "third launched line\n");
        _monitor.AddClient(100, T0, watchWorlds: false);
        _monitor.AddClient(200, T0.AddSeconds(3), watchWorlds: false);
        var store = new FakeManagedClientStore
        {
            Clients =
            [
                new ManagedClient(50, T0.AddSeconds(-3), 1, 0, 0), // closed
                new ManagedClient(200, T0.AddSeconds(3), 1, 2, 2),
                new ManagedClient(100, T0, 1, 1, 1),
            ],
        };

        CreateRelaunchReader(store).ReadSnapshot().Content.Should().Contain("second launched line");
    }

    [Fact]
    public void In_Relaunch_mode_a_client_with_the_recorded_PID_but_another_creation_time_is_not_chosen_from_the_record()
    {
        WriteLogFor(T0, "older client line\n");
        WriteLogFor(T0.AddSeconds(3), "reused pid line\n");
        _monitor.AddClient(100, T0, watchWorlds: false);
        _monitor.AddClient(200, T0.AddSeconds(3), watchWorlds: false);
        var store = new FakeManagedClientStore
        {
            Clients = [new ManagedClient(200, T0.AddSeconds(3).AddTicks(1), 1, 0, 0)],
        };

        CreateRelaunchReader(store).ReadSnapshot().Content.Should().Contain("older client line",
            "without a running recorded client, the oldest client is the first one, as in Reload mode");
    }

    [Fact]
    public void ReadSnapshot_reports_a_diagnostic_instead_of_another_clients_log_when_nothing_matches()
    {
        WriteLogFor(T0.AddMinutes(10), "some other session\n");
        _monitor.AddClient(100, T0);

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().Contain("Could not match").And.Contain("PID 100");
        result.Content.Should().NotContain("some other session");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void ReadSnapshot_replaces_the_previous_sessions_log_while_the_first_client_has_no_log_yet(int secondsAgo)
    {
        // Publishing nothing here would leave the closed client's log on the share, shown
        // as if it were the first client's (a9292a1 re-verification on real hardware).
        var previous = WriteLogFor(DateTime.UtcNow.AddMinutes(-5), "previous session line\n");
        _monitor.AddClient(100, DateTime.UtcNow.AddSeconds(-secondsAgo));

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().Contain("has not created its output_log_*.txt file yet").And.Contain("PID 100")
            .And.Contain("heavy CPU load").And.NotContain("previous session line").And.NotContain("Could not match");
        File.Exists(previous).Should().BeTrue();
    }

    [Fact]
    public void ReadSnapshot_stops_saying_not_yet_once_no_file_can_be_named_in_the_clients_range()
    {
        _monitor.AddClient(100, DateTime.UtcNow.AddSeconds(-61));

        var result = CreateReader().ReadSnapshot();

        result.Content.Should().Contain("Could not match").And.NotContain("yet");
    }

    [Fact]
    public void ReadSnapshot_matches_a_log_created_seconds_after_the_time_in_its_name()
    {
        // Under heavy load VRChat created output_log_..._18-04-10.txt at 18:04:11.
        var path = WriteLogFor(T0.AddSeconds(20), "first client line\n");
        _monitor.AddClient(100, T0);
        var reader = new VrchatLogReader(
            Options.Create(new BridgeOptions { VrchatWorldsDirectory = _worldsDir }),
            _monitor,
            NullLogger<VrchatLogReader>.Instance,
            p => CreationTimeFromName(p).AddSeconds(5));

        reader.ReadSnapshot().Content.Should().Contain("first client line");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void ReadSnapshot_reports_the_diagnostic_at_once_when_the_start_time_is_unknown()
    {
        _monitor.AddClient(100, creationTimeUtc: null);

        CreateReader().ReadSnapshot().Content.Should().Contain("Could not match").And.Contain("start time unknown");
    }

    /// <summary>Clients 100 (first) and 200 with their own logs; the reader has pinned 100's.</summary>
    private VrchatLogReader CreatePinnedReader()
    {
        WriteLogFor(T0, "first client line\n");
        WriteLogFor(T0.AddSeconds(3), "second client line\n");
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        var reader = CreateReader();
        reader.ReadSnapshot().Content.Should().Contain("first client line");
        return reader;
    }

    [Fact]
    public void The_pin_stays_while_its_client_runs_even_when_another_client_would_be_chosen_now()
    {
        var reader = CreatePinnedReader();

        _monitor.Clients[0] = new VrchatClientInfo(100, T0, WatchWorldsDetected: false);

        reader.ReadSnapshot().Content.Should().Contain("first client line");
    }

    [Fact]
    public void The_pin_stays_when_its_client_cannot_be_checked_for_a_poll()
    {
        var reader = CreatePinnedReader();

        _monitor.Clients.RemoveAt(0);
        _monitor.UnverifiedCount = 1;
        var whileUnverified = reader.ReadSnapshot();

        _monitor.Clients.Insert(0, new VrchatClientInfo(100, T0, true));
        _monitor.UnverifiedCount = 0;
        var afterwards = reader.ReadSnapshot();

        whileUnverified.Content.Should().Contain("could not be checked").And.NotContain("second client line");
        afterwards.Content.Should().Contain("first client line");
    }

    [Fact]
    public void The_pin_stays_when_the_process_list_cannot_be_read_for_a_poll()
    {
        var reader = CreatePinnedReader();

        _monitor.EnumerationError = "transient failure";
        var whileFailing = reader.ReadSnapshot();
        _monitor.EnumerationError = null;
        var afterwards = reader.ReadSnapshot();

        whileFailing.Content.Should().Contain("could not be checked").And.NotContain("client line");
        afterwards.Content.Should().Contain("first client line");
    }

    [Fact]
    public void The_pin_is_still_read_when_only_another_process_cannot_be_checked()
    {
        var reader = CreatePinnedReader();

        _monitor.UnverifiedCount = 1;

        reader.ReadSnapshot().Content.Should().Contain("first client line");
    }

    [Fact]
    public void A_confirmed_exit_moves_the_log_to_the_next_client()
    {
        var reader = CreatePinnedReader();

        _monitor.Clients.RemoveAt(0);

        reader.ReadSnapshot().Content.Should().Contain("second client line");
    }

    [Fact]
    public void A_reused_PID_releases_the_pin()
    {
        var reader = CreatePinnedReader();

        // PID 100 now belongs to a process started 30 s later, which has no log yet.
        _monitor.Clients[0] = new VrchatClientInfo(100, T0.AddSeconds(30), true);
        _monitor.Clients.RemoveAt(1);

        reader.ReadSnapshot().Content.Should().Contain("Could not match").And.NotContain("first client line");
    }

    [Fact]
    public void A_deleted_log_releases_the_pin()
    {
        var reader = CreatePinnedReader();

        File.Delete(Path.Combine(_logDir, $"output_log_{T0.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt"));

        reader.ReadSnapshot().Content.Should().Contain("Could not match");
    }

    [Fact]
    public void A_second_log_in_the_same_range_does_not_unpin_the_first()
    {
        var reader = CreatePinnedReader();

        // A fresh match would now be ambiguous; the pinned file keeps being read.
        WriteLogFor(T0.AddSeconds(1), "later line\n");

        reader.ReadSnapshot().Content.Should().Contain("first client line");
    }

    [Fact]
    public void ReadSnapshot_reports_a_diagnostic_when_the_running_VRChat_cannot_be_checked()
    {
        WriteLogFor(T0, "unknown owner\n");
        _monitor.UnverifiedCount = 1;

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().NotContain("unknown owner");
    }

    [Fact]
    public void ReadSnapshot_reports_a_diagnostic_when_the_process_list_cannot_be_read()
    {
        WriteLogFor(T0, "unknown owner\n");
        _monitor.EnumerationError = "access denied";

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().NotContain("unknown owner");
    }

    [Fact]
    public void Restart_Manager_finds_the_first_clients_log_when_its_name_is_far_from_its_creation()
    {
        // B1 of the PR #3 verification: named 19-53-40 under load, but created 33 s later.
        Func<string, DateTime> creation = p => CreationTimeFromName(p).AddSeconds(33);
        _monitor.AddClient(100, T0);
        var path = WriteLogFor(T0.AddSeconds(75), "first client line\n");
        Open(path, 100);

        CreateReader(creation).ReadSnapshot().Content.Should().NotContain("first client line", "the name is not trusted");
        CreateOwnerAwareReader(creation).ReadSnapshot().Content.Should().Contain("first client line");
        _logger.MessagesAt(LogLevel.Information).Should().Contain(m =>
            m.Contains("as the log of VRChat client 100") && m.Contains("the only VRChat process that has it open"));
    }

    [Fact]
    public void Restart_Manager_finds_the_first_clients_log_when_it_is_named_after_the_next_clients_start()
    {
        // B2, B3 and B5: the first client's log appeared after the next client had started.
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(5));
        var first = WriteLogFor(T0.AddSeconds(55), "first client line\n");
        var second = WriteLogFor(T0.AddSeconds(60), "second client line\n");
        Open(first, 100);
        Open(second, 200);

        CreateReader().ReadSnapshot().Content.Should().Contain("Could not match");
        CreateOwnerAwareReader().ReadSnapshot().Content.Should().Contain("first client line").And.NotContain("second");
    }

    [Fact]
    public void A_log_the_first_client_shares_with_another_client_is_not_shown()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        var shared = WriteLogFor(T0.AddSeconds(30), "mixed line\n");
        Open(shared, 100, 200);

        var result = CreateOwnerAwareReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().Contain("is or was also open by other VRChat clients").And.Contain("PID 100")
            .And.NotContain("mixed line");
    }

    [Fact]
    public void A_log_seen_mixed_stays_refused_after_the_other_client_exits()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        var shared = WriteLogFor(T0.AddSeconds(30), "mixed line\n");
        Open(shared, 100, 200);
        var reader = CreateOwnerAwareReader();
        reader.ReadSnapshot();

        _monitor.Clients.RemoveAt(1);
        Open(shared, 100);

        reader.ReadSnapshot().Content.Should().Contain("is or was also open by other VRChat clients")
            .And.NotContain("mixed line");
    }

    [Fact]
    public void A_shown_log_that_another_client_opens_is_withdrawn()
    {
        _monitor.AddClient(100, T0);
        var path = WriteLogFor(T0, "first client line\n");
        Open(path, 100);
        var reader = CreateOwnerAwareReader();
        reader.ReadSnapshot().Content.Should().Contain("first client line");

        _monitor.AddClient(200, T0.AddSeconds(1));
        Open(path, 100, 200);

        reader.ReadSnapshot().Content.Should().Contain("also open by other VRChat clients").And.NotContain("client line");
        _queried.Should().HaveCount(2, "the shown file is asked about on every poll");
    }

    [Fact]
    public void A_name_match_gives_way_to_the_file_the_client_turns_out_to_have_open()
    {
        // A closed session's file named in the client's first second, before its own appears.
        _monitor.AddClient(100, T0);
        WriteLogFor(T0, "closed session line\n");
        var reader = CreateOwnerAwareReader();
        reader.ReadSnapshot().Content.Should().Contain("closed session line");

        var own = WriteLogFor(T0.AddSeconds(1), "first client line\n");
        Open(own, 100);

        reader.ReadSnapshot().Content.Should().Contain("first client line").And.NotContain("closed session");
    }

    [Fact]
    public void A_name_matched_log_that_another_client_has_open_is_released()
    {
        _monitor.AddClient(100, T0);
        var path = WriteLogFor(T0, "first second line\n");
        var reader = CreateOwnerAwareReader();
        reader.ReadSnapshot().Content.Should().Contain("first second line");

        _monitor.AddClient(200, T0.AddSeconds(30));
        Open(path, 200);

        reader.ReadSnapshot().Content.Should().NotContain("first second line").And.Contain("PID 100");
        _logger.MessagesAt(LogLevel.Information).Should().Contain(m => m.Contains("No longer showing"));
    }

    [Fact]
    public void The_name_match_never_picks_a_file_another_VRChat_has_open()
    {
        // E.g. a VRChat in another session, or one that could not be checked.
        _monitor.AddClient(100, T0);
        var path = WriteLogFor(T0, "other VRChat line\n");
        SetOwners(path, new VrchatLogOwners.Owner(300, T0.AddMinutes(-1), "VRChat"));

        var result = CreateOwnerAwareReader().ReadSnapshot();

        result.Content.Should().NotContain("other VRChat line").And.Contain("PID 100");
    }

    [Fact]
    public void Processes_that_are_not_VRChat_do_not_keep_the_log_from_being_shown()
    {
        _monitor.AddClient(100, T0);
        var path = WriteLogFor(T0.AddSeconds(75), "first client line\n");
        SetOwners(
            path,
            new VrchatLogOwners.Owner(4242, T0.AddHours(-1), "VRCRemoteTest.Bridge"),
            new VrchatLogOwners.Owner(100, T0, "VRChat"),
            new VrchatLogOwners.Owner(3100, T0.AddDays(-1), "MsMpEng"));

        CreateOwnerAwareReader().ReadSnapshot().Content.Should().Contain("first client line");
    }

    [Fact]
    public void The_owner_is_trusted_while_another_VRChat_cannot_be_checked()
    {
        _monitor.AddClient(100, T0);
        _monitor.UnverifiedCount = 1;
        var path = WriteLogFor(T0, "first client line\n");
        Open(path, 100);

        CreateReader().ReadSnapshot().Content.Should().NotContain("first client line");
        CreateOwnerAwareReader().ReadSnapshot().Content.Should().Contain("first client line");
    }

    [Fact]
    public void Several_files_open_by_the_first_client_are_not_shown()
    {
        _monitor.AddClient(100, T0);
        var a = WriteLogFor(T0, "a line\n");
        var b = WriteLogFor(T0.AddSeconds(20), "b line\n");
        Open(a, 100);
        Open(b, 100);

        CreateOwnerAwareReader().ReadSnapshot().Content.Should().Contain("Could not match").And.NotContain("line");
    }

    [Fact]
    public void With_no_owners_at_all_the_names_decide_as_before()
    {
        WriteLogFor(T0, "first client line\n");
        WriteLogFor(T0.AddSeconds(3), "second client line\n");
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));

        CreateOwnerAwareReader().ReadSnapshot().Content.Should().Contain("first client line");
        _logger.MessagesAt(LogLevel.Information).Should().Contain(m => m.Contains("matched by the time in its name"));
    }

    [Fact]
    public void A_Restart_Manager_failure_falls_back_to_the_names_and_is_logged_once()
    {
        WriteLogFor(T0, "first client line\n");
        WriteLogFor(T0.AddSeconds(3), "second client line\n");
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        _ownerQueryError = new Win32Exception(5);
        var reader = CreateOwnerAwareReader();

        reader.ReadSnapshot().Content.Should().Contain("first client line");
        reader.ReadSnapshot().Content.Should().Contain("first client line");
        _ownerQueryError = null;
        reader.ReadSnapshot().Content.Should().Contain("first client line");

        _logger.MessagesAt(LogLevel.Warning).Should().ContainSingle().Which.Should().Contain("Restart Manager");
        _logger.MessagesAt(LogLevel.Information).Should().Contain(m => m.Contains("Restart Manager answers again"));
    }

    [Fact]
    public void A_shown_log_stays_while_Restart_Manager_fails()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        var path = WriteLogFor(T0.AddSeconds(75), "first client line\n");
        Open(path, 100);
        var reader = CreateOwnerAwareReader();
        reader.ReadSnapshot().Content.Should().Contain("first client line");

        _ownerQueryError = new Win32Exception(5);

        reader.ReadSnapshot().Content.Should().Contain("first client line");
    }

    [Fact]
    public void Not_yet_lasts_past_a_minute_while_Restart_Manager_can_still_find_the_log()
    {
        _monitor.AddClient(100, DateTime.UtcNow.AddSeconds(-90));

        var result = CreateOwnerAwareReader().ReadSnapshot();

        result.Content.Should().Contain("has not created its output_log_*.txt file yet").And.Contain("a minute or more")
            .And.NotContain("Could not match");
    }

    [Fact]
    public void Not_yet_holds_while_the_other_clients_already_hold_their_logs()
    {
        // B: past the 60 s wait the other clients start and log, while the first has no log yet.
        var now = DateTime.UtcNow;
        _monitor.AddClient(100, now.AddSeconds(-90));
        _monitor.AddClient(200, now.AddSeconds(-20));
        var second = WriteLogFor(now.AddSeconds(-19), "second client line\n");
        Open(second, 200);

        CreateReader().ReadSnapshot().Content.Should().Contain("Could not match");
        CreateOwnerAwareReader().ReadSnapshot().Content.Should().Contain("has not created its output_log_*.txt file yet")
            .And.NotContain("second client line");
    }

    [Fact]
    public void Only_files_created_since_shortly_before_the_client_started_are_asked_about()
    {
        WriteLogFor(T0.AddMinutes(-2), "old session line\n");
        var recent = WriteLogFor(T0.AddSeconds(-30), "recent session line\n");
        var own = WriteLogFor(T0, "first client line\n");
        _monitor.AddClient(100, T0);
        Open(own, 100);

        CreateOwnerAwareReader().ReadSnapshot().Content.Should().Contain("first client line");

        _queried.Should().BeEquivalentTo([Path.GetFileName(recent), Path.GetFileName(own)]);
    }
}

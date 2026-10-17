using System.Text;
using FluentAssertions;
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
/// fake process monitor; the matching rules themselves are covered by
/// VrchatLogMatcherTests.
/// </summary>
public class VrchatLogReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logDir;
    private readonly string _worldsDir;
    private readonly FakeVrchatProcessMonitor _monitor = new();

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
    private VrchatLogReader CreateReader() =>
        new(
            Options.Create(new BridgeOptions { VrchatWorldsDirectory = _worldsDir }),
            _monitor,
            NullLogger<VrchatLogReader>.Instance,
            CreationTimeFromName);

    private static DateTime CreationTimeFromName(string path) =>
        VrchatLogMatcher.ParseNameTime(Path.GetFileName(path)) is { } local
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local)
            : DateTime.MinValue;

    /// <summary>Writes an output_log named after <paramref name="startedUtc"/> in the machine's time zone, as VRChat does.</summary>
    private string WriteLogFor(DateTime startedUtc, string content)
    {
        var path = Path.Combine(_logDir, $"output_log_{startedUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadSnapshot_returns_empty_when_log_directory_does_not_exist()
    {
        var options = Options.Create(new BridgeOptions
        {
            VrchatWorldsDirectory = Path.Combine(_tempDir, "does-not-exist", "Worlds"),
        });
        var reader = new VrchatLogReader(options, _monitor, NullLogger<VrchatLogReader>.Instance);

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
        File.WriteAllText(older, "old content line\n");
        File.WriteAllText(newer, "new content line\n");
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
            sb.Append("line-").Append(i).Append('\n');
        }
        File.WriteAllText(path, sb.ToString());

        var result = CreateReader().ReadSnapshot();

        var lines = result.Content.Split('\n');
        lines.Should().HaveCount(200);
        lines[0].Should().Be("line-51");
        lines[^1].Should().Be("line-250");
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
        File.WriteAllText(path, $"{lineA}\n{lineB}\n{lineC}\n");

        var result = CreateReader().ReadSnapshot();

        result.Content.Should().NotContain("B");
        result.Content.Should().Contain("C");
    }

    [Fact]
    public void ReadSnapshot_truncates_lines_longer_than_512_characters()
    {
        var path = Path.Combine(_logDir, "output_log_2026-09-01_00-00-00.txt");
        File.WriteAllText(path, new string('X', 1000) + "\n");

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
            sb.Append(new string('L', 512)).Append('\n');
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

    [Fact]
    public void ReadSnapshot_publishes_nothing_while_a_just_started_client_has_no_log_yet()
    {
        var previous = WriteLogFor(T0, "previous session line\n");
        _monitor.AddClient(100, DateTime.UtcNow);

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeFalse("the share keeps its last log instead of a diagnostic");
        File.Exists(previous).Should().BeTrue();
    }

    [Fact]
    public void ReadSnapshot_reports_the_diagnostic_once_the_client_is_past_the_grace_period()
    {
        _monitor.AddClient(100, DateTime.UtcNow.AddSeconds(-11));

        var result = CreateReader().ReadSnapshot();

        result.IsAvailable.Should().BeTrue();
        result.Content.Should().Contain("Could not match");
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
}

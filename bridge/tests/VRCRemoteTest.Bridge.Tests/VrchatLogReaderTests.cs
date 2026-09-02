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
/// separate, controllable temp directory.
/// </summary>
public class VrchatLogReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logDir;
    private readonly string _worldsDir;

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

    private VrchatLogReader CreateReader() =>
        new(
            Options.Create(new BridgeOptions { VrchatWorldsDirectory = _worldsDir }),
            NullLogger<VrchatLogReader>.Instance);

    [Fact]
    public void ReadSnapshot_returns_empty_when_log_directory_does_not_exist()
    {
        var options = Options.Create(new BridgeOptions
        {
            VrchatWorldsDirectory = Path.Combine(_tempDir, "does-not-exist", "Worlds"),
        });
        var reader = new VrchatLogReader(options, NullLogger<VrchatLogReader>.Instance);

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
}

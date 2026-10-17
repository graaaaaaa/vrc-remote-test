using FluentAssertions;
using Microsoft.Extensions.Logging;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// The held-open cases are Windows-only: on Unix, rename(2) replaces an open
/// destination, so the in-place fallback never runs there.
/// </summary>
public class SharedFileWriterTests : IDisposable
{
    /// <summary>The share mode of the SMB server's handle observed while the Mac polled the file.</summary>
    private const FileShare SmbClientShare = FileShare.ReadWrite | FileShare.Delete;

    private readonly string _tempDir;
    private readonly string _path;
    private readonly ListLogger<SharedFileWriterTests> _logger = new();
    private readonly SharedFileWriter _writer;

    public SharedFileWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-sharedfile-").FullName;
        _path = Path.Combine(_tempDir, "vrchat-latest.log");
        _writer = new SharedFileWriter(_logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Replaces_the_destination_when_nothing_holds_it_open()
    {
        File.WriteAllText(_path, "old");

        _writer.Write(_path, "new").Should().Be(SharedFileWriteOutcome.Replaced);

        File.ReadAllText(_path).Should().Be("new");
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(_path);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Rewrites_in_place_a_destination_held_open_the_way_the_Mac_SMB_client_holds_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(_path, "old content that is longer than the new content");
        using var macHandle = HoldOpen(SmbClientShare);

        _writer.Write(_path, "new content").Should().Be(SharedFileWriteOutcome.OverwrittenInPlace);
        ReadThrough(macHandle).Should().Be("new content");

        _writer.Write(_path, "newer content, now longer than before").Should().Be(SharedFileWriteOutcome.OverwrittenInPlace);
        ReadThrough(macHandle).Should().Be("newer content, now longer than before");

        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(_path);
    }

    [Fact]
    public void Reports_busy_without_throwing_when_the_reader_does_not_share_write_access()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(_path, "old");
        using var reader = HoldOpen(FileShare.Read);

        _writer.Write(_path, "new").Should().Be(SharedFileWriteOutcome.DestinationBusy);

        ReadThrough(reader).Should().Be("old");
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(_path);
    }

    [Fact]
    public void Still_throws_when_the_destination_cannot_be_written_at_all()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The read-only attribute blocks rename-over and open-for-write only on Windows.
        }

        File.WriteAllText(_path, "old");
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try
        {
            var write = () => _writer.Write(_path, "new");

            write.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetAttributes(_path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Logs_each_outcome_change_once_instead_of_on_every_write()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(_path, "old");

        using (HoldOpen(SmbClientShare))
        {
            WriteThreeTimes();
        }

        using (HoldOpen(FileShare.Read))
        {
            WriteThreeTimes();
        }

        WriteThreeTimes();

        _logger.Entries.Select(e => e.Level).Should().Equal(LogLevel.Information, LogLevel.Warning, LogLevel.Information);
        _logger.Entries.Should().OnlyContain(e => e.Exception == null);

        void WriteThreeTimes()
        {
            for (var i = 0; i < 3; i++)
            {
                _writer.Write(_path, $"content {i}");
            }
        }
    }

    /// <summary>Unbuffered, so every ReadThrough sees what is on disk now.</summary>
    private FileStream HoldOpen(FileShare share) =>
        new(_path, FileMode.Open, FileAccess.Read, share, bufferSize: 0);

    private static string ReadThrough(FileStream handle)
    {
        handle.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(handle, leaveOpen: true);
        return reader.ReadToEnd();
    }
}

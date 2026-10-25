using FluentAssertions;
using Microsoft.Extensions.Logging;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// The held-open cases are Windows-only: on Unix, rename(2) replaces an open
/// destination, so the POSIX-semantics fallback never runs there.
/// </summary>
public class PrivateSnapshotWriterTests : IDisposable
{
    /// <summary>The share mode of the SMB server's handle observed while the Mac held the file open.</summary>
    private const FileShare SmbClientShare = FileShare.ReadWrite | FileShare.Delete;

    private readonly string _tempDir;
    private readonly string _path;
    private readonly ListLogger<PrivateSnapshotWriterTests> _logger = new();
    private readonly PrivateSnapshotWriter _writer;

    public PrivateSnapshotWriterTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-sharedfile-").FullName;
        _path = Path.Combine(_tempDir, "vrchat-latest.log");
        _writer = new PrivateSnapshotWriter(_logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Replaces_the_destination_when_nothing_holds_it_open()
    {
        File.WriteAllText(_path, "old");

        _writer.Write(_path, "new").Should().Be(SnapshotWriteOutcome.Replaced);

        File.ReadAllText(_path).Should().Be("new");
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(_path);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Replaces_a_destination_held_open_the_way_the_Mac_SMB_client_holds_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(_path, "old content");
        using var macHandle = HoldOpen(SmbClientShare);

        _writer.Write(_path, "new content").Should().Be(SnapshotWriteOutcome.ReplacedWhileHeldOpen);

        File.ReadAllText(_path).Should().Be("new content", "a new open must see the new file");
        ReadThrough(macHandle).Should().Be(
            "old content", "the held handle keeps the old file intact rather than a half-rewritten one");
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(_path);

        // The name now refers to a file nobody holds, so the normal path works again.
        _writer.Write(_path, "newer content").Should().Be(SnapshotWriteOutcome.Replaced);
        File.ReadAllText(_path).Should().Be("newer content");
    }

    [Fact]
    public void Reports_busy_without_throwing_when_the_reader_does_not_share_delete_access()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(_path, "old");
        using var reader = HoldOpen(FileShare.ReadWrite);

        _writer.Write(_path, "new").Should().Be(SnapshotWriteOutcome.DestinationBusy);

        ReadThrough(reader).Should().Be("old");
        Directory.GetFiles(_tempDir).Should().ContainSingle().Which.Should().Be(_path);
    }

    [Fact]
    public void Still_throws_when_the_destination_is_read_only()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The read-only attribute blocks rename-over only on Windows.
        }

        File.WriteAllText(_path, "old");
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try
        {
            var write = () => _writer.Write(_path, "new");

            write.Should().Throw<UnauthorizedAccessException>();
            File.ReadAllText(_path).Should().Be("old");
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

        using (HoldOpen(FileShare.ReadWrite))
        {
            WriteThreeTimes();
        }

        WriteThreeTimes();

        using (HoldOpen(SmbClientShare))
        {
            _writer.Write(_path, "while held");
        }

        _logger.Entries.Select(e => e.Level).Should().Equal(LogLevel.Warning, LogLevel.Information, LogLevel.Information);
        _logger.Entries.Should().OnlyContain(e => e.Exception == null);

        void WriteThreeTimes()
        {
            for (var i = 0; i < 3; i++)
            {
                _writer.Write(_path, $"content {i}");
            }
        }
    }

    /// <summary>Unbuffered, so every ReadThrough sees what the handle refers to now.</summary>
    private FileStream HoldOpen(FileShare share) =>
        new(_path, FileMode.Open, FileAccess.Read, share, bufferSize: 0);

    private static string ReadThrough(FileStream handle)
    {
        handle.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(handle, leaveOpen: true);
        return reader.ReadToEnd();
    }
}

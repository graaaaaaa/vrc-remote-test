using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// The real Restart Manager, so these return at once when not on Windows (like
/// PrivateSnapshotWriterTests). On Windows they check what the rules in VrchatLogOwnersTests
/// take for granted: a process that holds a file open is reported with its PID, a start
/// time that matches Process.StartTime, and its image name, and is gone once it closes
/// the file.
/// </summary>
public class RestartManagerFileOwnersTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-rm-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private string NewFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, "line\n");
        return path;
    }

    /// <summary>VRChat keeps its output_log open for writing and lets others read it.</summary>
    private static FileStream HoldOpen(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    [Fact]
    public void Reports_this_process_while_it_holds_the_file_open_and_not_after()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = NewFile("held.txt");
        using var current = Process.GetCurrentProcess();

        using (HoldOpen(path))
        {
            var owner = RestartManagerFileOwners.Query(path).Should().ContainSingle().Which;
            owner.ProcessId.Should().Be(current.Id);
            owner.ProcessName.Should().Be(current.ProcessName);
            owner.StartTimeUtc.Should().NotBeNull();
            VrchatProcessMonitor.IsSameCreationTime(current.StartTime.ToUniversalTime(), owner.StartTimeUtc!.Value)
                .Should().BeTrue("the start time must identify the process as VrchatProcessMonitor reads it");
        }

        RestartManagerFileOwners.Query(path).Should().BeEmpty();
    }

    [Fact]
    public void Reports_every_process_that_holds_the_file_with_its_own_name()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = NewFile("shared.txt");
        using var current = Process.GetCurrentProcess();
        using var held = HoldOpen(path);
        using var other = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                $"$f = [System.IO.File]::Open('{path}', 'Open', 'Read', 'ReadWrite, Delete'); Start-Sleep -Seconds 60",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            IReadOnlyList<VrchatLogOwners.Owner> owners;
            do
            {
                Thread.Sleep(200);
                owners = RestartManagerFileOwners.Query(path);
            }
            while (owners.All(o => o.ProcessId != other.Id) && DateTime.UtcNow < deadline);

            owners.Select(o => o.ProcessId).Should().BeEquivalentTo([current.Id, other.Id]);
            owners.Single(o => o.ProcessId == other.Id).ProcessName.Should().BeEquivalentTo("powershell");
        }
        finally
        {
            other.Kill();
            other.WaitForExit();
        }
    }

    [Fact]
    public void The_reader_shows_the_log_its_client_holds_even_when_the_name_cannot_match()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // This test process stands in for the first VRChat client. The name is years away
        // from its start, so only Restart Manager can tie the file to it.
        var logDir = Path.Combine(_tempDir, "VRChat", "VRChat");
        var worldsDir = Path.Combine(logDir, "Worlds");
        Directory.CreateDirectory(worldsDir);
        var path = Path.Combine(logDir, "output_log_2020-01-01_00-00-00.txt");
        File.WriteAllText(path, "first client line\r\n");

        using var current = Process.GetCurrentProcess();
        var monitor = new FakeVrchatProcessMonitor();
        monitor.AddClient(current.Id, current.StartTime.ToUniversalTime());
        var reader = new VrchatLogReader(
            Options.Create(new BridgeOptions { VrchatWorldsDirectory = worldsDir }),
            monitor,
            NullLogger<VrchatLogReader>.Instance,
            new FakeManagedClientStore(),
            new VrchatClientRoster(filePath: null));

        using (HoldOpen(path))
        {
            reader.ReadSnapshot().Content.Should().Contain("first client line");
        }
    }
}

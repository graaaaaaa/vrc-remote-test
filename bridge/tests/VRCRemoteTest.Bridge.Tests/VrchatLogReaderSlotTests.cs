using System.ComponentModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VRCRemoteTest.Bridge.Configuration;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Client-observation plan F2: VrchatLogReaderTests' scenes with two to four clients, each
/// in its own slot, on real files with a fake process monitor and Restart Manager.
/// </summary>
public class VrchatLogReaderSlotTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 17, 6, 0, 0, 100, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly string _logDir;
    private readonly string _worldsDir;
    private readonly FakeVrchatProcessMonitor _monitor = new();
    private readonly CapturingLogger<VrchatLogReader> _logger = new();
    private readonly Dictionary<string, VrchatLogOwners.Owner[]> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _queried = [];

    /// <summary>File names the fake Restart Manager fails for.</summary>
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);

    public VrchatLogReaderSlotTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-logslots-").FullName;
        _logDir = Path.Combine(_tempDir, "VRChat", "VRChat");
        _worldsDir = Path.Combine(_logDir, "Worlds");
        Directory.CreateDirectory(_worldsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private VrchatLogReader CreateReader(bool withOwners = false, FakeManagedClientStore? record = null) =>
        new(
            Options.Create(new BridgeOptions
            {
                VrchatWorldsDirectory = _worldsDir,
                VrchatClientMode = record is null ? "Reload" : "Relaunch",
            }),
            _monitor,
            _logger,
            CreationTimeFromName,
            withOwners ? QueryOwners : null,
            record);

    private IReadOnlyList<VrchatLogOwners.Owner> QueryOwners(string path)
    {
        var name = Path.GetFileName(path);
        _queried.Add(name);
        if (_failing.Contains(name))
        {
            throw new Win32Exception(5);
        }

        return _owners.TryGetValue(name, out var owners) ? owners : [];
    }

    private void Open(string path, params int[] processIds) =>
        _owners[Path.GetFileName(path)] = processIds
            .Select(pid => new VrchatLogOwners.Owner(pid, _monitor.Clients.Single(c => c.ProcessId == pid).CreationTimeUtc, "VRChat"))
            .ToArray();

    private static DateTime CreationTimeFromName(string path) =>
        VrchatLogMatcher.ParseNameTime(Path.GetFileName(path)) is { } local
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local)
            : DateTime.MinValue;

    private string WriteLogFor(DateTime startedUtc, string content)
    {
        var path = Path.Combine(_logDir, $"output_log_{startedUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.txt");
        File.WriteAllText(path, content + "\r\n");
        return path;
    }

    private static string Content(VrchatLogReadResult read, int slot) => read.Slots[slot].Content.Content;

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Every_client_gets_its_own_slot_and_log(int count)
    {
        for (var i = 0; i < count; i++)
        {
            WriteLogFor(T0.AddSeconds(3 * i), $"client {i} line");
            _monitor.AddClient(100 + i, T0.AddSeconds(3 * i));
        }

        var read = CreateReader().Read();

        for (var i = 0; i < count; i++)
        {
            read.Slots[i].Status.Should().Be(SlotLogStatus.Shown);
            Content(read, i).Should().Contain($"client {i} line");
            for (var j = 0; j < count; j++)
            {
                if (j != i)
                {
                    Content(read, i).Should().NotContain($"client {j} line");
                }
            }
        }

        for (var i = count; i < 4; i++)
        {
            read.Slots[i].Status.Should().Be(SlotLogStatus.NotRunning);
            Content(read, i).Should().Contain($"No VRChat client is running in slot {i}");
        }

        read.Latest.Content.Should().Contain("client 0 line");
    }

    [Fact]
    public void A_mixed_log_is_refused_for_every_slot()
    {
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        var shared = WriteLogFor(T0.AddSeconds(30), "mixed line");
        Open(shared, 100, 200);

        var read = CreateReader(withOwners: true).Read();

        foreach (var slot in new[] { 0, 1 })
        {
            Content(read, slot).Should().Contain("is or was also open by other VRChat clients").And.NotContain("mixed line");
        }
    }

    [Fact]
    public void A_log_seen_mixed_through_one_slot_stays_refused_for_the_others_whatever_their_order()
    {
        // Slot 0 is the newer client (Relaunch record order); the file is mixed with the older one.
        var record = new FakeManagedClientStore
        {
            Clients = [new ManagedClient(100, T0.AddSeconds(30), 1, 0, 0), new ManagedClient(200, T0, 1, 1, 1)],
        };
        _monitor.AddClient(100, T0.AddSeconds(30), watchWorlds: false);
        _monitor.AddClient(200, T0, watchWorlds: false);
        var file = WriteLogFor(T0.AddSeconds(31), "mixed line");
        Open(file, 100, 200);
        var reader = CreateReader(withOwners: true, record);
        reader.Read();

        // The older client exits; the newer one now has the file to itself.
        _monitor.Clients.RemoveAll(c => c.ProcessId == 200);
        Open(file, 100);

        Content(reader.Read(), 0).Should().Contain("is or was also open by other VRChat clients").And.NotContain("mixed line");
    }

    [Fact]
    public void One_client_closing_does_not_release_another_slots_log()
    {
        WriteLogFor(T0, "first client line");
        var second = WriteLogFor(T0.AddSeconds(3), "second client line");
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        var reader = CreateReader();
        reader.Read();

        _monitor.Clients.RemoveAt(0);
        // A fresh name match for slot 1 would now be ambiguous; its pinned file keeps being read.
        WriteLogFor(T0.AddSeconds(4), "later line");
        var read = reader.Read();

        read.Slots[0].Status.Should().Be(SlotLogStatus.NotRunning);
        Content(read, 0).Should().Contain("The VRChat client in slot 0 (PID 100) is not running");
        read.Slots[1].Path.Should().Be(second);
        Content(read, 1).Should().Contain("second client line");
        read.Latest.Content.Should().Contain("second client line", "vrchat-latest.log moves to the next client as before");
    }

    [Fact]
    public void Each_file_is_asked_about_once_per_poll_however_many_slots_ask()
    {
        var paths = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            _monitor.AddClient(100 + i, T0.AddSeconds(3 * i));
            paths.Add(WriteLogFor(T0.AddSeconds(3 * i), $"client {i} line"));
            Open(paths[^1], 100 + i);
        }

        var read = CreateReader(withOwners: true).Read();

        _queried.Should().OnlyHaveUniqueItems().And.HaveCount(4);
        Enumerable.Range(0, 4).Should().OnlyContain(i => read.Slots[i].Path == paths[i]);
    }

    [Fact]
    public void A_failed_question_about_another_slots_file_does_not_spread()
    {
        // Slot 0's client started five minutes after slot 1's, so it only asks about its own file.
        var record = new FakeManagedClientStore
        {
            Clients = [new ManagedClient(100, T0.AddMinutes(5), 1, 0, 0), new ManagedClient(200, T0, 1, 1, 1)],
        };
        _monitor.AddClient(100, T0.AddMinutes(5), watchWorlds: false);
        _monitor.AddClient(200, T0, watchWorlds: false);
        var first = WriteLogFor(T0.AddMinutes(5), "first client line");
        var second = WriteLogFor(T0, "second client line");
        Open(first, 100);
        Open(second, 200);
        _failing.Add(Path.GetFileName(second));

        var read = CreateReader(withOwners: true, record).Read();

        Content(read, 0).Should().Contain("first client line");
        _logger.MessagesAt(LogLevel.Information).Should().Contain(m =>
            m.Contains("as the log of VRChat client 100 in slot 0") && m.Contains("the only VRChat process that has it open"));
        Content(read, 1).Should().Contain("second client line", "slot 1 falls back to the names");
        _logger.MessagesAt(LogLevel.Information).Should().Contain(m =>
            m.Contains("as the log of VRChat client 200 in slot 1") && m.Contains("matched by the time in its name"));
    }

    [Fact]
    public void A_client_that_cannot_be_checked_keeps_its_slot_and_says_so()
    {
        WriteLogFor(T0, "first client line");
        WriteLogFor(T0.AddSeconds(3), "second client line");
        _monitor.AddClient(100, T0);
        _monitor.AddClient(200, T0.AddSeconds(3));
        var reader = CreateReader();
        reader.Read();

        _monitor.Clients.RemoveAt(0);
        _monitor.UnverifiedCount = 1;
        var read = reader.Read();

        read.Slots[0].Status.Should().Be(SlotLogStatus.CannotCheck);
        Content(read, 0).Should().Contain("could not be checked");
        Content(read, 1).Should().Contain("second client line");
        read.Latest.Content.Should().Contain("could not be checked", "vrchat-latest.log stays on its client as before");
    }
}

using System.Text;
using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Real filesystem tests for managed-clients.json (multi-client plan section 13.3).</summary>
public class ManagedClientStoreTests : IDisposable
{
    private readonly string _stateDir;
    private readonly ManagedClientStore _store;

    /// <summary>Ticks that no millisecond (or microsecond) rounding would keep.</summary>
    private static readonly DateTime Created = new DateTime(2026, 10, 17, 6, 41, 7, DateTimeKind.Utc).AddTicks(3_505_842);

    public ManagedClientStoreTests()
    {
        _stateDir = Directory.CreateTempSubdirectory("vrc-remote-test-managed-").FullName;
        _store = new ManagedClientStore(_stateDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_stateDir, recursive: true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_stateDir, ManagedClientStore.FileName);

    private static ManagedClient Client(int processId = 31832, int profile = 0, int launchOrder = 0) =>
        new(processId, Created, SessionId: 1, profile, launchOrder);

    private void WriteFile(string json) => File.WriteAllText(FilePath, json, new UTF8Encoding(false));

    private static string Entry(
        int processId = 31832,
        string creationTimeUtc = "\"2026-10-17T06:41:07.3505842Z\"",
        int sessionId = 1,
        int profile = 0,
        int launchOrder = 0) =>
        $$"""{ "processId": {{processId}}, "creationTimeUtc": {{creationTimeUtc}}, "sessionId": {{sessionId}}, "profile": {{profile}}, "launchOrder": {{launchOrder}} }""";

    private static string FileWith(params string[] entries) =>
        $$"""{ "schemaVersion": 1, "clients": [ {{string.Join(", ", entries)}} ] }""";

    [Fact]
    public void A_missing_file_is_the_first_use_with_no_clients()
    {
        var result = _store.Load();

        result.Status.Should().Be(ManagedClientLoadStatus.Missing);
        result.Clients.Should().BeEmpty();
    }

    [Fact]
    public void Round_trips_every_field_with_the_creation_time_at_full_precision()
    {
        var clients = new[] { Client(31832, profile: 0, launchOrder: 0), Client(31900, profile: 7, launchOrder: 1) };

        _store.Save(clients);
        var result = new ManagedClientStore(_stateDir).Load();

        result.Status.Should().Be(ManagedClientLoadStatus.Loaded);
        result.Clients.Should().Equal(clients);
        result.Clients[0].CreationTimeUtc.Ticks.Should().Be(Created.Ticks);
        result.Clients[0].CreationTimeUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Writes_the_schema_version_and_utc_times_with_seven_fractional_digits()
    {
        _store.Save([Client()]);

        var json = File.ReadAllText(FilePath);
        json.Should().Contain("\"schemaVersion\": 1");
        json.Should().Contain("\"creationTimeUtc\": \"2026-10-17T06:41:07.3505842Z\"");
    }

    [Fact]
    public void Saving_replaces_the_file_and_leaves_no_temporary_file()
    {
        _store.Save([Client(1), Client(2, profile: 1, launchOrder: 1)]);
        _store.Save([]);

        _store.Load().Clients.Should().BeEmpty();
        Directory.GetFiles(_stateDir).Should().ContainSingle().Which.Should().Be(FilePath);
    }

    [Fact]
    public void A_failed_save_throws_and_keeps_the_previous_file()
    {
        _store.Save([Client()]);
        Directory.CreateDirectory(FilePath + ".tmp"); // the temporary file cannot be created

        var act = () => _store.Save([]);

        act.Should().Throw<Exception>().Where(e => e is IOException || e is UnauthorizedAccessException);
        new ManagedClientStore(_stateDir).Load().Clients.Should().ContainSingle();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "schemaVersion": 2, "clients": [] }""")]
    [InlineData("""{ "clients": [] }""")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "schemaVersion": 1, "clients": null }""")]
    [InlineData("""{ "schemaVersion": 1, "clients": [ null ] }""")]
    [InlineData("""{ "schemaVersion": 1, "clients": [], "extra": true }""")]
    [InlineData("""{ "schemaVersion": "1", "clients": [] }""")]
    public void A_file_that_is_not_a_record_of_this_schema_is_corrupt(string json)
    {
        WriteFile(json);

        var result = _store.Load();

        result.Status.Should().Be(ManagedClientLoadStatus.Corrupt);
        result.Clients.Should().BeEmpty();
        result.Problem.Should().NotBeNullOrEmpty();
    }

    public static TheoryData<string> CorruptEntries => new()
    {
        Entry(processId: 0),
        Entry(processId: -5),
        Entry(sessionId: -1),
        Entry(profile: 100),
        Entry(profile: -1),
        Entry(launchOrder: 4),
        Entry(launchOrder: -1),
        Entry(creationTimeUtc: "null"),
        Entry(creationTimeUtc: "\"2026-10-17T06:41:07.350Z\""),
        Entry(creationTimeUtc: "\"2026-10-17T06:41:07.3505842+09:00\""),
        Entry(creationTimeUtc: "\"2026-10-17 06:41:07\""),
        Entry(creationTimeUtc: "133420000000000000"),
        """{ "processId": 1, "creationTimeUtc": "2026-10-17T06:41:07.3505842Z", "sessionId": 1, "profile": 0 }""",
        """{ "processId": 1, "creationTimeUtc": "2026-10-17T06:41:07.3505842Z", "sessionId": 1, "profile": 0, "launchOrder": 0, "name": "x" }""",
    };

    [Theory]
    [MemberData(nameof(CorruptEntries))]
    public void An_entry_with_a_missing_unknown_or_out_of_range_value_makes_the_file_corrupt(string entry)
    {
        WriteFile(FileWith(entry));

        _store.Load().Status.Should().Be(ManagedClientLoadStatus.Corrupt);
    }

    [Fact]
    public void More_than_four_clients_is_corrupt()
    {
        WriteFile(FileWith(Enumerable.Range(1, 5).Select(i => Entry(processId: i, launchOrder: i % 4)).ToArray()));

        _store.Load().Status.Should().Be(ManagedClientLoadStatus.Corrupt);
    }

    [Fact]
    public void A_PID_listed_twice_is_corrupt()
    {
        WriteFile(FileWith(Entry(processId: 7, launchOrder: 0), Entry(processId: 7, launchOrder: 1)));

        _store.Load().Status.Should().Be(ManagedClientLoadStatus.Corrupt);
    }

    [Fact]
    public void A_launch_order_listed_twice_is_corrupt()
    {
        WriteFile(FileWith(Entry(processId: 7, launchOrder: 0), Entry(processId: 8, launchOrder: 0)));

        _store.Load().Status.Should().Be(ManagedClientLoadStatus.Corrupt);
    }

    [Fact]
    public void A_file_over_the_size_cap_is_corrupt_without_being_parsed()
    {
        WriteFile(new string(' ', ManagedClientStore.MaxFileBytes) + FileWith());

        _store.Load().Status.Should().Be(ManagedClientLoadStatus.Corrupt);
    }

    [Fact]
    public void A_file_written_by_hand_in_the_same_format_loads()
    {
        WriteFile(FileWith(Entry(processId: 11, profile: 0, launchOrder: 0), Entry(processId: 12, profile: 3, launchOrder: 1)));

        var result = _store.Load();

        result.Status.Should().Be(ManagedClientLoadStatus.Loaded);
        result.Clients.Select(c => (c.ProcessId, c.Profile, c.LaunchOrder)).Should().Equal((11, 0, 0), (12, 3, 1));
    }

    [Fact]
    public void A_byte_order_mark_from_an_editor_is_tolerated()
    {
        File.WriteAllText(FilePath, FileWith(Entry(processId: 11)), new UTF8Encoding(true));

        _store.Load().Clients.Should().ContainSingle().Which.ProcessId.Should().Be(11);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_unreadable_rather_than_empty()
    {
        // A directory in its place fails the open on every OS, as an access error would.
        Directory.CreateDirectory(FilePath);

        var result = _store.Load();

        result.Status.Should().Be(ManagedClientLoadStatus.Unreadable);
        result.Problem.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Known_reads_the_file_once_and_then_follows_saves()
    {
        WriteFile(FileWith(Entry(processId: 11)));

        _store.Known.Should().ContainSingle().Which.ProcessId.Should().Be(11);

        _store.Save([Client(22)]);
        File.Delete(FilePath);

        _store.Known.Should().ContainSingle().Which.ProcessId.Should().Be(22, "saves update it without rereading the file");
    }

    [Fact]
    public void Known_is_empty_when_the_file_is_corrupt_or_unreadable()
    {
        WriteFile("not json");
        new ManagedClientStore(_stateDir).Known.Should().BeEmpty();

        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);
        new ManagedClientStore(_stateDir).Known.Should().BeEmpty();
    }
}

using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>
/// Pure tests of the client-to-output_log matching rules (multi-client plan section
/// 13.2), with explicit time zones and file creation times so they do not depend on the
/// machine's time zone or on whether its filesystem records creation times.
/// </summary>
public class VrchatLogMatcherTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>A fixed +09:00 zone without daylight saving time.</summary>
    private static readonly TimeZoneInfo Jst =
        TimeZoneInfo.CreateCustomTimeZone("Test/JST", TimeSpan.FromHours(9), "Test JST", "Test JST");

    /// <summary>UTC+0 with DST from 30 March 02:00 (clocks jump to 03:00) to 26 October 03:00 (back to 02:00).</summary>
    private static readonly TimeZoneInfo Dst = TimeZoneInfo.CreateCustomTimeZone(
        "Test/DST",
        TimeSpan.Zero,
        "Test DST",
        "Test Standard",
        "Test Daylight",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1),
                new DateTime(2099, 12, 31),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 30),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 26)),
        ]);

    private static VrchatClientInfo Client(int pid, DateTime? createdUtc, bool watchWorlds = true) =>
        new(pid, createdUtc, watchWorlds);

    /// <summary>A file named after <paramref name="nameUtc"/> in <paramref name="zone"/>, created at the same instant unless given.</summary>
    private static VrchatLogMatcher.Candidate File(DateTime nameUtc, TimeZoneInfo zone, DateTime? createdUtc = null)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(nameUtc, zone);
        var name = $"output_log_{local:yyyy-MM-dd_HH-mm-ss}.txt";
        return new VrchatLogMatcher.Candidate(name, VrchatLogMatcher.ParseNameTime(name), createdUtc ?? nameUtc);
    }

    [Fact]
    public void Matches_the_file_named_at_the_clients_start_second()
    {
        var client = Client(1, T0.AddMilliseconds(400));
        var file = File(T0, TimeZoneInfo.Utc, createdUtc: T0.AddMilliseconds(600));

        VrchatLogMatcher.Match(client, [client], [file], TimeZoneInfo.Utc).Should().Be(file.Path);
    }

    [Fact]
    public void Reads_the_file_name_in_the_given_time_zone()
    {
        var client = Client(1, T0.AddMilliseconds(400));
        var file = File(T0, Jst);

        file.Path.Should().Be("output_log_2026-10-17_15-00-00.txt");
        VrchatLogMatcher.Match(client, [client], [file], Jst).Should().Be(file.Path);
        VrchatLogMatcher.Match(client, [client], [file], TimeZoneInfo.Utc).Should().BeNull(
            "read as UTC, the name is nine hours away from the creation time");
    }

    [Fact]
    public void Picks_the_first_clients_own_file_when_clients_start_seconds_apart()
    {
        var first = Client(1, T0.AddMilliseconds(100));
        var second = Client(2, T0.AddSeconds(3).AddMilliseconds(100));
        var firstFile = File(T0, TimeZoneInfo.Utc);
        var secondFile = File(T0.AddSeconds(3), TimeZoneInfo.Utc);
        var files = new[] { secondFile, firstFile };

        VrchatLogMatcher.Match(first, [first, second], files, TimeZoneInfo.Utc).Should().Be(firstFile.Path);
        VrchatLogMatcher.Match(second, [first, second], files, TimeZoneInfo.Utc).Should().Be(secondFile.Path);
    }

    [Fact]
    public void Clients_started_in_the_same_second_share_a_file_and_are_not_matched()
    {
        var first = Client(1, T0.AddMilliseconds(100));
        var second = Client(2, T0.AddMilliseconds(700));
        var shared = File(T0, TimeZoneInfo.Utc);

        VrchatLogMatcher.Match(first, [first, second], [shared], TimeZoneInfo.Utc).Should().BeNull();
    }

    [Fact]
    public void Two_files_in_the_clients_range_are_not_matched()
    {
        var client = Client(1, T0.AddMilliseconds(100));
        var files = new[] { File(T0, TimeZoneInfo.Utc), File(T0.AddSeconds(1), TimeZoneInfo.Utc) };

        VrchatLogMatcher.Match(client, [client], files, TimeZoneInfo.Utc).Should().BeNull();
    }

    [Fact]
    public void A_file_that_also_falls_in_an_older_clients_range_is_not_matched()
    {
        // The older client (without --watch-worlds) started 0.3 s earlier, in the previous
        // second; its range runs up to the first client's start second, which the file is
        // named before.
        var older = Client(1, T0.AddMilliseconds(900), watchWorlds: false);
        var first = Client(2, T0.AddSeconds(1).AddMilliseconds(200));
        var file = File(T0, TimeZoneInfo.Utc);

        VrchatLogMatcher.Match(first, [older, first], [file], TimeZoneInfo.Utc).Should().BeNull();
    }

    [Fact]
    public void Another_client_without_a_creation_time_prevents_a_match()
    {
        var first = Client(1, T0.AddMilliseconds(100));
        var unknown = Client(2, null, watchWorlds: false);

        VrchatLogMatcher.Match(first, [first, unknown], [File(T0, TimeZoneInfo.Utc)], TimeZoneInfo.Utc).Should().BeNull();
    }

    [Fact]
    public void A_client_without_a_creation_time_is_not_matched()
    {
        var client = Client(1, null);

        VrchatLogMatcher.Match(client, [client], [File(T0, TimeZoneInfo.Utc)], TimeZoneInfo.Utc).Should().BeNull();
    }

    [Theory]
    [InlineData(-10, true)]
    [InlineData(10, true)]
    [InlineData(-11, false)]
    [InlineData(11, false)]
    public void A_file_whose_name_disagrees_with_its_creation_time_by_more_than_ten_seconds_is_not_used(
        int skewSeconds, bool used)
    {
        var client = Client(1, T0.AddMilliseconds(100));
        var file = File(T0, TimeZoneInfo.Utc, createdUtc: T0.AddSeconds(skewSeconds));

        var match = VrchatLogMatcher.Match(client, [client], [file], TimeZoneInfo.Utc);

        (match is not null).Should().Be(used);
    }

    [Fact]
    public void A_skewed_file_is_ignored_rather_than_making_the_match_ambiguous()
    {
        var client = Client(1, T0.AddMilliseconds(100));
        var good = File(T0, TimeZoneInfo.Utc);
        var skewed = File(T0.AddSeconds(1), TimeZoneInfo.Utc, createdUtc: T0.AddHours(-1));

        VrchatLogMatcher.Match(client, [client], [skewed, good], TimeZoneInfo.Utc).Should().Be(good.Path);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(-2, false)]
    [InlineData(59, true)]
    [InlineData(61, false)]
    public void The_range_starts_one_second_early_and_lasts_at_most_sixty_seconds(int offsetSeconds, bool matched)
    {
        var client = Client(1, T0.AddMilliseconds(100));
        var file = File(T0.AddSeconds(offsetSeconds), TimeZoneInfo.Utc);

        var match = VrchatLogMatcher.Match(client, [client], [file], TimeZoneInfo.Utc);

        (match is not null).Should().Be(matched);
    }

    [Fact]
    public void Evaluate_tells_a_file_not_created_yet_from_one_it_cannot_attribute()
    {
        var first = Client(1, T0.AddMilliseconds(100));
        var second = Client(2, T0.AddMilliseconds(700));
        var shared = File(T0, TimeZoneInfo.Utc);
        var earlierSession = File(T0.AddMinutes(-5), TimeZoneInfo.Utc);

        VrchatLogMatcher.Evaluate(first, [first], [earlierSession], TimeZoneInfo.Utc).Failure
            .Should().Be(VrchatLogMatcher.MatchFailure.NoFileYet);
        VrchatLogMatcher.Evaluate(first, [first, second], [shared], TimeZoneInfo.Utc).Failure
            .Should().Be(VrchatLogMatcher.MatchFailure.NoFileInRange, "the shared file is named in the second's range only");
        VrchatLogMatcher.Evaluate(first, [first], [shared, File(T0.AddSeconds(1), TimeZoneInfo.Utc)], TimeZoneInfo.Utc)
            .Failure.Should().Be(VrchatLogMatcher.MatchFailure.Ambiguous);
        VrchatLogMatcher.Evaluate(Client(3, null), [first], [shared], TimeZoneInfo.Utc).Failure
            .Should().Be(VrchatLogMatcher.MatchFailure.StartTimeUnknown);
        VrchatLogMatcher.Evaluate(first, [first], [shared], TimeZoneInfo.Utc)
            .Should().Be(new VrchatLogMatcher.MatchResult(shared.Path, VrchatLogMatcher.MatchFailure.None));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(999, true)]
    [InlineData(-1, false)]
    public void A_client_started_at_EarliestNextStartUtc_or_later_leaves_the_earlier_file_to_the_earlier_client(
        int offsetMilliseconds, bool matched)
    {
        var first = Client(1, T0.AddMilliseconds(100));
        var file = File(T0.AddSeconds(20), TimeZoneInfo.Utc, createdUtc: T0.AddSeconds(20.4));
        var nextStart = VrchatLogMatcher.EarliestNextStartUtc(T0.AddSeconds(20)).AddMilliseconds(offsetMilliseconds);
        var second = Client(2, nextStart);

        var match = VrchatLogMatcher.Match(first, [first, second], [file], TimeZoneInfo.Utc);

        (match is not null).Should().Be(matched);
    }

    /// <summary>Times from docs/validation/multi-client-spike.md (PR #2, item 12), read as UTC.</summary>
    private static DateTime At(int minute, double seconds) =>
        new DateTime(2026, 10, 17, 18, minute, 0, DateTimeKind.Utc).AddSeconds(seconds);

    [Fact]
    public void Hardware_run_1_under_load_without_waiting_for_logs_leaves_the_first_client_unmatched()
    {
        // 4 clients launched without waiting; the first two logs were both created at 18:04:11.
        List<VrchatClientInfo> clients =
        [
            Client(1, At(3, 59.922)), Client(2, At(4, 7.608)), Client(3, At(4, 13.077)), Client(4, At(4, 16.093)),
        ];
        var files = new[]
        {
            File(At(4, 10), TimeZoneInfo.Utc, createdUtc: At(4, 11.5)),
            File(At(4, 11), TimeZoneInfo.Utc, createdUtc: At(4, 11.5)),
            File(At(4, 13), TimeZoneInfo.Utc, createdUtc: At(4, 13.8)),
            File(At(4, 16), TimeZoneInfo.Utc, createdUtc: At(4, 16.85)),
        };

        VrchatLogMatcher.Evaluate(clients[0], clients, files, TimeZoneInfo.Utc).Failure
            .Should().Be(VrchatLogMatcher.MatchFailure.NoFileInRange, "client 1's log is named after client 2 started");
    }

    [Fact]
    public void Hardware_run_2_under_load_without_waiting_for_logs_shares_one_file_that_the_first_client_never_gets()
    {
        // All 4 clients wrote to output_log_..._18-05-24.txt, created 22 s after client 1 started.
        List<VrchatClientInfo> clients =
        [
            Client(1, At(5, 2.222)), Client(2, At(5, 7.280)), Client(3, At(5, 14.659)), Client(4, At(5, 22.254)),
        ];
        var shared = File(At(5, 24), TimeZoneInfo.Utc, createdUtc: At(5, 24.442));

        foreach (var client in clients.Take(3))
        {
            VrchatLogMatcher.Match(client, clients, [shared], TimeZoneInfo.Utc).Should().BeNull();
        }

        // A known limit: named after the last client's start, the shared file looks like
        // that client's own log. Waiting for each log before the next launch prevents it.
        VrchatLogMatcher.Match(clients[3], clients, [shared], TimeZoneInfo.Utc).Should().Be(shared.Path);
    }

    [Fact]
    public void A_file_named_in_a_skipped_daylight_saving_hour_is_not_used()
    {
        // 02:30 local on 30 March never happened in the test zone.
        var client = Client(1, new DateTime(2026, 3, 30, 2, 30, 0, DateTimeKind.Utc));
        const string name = "output_log_2026-03-30_02-30-00.txt";
        var file = new VrchatLogMatcher.Candidate(name, VrchatLogMatcher.ParseNameTime(name), client.CreationTimeUtc!.Value);

        VrchatLogMatcher.Match(client, [client], [file], Dst).Should().BeNull();
    }

    [Fact]
    public void A_file_named_in_a_repeated_daylight_saving_hour_is_not_used()
    {
        // 02:30 local on 26 October happened twice in the test zone (01:30Z and 02:30Z).
        var client = Client(1, new DateTime(2026, 10, 26, 1, 30, 0, DateTimeKind.Utc));
        const string name = "output_log_2026-10-26_02-30-00.txt";
        var file = new VrchatLogMatcher.Candidate(name, VrchatLogMatcher.ParseNameTime(name), client.CreationTimeUtc!.Value);

        VrchatLogMatcher.Match(client, [client], [file], Dst).Should().BeNull();
    }

    [Fact]
    public void Daylight_saving_time_is_applied_outside_the_transitions()
    {
        var client = Client(1, new DateTime(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc));
        const string name = "output_log_2026-07-01_12-00-00.txt"; // UTC+1 in summer
        var file = new VrchatLogMatcher.Candidate(name, VrchatLogMatcher.ParseNameTime(name), client.CreationTimeUtc!.Value);

        VrchatLogMatcher.Match(client, [client], [file], Dst).Should().Be(name);
    }

    [Theory]
    [InlineData("output_log_2026-10-17_13-46-59.txt", true)]
    [InlineData("output_log_2026-13-17_13-46-59.txt", false)]
    [InlineData("output_log_test_0001.txt", false)]
    [InlineData("output_log_2026-10-17_13-46-59.txt.bak", false)]
    [InlineData("Player.log", false)]
    public void ParseNameTime_accepts_only_VRChats_file_name_format(string fileName, bool parsed)
    {
        (VrchatLogMatcher.ParseNameTime(fileName) is not null).Should().Be(parsed);
    }
}

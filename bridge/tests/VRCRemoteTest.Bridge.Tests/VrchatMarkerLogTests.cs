using System.Text;
using System.Text.Json;
using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Client-observation plan F3: which lines are markers, and the file's shape and limits.</summary>
public class VrchatMarkerLogTests
{
    private const string Header = "2026.10.18 13:41:41 Debug      -  ";

    private readonly VrchatMarkerLog _markers = new(["[VRCRT]", "[SPK003]"]);
    private long _seq;

    private void Line(string text, bool truncated = false) =>
        _markers.OnLine(new VrchatLogPhysicalLine(++_seq, text, truncated));

    private string[] Records() =>
        _markers.Render(new MarkerFileHeader { Slot = 1, ClientGeneration = 7, ObservationEpoch = 3 })
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[1..];

    [Fact]
    public void Collects_lines_whose_message_starts_with_a_prefix()
    {
        Line(Header + "[Behaviour] Joining local:x");
        Line(Header + "[SPK003] PLAYER_RESTORED id=1 local=True");
        Line(Header + "text [VRCRT] not at the start");
        Line("    [VRCRT] a continuation is never a marker on its own");
        Line(Header + "[VRCRT] S5 done after 6 rounds");

        Records().Should().Equal(
            "2\t" + Header + "[SPK003] PLAYER_RESTORED id=1 local=True",
            "5\t" + Header + "[VRCRT] S5 done after 6 rounds");
    }

    [Fact]
    public void Lines_without_a_header_after_a_marker_join_its_record()
    {
        Line(Header + "[VRCRT] multi\nline");
        Line("third");
        Line(string.Empty);
        Line(Header + "[Behaviour] other");
        Line("not this one");

        Records().Should().Equal("1\t" + Header + @"[VRCRT] multi\nline\nthird");
    }

    [Fact]
    public void Escapes_control_characters_and_masks_before_cutting()
    {
        Line(Header + "[VRCRT] tab\there esc\u001b[31m back\\slash \u202eRTL (usr_0123abcd-0000-4000-8000-0123456789ab)");

        Records().Single().Should().Be(
            "1\t" + Header + "[VRCRT] tab\\there esc\\u001b[31m back\\\\slash \\u202eRTL (usr_***)");
    }

    [Fact]
    public void A_record_is_at_most_4_KiB_with_its_cut_marked()
    {
        Line(Header + "[VRCRT] " + new string('é', 5000));
        Line(Header + "[VRCRT] kept start of a long line", truncated: true);

        var records = Records();
        Encoding.UTF8.GetByteCount(records[0]).Should().BeLessOrEqualTo(VrchatMarkerLog.MaxRecordBytes);
        records[0].Should().EndWith(VrchatMarkerLog.TruncationMark);
        records[1].Should().EndWith("kept start of a long line" + VrchatMarkerLog.TruncationMark);
    }

    [Fact]
    public void Keeps_the_newest_records_and_says_how_many_were_dropped()
    {
        for (var i = 0; i < 2_500; i++)
        {
            Line(Header + $"[VRCRT] marker {i}");
        }

        var content = _markers.Render(new MarkerFileHeader { Slot = 0, ClientGeneration = 7, ObservationEpoch = 3 });
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(VrchatMarkerLog.MaxLines);
        lines[^1].Should().EndWith("marker 2499");
        using var header = JsonDocument.Parse(lines[0][VrchatMarkerLog.HeaderPrefix.Length..]);
        _markers.DroppedRecords.Should().Be(501);
        _markers.DroppedThroughSeq.Should().Be(501);
        header.RootElement.GetProperty("clientGeneration").GetInt64().Should().Be(7);
        header.RootElement.GetProperty("observationEpoch").GetInt64().Should().Be(3);
    }

    [Fact]
    public void The_file_stays_under_1_MiB()
    {
        for (var i = 0; i < 1_000; i++)
        {
            Line(Header + "[VRCRT] " + new string('x', 3_000));
        }

        var content = _markers.Render(new MarkerFileHeader());

        Encoding.UTF8.GetByteCount(content).Should().BeLessOrEqualTo(VrchatMarkerLog.MaxFileBytes);
        _markers.DroppedRecords.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Only_the_open_record_keeps_unmasked_text_and_only_up_to_a_cap()
    {
        Line(Header + "[VRCRT] token=" + new string('s', 60_000));
        _markers.RetainedRawChars.Should().Be(0, "a line over the cap is formatted and released at once");

        Line(Header + "[VRCRT] open");
        for (var i = 0; i < 100; i++)
        {
            Line(new string('c', 1_000));
        }

        _markers.RetainedRawChars.Should().Be(0, "past the cap the record is closed");
        Records().Should().HaveCount(2).And.OnlyContain(r => r.EndsWith(VrchatMarkerLog.TruncationMark));

        Line(Header + "[VRCRT] another");
        Line("continued");
        _markers.RetainedRawChars.Should().BeGreaterThan(0);
        Line(Header + "[Behaviour] a header closes it");
        _markers.RetainedRawChars.Should().Be(0);
    }

    [Fact]
    public void A_masked_value_is_never_shown_in_part()
    {
        Line(Header + "[VRCRT] token=" + new string('s', 60_000));

        Records().Single().Should().NotContain("sss");
    }

    [Fact]
    public void The_file_stays_under_1_MiB_with_the_longest_header()
    {
        // 8 prefixes of 32 characters that JSON escapes to 6 bytes each.
        var prefixes = Enumerable.Range(0, 8).Select(i => new string((char)('\u3042' + i), 32)).ToArray();
        var markers = new VrchatMarkerLog(prefixes);
        for (var i = 0; i < 1_000; i++)
        {
            markers.OnLine(new VrchatLogPhysicalLine(i + 1, Header + prefixes[0] + new string('x', 3_000), false));
        }

        var content = markers.Render(new MarkerFileHeader
        {
            ClientGeneration = long.MaxValue,
            ObservationEpoch = long.MaxValue,
            DroppedRecords = long.MaxValue,
            DroppedThroughSeq = long.MaxValue,
            Prefixes = prefixes,
        });

        Encoding.UTF8.GetByteCount(content).Should().BeLessOrEqualTo(VrchatMarkerLog.MaxFileBytes);
    }
}

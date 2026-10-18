using System.Text;
using FluentAssertions;
using VRCRemoteTest.Bridge.VRChat;
using Xunit;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Client-observation plan F3: reading what was appended, on real files.</summary>
public class VrchatLogScannerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("vrc-remote-test-scanner-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private string PathOf(string name) => Path.Combine(_tempDir, name);

    private static void Append(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }

    private static void Append(string path, string text) => Append(path, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Reads_only_what_was_appended_and_keeps_an_unfinished_line_for_later()
    {
        var path = PathOf("a.txt");
        File.WriteAllText(path, "one\r\ntw");
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);

        var first = scanner.Read(1024, CancellationToken.None);
        Append(path, "o\r\nthree\r\n");
        var second = scanner.Read(1024, CancellationToken.None);

        first.Lines.Select(l => (l.Seq, l.Text)).Should().Equal((1L, "one"));
        second.Lines.Select(l => (l.Seq, l.Text)).Should().Equal((2L, "two"), (3L, "three"));
        second.ReachedEnd.Should().BeTrue();
    }

    [Fact]
    public void A_CRLF_split_by_a_read_boundary_still_ends_the_line()
    {
        var path = PathOf("a.txt");
        File.WriteAllText(path, "one\r");
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);

        scanner.Read(1024, CancellationToken.None).Lines.Should().BeEmpty();
        Append(path, "\nlone\nlf\r\n");

        scanner.Read(1024, CancellationToken.None).Lines.Select(l => l.Text).Should().Equal("one", "lone\nlf");
    }

    [Fact]
    public void A_character_split_by_a_read_boundary_is_decoded_whole()
    {
        var path = PathOf("a.txt");
        var emoji = Encoding.UTF8.GetBytes("🎮日本\r\n");
        File.WriteAllBytes(path, emoji[..2]);
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);

        scanner.Read(1024, CancellationToken.None);
        Append(path, emoji[2..5]);
        scanner.Read(1024, CancellationToken.None);
        Append(path, emoji[5..]);

        scanner.Read(1024, CancellationToken.None).Lines.Single().Text.Should().Be("🎮日本");
    }

    [Fact]
    public void A_line_longer_than_64_KiB_keeps_its_first_4_KiB_and_is_marked()
    {
        // S5b F: a world wrote a single 65,593-byte line.
        var path = PathOf("a.txt");
        var longLine = new string('a', 4000) + " key=" + new string('x', 70_000);
        File.WriteAllText(path, longLine + "\r\nnext\r\n");
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);

        var lines = scanner.Read(1024 * 1024, CancellationToken.None).Lines;

        lines.Should().HaveCount(2);
        lines[0].Truncated.Should().BeTrue();
        lines[0].Text.Should().Be(new string('a', 4000), "it is cut back to the last space so half a value is not kept");
        lines[1].Should().Be(new VrchatLogPhysicalLine(2, "next", false));
        scanner.TruncatedLines.Should().Be(1);
    }

    [Fact]
    public void A_truncated_line_is_cut_at_a_character_boundary()
    {
        var path = PathOf("a.txt");
        File.WriteAllText(path, new string('あ', 30_000) + "\r\n");
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);

        var line = scanner.Read(1024 * 1024, CancellationToken.None).Lines.Single();

        line.Truncated.Should().BeTrue();
        line.Text.Should().NotContain("�").And.MatchRegex("^あ+$");
        Encoding.UTF8.GetByteCount(line.Text).Should().BeLessOrEqualTo(VrchatLogScanner.TruncatedKeepBytes);
    }

    [Fact]
    public void Stops_at_the_budget_and_carries_on_from_there()
    {
        var path = PathOf("a.txt");
        var text = string.Concat(Enumerable.Range(0, 1000).Select(i => $"line {i:D4}\r\n"));
        File.WriteAllText(path, text);
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);

        var first = scanner.Read(4000, CancellationToken.None);
        var rest = scanner.Read(1024 * 1024, CancellationToken.None);

        first.BytesRead.Should().Be(4000);
        first.ReachedEnd.Should().BeFalse();
        rest.ReachedEnd.Should().BeTrue();
        first.Lines.Concat(rest.Lines).Select(l => l.Seq).Should().Equal(Enumerable.Range(1, 1000).Select(i => (long)i));
    }

    [Fact]
    public void Reports_a_file_that_shrank()
    {
        var path = PathOf("a.txt");
        File.WriteAllText(path, "one\r\ntwo\r\n");
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);
        scanner.Read(1024, CancellationToken.None);

        File.WriteAllText(path, "x\r\n");

        scanner.Read(1024, CancellationToken.None).Outcome.Should().Be(VrchatLogScanOutcome.Shrunk);
    }

    [Fact]
    public void A_missing_file_is_unreadable_rather_than_an_error()
    {
        var scanner = new VrchatLogScanner(PathOf("missing.txt"), DateTime.UnixEpoch);

        scanner.Read(1024, CancellationToken.None).Outcome.Should().Be(VrchatLogScanOutcome.Unreadable);
    }

    [Fact]
    public void Can_be_cancelled()
    {
        var path = PathOf("a.txt");
        File.WriteAllText(path, "one\r\n");
        var scanner = new VrchatLogScanner(path, DateTime.UnixEpoch);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var read = () => scanner.Read(1024, cts.Token);

        read.Should().Throw<OperationCanceledException>();
    }
}

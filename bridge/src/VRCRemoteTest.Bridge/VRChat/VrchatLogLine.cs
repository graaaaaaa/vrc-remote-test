using System.Text;

namespace VRCRemoteTest.Bridge.VRChat;

internal enum VrchatLogLevel
{
    Debug,
    Warning,
    Error,

    /// <summary>Unity's other log types; not seen in VRChat's output_log so far, kept as headers all the same.</summary>
    Other,
}

/// <summary>The header of a VRChat log line: "yyyy.MM.dd HH:mm:ss " + the level padded with spaces to 11 + "-  ".</summary>
internal readonly record struct VrchatLogHeader(DateTime LocalTime, VrchatLogLevel Level);

/// <summary>
/// One line of an output_log as the Bridge reads it: the bytes up to a CRLF, decoded.
/// <paramref name="Seq"/> numbers the complete lines of the file from 1 in the order they
/// were read. <paramref name="Truncated"/> lines were longer than
/// <see cref="VrchatLogScanner.MaxLineBytes"/> and keep only their start.
/// </summary>
internal sealed record VrchatLogPhysicalLine(long Seq, string Text, bool Truncated);

/// <summary>
/// How the Bridge splits and reads VRChat's output_log (client-observation plan C6).
///
/// The line delimiter is CRLF. A world's Debug.Log strings are written unchanged, LFs and
/// CRLFs included (docs/validation/multi-client-spike.md, S5b A), but in a log that only
/// one client writes the start of a real line was always preceded by a CRLF or the start
/// of the file (S5a C: 268 files, 533,717 lines). So a lone LF never ends a line, and a
/// date header right after one is not a real line but the continuation of the previous
/// one: that drops a forged header written after an LF inside a message, as in all six
/// rounds of S5b. A forged header after a CRLF, or a whole forged line, cannot be told
/// from a real one by its bytes; whatever is read from these lines is an observation, never
/// a reason to authorize or own anything.
///
/// The header is checked from the start, character by character: ASCII digits for the
/// date and time, the level padded with spaces to 11 characters, then "-  " (S5b C).
/// </summary>
internal static class VrchatLogLine
{
    /// <summary>"2026.10.18 13:34:37 Debug      -  " is 34 characters (S5b F).</summary>
    internal const int HeaderLength = 34;

    private const int LevelStart = 20;
    private const int LevelWidth = 11;

    /// <summary>Marks a continuation line in the published copies (client-observation plan F2).</summary>
    internal const string ContinuationIndent = "    ";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static bool TryParseHeader(string line, out VrchatLogHeader header)
    {
        header = default;
        if (line.Length < HeaderLength)
        {
            return false;
        }

        var span = line.AsSpan();
        if (!TryDigits(span, 0, 4, out var year) || span[4] != '.'
            || !TryDigits(span, 5, 2, out var month) || span[7] != '.'
            || !TryDigits(span, 8, 2, out var day) || span[10] != ' '
            || !TryDigits(span, 11, 2, out var hour) || span[13] != ':'
            || !TryDigits(span, 14, 2, out var minute) || span[16] != ':'
            || !TryDigits(span, 17, 2, out var second) || span[19] != ' '
            || span[31] != '-' || span[32] != ' ' || span[33] != ' ')
        {
            return false;
        }

        // The name starts the field and only spaces follow it.
        var levelName = span.Slice(LevelStart, LevelWidth).TrimEnd(' ');
        if (levelName.Length == 0 || levelName.Contains(' '))
        {
            return false;
        }

        VrchatLogLevel level;
        if (levelName.SequenceEqual("Debug"))
        {
            level = VrchatLogLevel.Debug;
        }
        else if (levelName.SequenceEqual("Warning"))
        {
            level = VrchatLogLevel.Warning;
        }
        else if (levelName.SequenceEqual("Error"))
        {
            level = VrchatLogLevel.Error;
        }
        else if (levelName.SequenceEqual("Log") || levelName.SequenceEqual("Exception") || levelName.SequenceEqual("Assert"))
        {
            level = VrchatLogLevel.Other;
        }
        else
        {
            return false;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Max(year, 1), month)
            || year < 1 || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        header = new VrchatLogHeader(
            new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified), level);
        return true;
    }

    /// <summary>The message after the header; the caller has checked the header.</summary>
    public static string Body(string line) => line[HeaderLength..];

    /// <summary>
    /// The lines to display for one CRLF-delimited line: the first as it is when it has a
    /// header, and everything else (the parts after a lone LF, and a line without a header)
    /// as a continuation, indented with <see cref="ContinuationIndent"/>. Empty parts stay
    /// empty. The published copy is LF-delimited, so readers (Unity, the Mac's scripts) tell
    /// a continuation by the indent and never split the copy again themselves.
    /// </summary>
    public static IEnumerable<string> ToDisplayLines(string physicalLine)
    {
        var parts = physicalLine.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                yield return part;
            }
            else if (i == 0 && TryParseHeader(part, out _))
            {
                yield return part;
            }
            else
            {
                yield return ContinuationIndent + part;
            }
        }
    }

    /// <summary>
    /// Splits <paramref name="bytes"/> at CRLFs. The last element is the part after the last
    /// CRLF, which may be incomplete (or empty when the bytes end with a CRLF).
    /// </summary>
    public static List<string> SplitPhysicalLines(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && bytes[i - 1] == (byte)'\r')
            {
                lines.Add(Decode(bytes[start..(i - 1)]));
                start = i + 1;
            }
        }

        lines.Add(Decode(bytes[start..]));
        return lines;
    }

    /// <summary>Index just past the first CRLF, or -1.</summary>
    public static int IndexAfterFirstCrlf(ReadOnlySpan<byte> bytes)
    {
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n' && bytes[i - 1] == (byte)'\r')
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// UTF-8 tolerant of invalid sequences (replacement characters instead of throwing):
    /// output_log is BOM-less UTF-8 (S5b E), but a read can still start or stop inside one.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes) => Utf8.GetString(bytes);

    /// <summary>A header's local time in UTC, or null when daylight saving time makes it ambiguous or skipped.</summary>
    public static DateTimeOffset? ToUtc(DateTime localTime, TimeZoneInfo timeZone)
    {
        if (timeZone.IsInvalidTime(localTime) || timeZone.IsAmbiguousTime(localTime))
        {
            return null;
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localTime, timeZone), TimeSpan.Zero);
    }

    private static bool TryDigits(ReadOnlySpan<char> span, int start, int count, out int value)
    {
        value = 0;
        for (var i = start; i < start + count; i++)
        {
            var c = span[i];
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = value * 10 + (c - '0');
        }

        return true;
    }
}

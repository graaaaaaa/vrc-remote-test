using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRCRemoteTest.Bridge.VRChat;

/// <summary>
/// The marker lines of one client's session: the lines a world writes with a known prefix,
/// collected from the start of its log so they never scroll out of the 200-line tail
/// (client-observation plan F3). Published as logs/clients/slot-{n}.markers.log:
///
/// <code>
/// # {"schemaVersion":1,"slot":0,"clientGeneration":…,"observationEpoch":…,"catchingUp":false,…}
/// 1520	2026.10.18 13:41:41 Debug      -  [SPK003] PLAYER_RESTORED id=1 local=True
/// </code>
///
/// A marker is a line with a header (<see cref="VrchatLogLine"/>) whose message starts with
/// one of the prefixes. The lines without a header that follow it (a multi-line message)
/// are added to the same record after an escaped "\n"; on their own they are never
/// markers. Each record is "&lt;seq&gt;\t&lt;line&gt;", masked (<see cref="LogRedactor"/>), with
/// backslashes, control characters (tabs, ESC, CR, LF, C1) and bidirectional controls
/// escaped, at most <see cref="MaxRecordBytes"/> bytes of UTF-8 including the mark of a
/// cut. The file keeps the newest records up to <see cref="MaxLines"/> lines or
/// <see cref="MaxFileBytes"/>, and its header says how many older ones were dropped.
/// </summary>
internal sealed class VrchatMarkerLog
{
    internal const int MaxRecordBytes = 4 * 1024;
    internal const int MaxLines = 2_000;
    internal const int MaxFileBytes = 1024 * 1024;
    internal const string HeaderPrefix = "# ";
    internal const string TruncationMark = " [truncated]";

    /// <summary>
    /// The unmasked text kept for the record continuation lines are still added to. Past
    /// this, the record is closed and marked cut: masking can shorten text, but a record is
    /// published as at most <see cref="MaxRecordBytes"/> anyway.
    /// </summary>
    internal const int MaxRawChars = 16 * 1024;

    /// <summary>
    /// Room for the header line. Its largest form is 8 prefixes of 32 characters that JSON
    /// escapes to 6 bytes each (1,536 bytes with their quotes and commas well under 1,600),
    /// plus fixed fields under 300 bytes.
    /// </summary>
    internal const int HeaderAllowance = 4 * 1024;

    private static readonly JsonSerializerOptions HeaderOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly LinkedList<Record> _records = new();
    private readonly IReadOnlyList<string> _prefixes;

    /// <summary>The published size of the records, newlines included.</summary>
    private long _bytes;

    /// <summary>The record continuation lines are added to, while the last line read was part of it.</summary>
    private Record? _open;

    public VrchatMarkerLog(IReadOnlyList<string> prefixes)
    {
        _prefixes = prefixes;
    }

    public long DroppedRecords { get; private set; }

    public long DroppedThroughSeq { get; private set; }

    /// <summary>Changes whenever the published content would.</summary>
    public long Version { get; private set; }

    public int Count => _records.Count;

    /// <summary>The unmasked characters still kept, for tests: only the open record keeps any.</summary>
    internal int RetainedRawChars => _open?.Raw?.Length ?? 0;

    public void OnLine(VrchatLogPhysicalLine line)
    {
        if (!VrchatLogLine.TryParseHeader(line.Text, out _))
        {
            if (_open is not null && line.Text.Length > 0)
            {
                Continue(_open, line);
            }

            return;
        }

        Close();
        var body = VrchatLogLine.Body(line.Text);
        if (!_prefixes.Any(p => body.StartsWith(p, StringComparison.Ordinal)))
        {
            return;
        }

        var record = new Record(line.Seq, line.Truncated);
        _records.AddLast(record);
        Version++;
        if (line.Truncated || line.Text.Length > MaxRawChars)
        {
            record.Truncated = true;
            Format(record, CutAtSpace(line.Text, MaxRawChars));
            return;
        }

        record.Raw = new StringBuilder(line.Text);
        _open = record;
    }

    /// <summary>Formats the open record, so what <see cref="Render"/> shows and the limits count is current.</summary>
    public void Flush()
    {
        if (_open is { Dirty: true } open)
        {
            Format(open, open.Raw!.ToString());
            if (open.Full)
            {
                Close();
            }
        }
    }

    private void Continue(Record record, VrchatLogPhysicalLine line)
    {
        Version++;
        var raw = record.Raw!;
        if (line.Truncated || raw.Length + 1 + line.Text.Length > MaxRawChars)
        {
            record.Truncated = true;
            raw.Append('\n').Append(CutAtSpace(line.Text, Math.Max(0, MaxRawChars - raw.Length - 1)));
            Close();
            return;
        }

        raw.Append('\n').Append(line.Text);
        record.Dirty = true;
    }

    /// <summary>
    /// At most <paramref name="max"/> characters, ending at the last space or tab before the
    /// cut, so a value the cut went through (a URL whose "@" was cut off) is dropped whole
    /// rather than kept where masking could no longer recognize it.
    /// </summary>
    internal static string CutAtSpace(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var space = text.AsSpan(0, max).LastIndexOfAny(' ', '\t');
        return space < 0 ? string.Empty : text[..space];
    }

    /// <summary>The open record is formatted and its unmasked text released.</summary>
    private void Close()
    {
        if (_open is not { } open)
        {
            return;
        }

        _open = null;
        Format(open, open.Raw!.ToString());
        open.Raw = null;
    }

    /// <summary>Masks first and cuts afterwards (client-observation plan C5), then applies the file's limits.</summary>
    private void Format(Record record, string raw)
    {
        var prefix = record.Seq.ToString(CultureInfo.InvariantCulture) + "\t";
        var budget = MaxRecordBytes - prefix.Length - TruncationMark.Length;
        var masked = LogRedactor.Redact(raw);
        var (text, cut) = Escape(masked, budget);
        record.Full = cut || LogRedactor.IsWithheld(masked);
        record.Dirty = false;
        var line = prefix + text + (cut || record.Truncated ? TruncationMark : string.Empty);
        var bytes = Encoding.UTF8.GetByteCount(line) + 1;
        _bytes += bytes - record.Bytes;
        record.Line = line;
        record.Bytes = bytes;
        Trim();
    }

    private void Trim()
    {
        while (_records.Count > MaxLines - 1 || (_records.Count > 0 && _bytes + HeaderAllowance > MaxFileBytes))
        {
            var oldest = _records.First!.Value;
            _records.RemoveFirst();
            _bytes -= oldest.Bytes;
            DroppedRecords++;
            DroppedThroughSeq = Math.Max(DroppedThroughSeq, oldest.Seq);
            if (ReferenceEquals(_open, oldest))
            {
                _open = null;
            }
        }
    }

    public string Render(MarkerFileHeader header)
    {
        Flush();
        var builder = new StringBuilder((int)Math.Min(_bytes + HeaderAllowance, MaxFileBytes));
        builder.Append(HeaderPrefix).Append(JsonSerializer.Serialize(header, HeaderOptions)).Append('\n');
        foreach (var record in _records)
        {
            if (record.Line.Length > 0)
            {
                builder.Append(record.Line).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Escapes <paramref name="text"/> for one record and cuts it at <paramref name="budget"/>
    /// bytes of UTF-8, never inside an escape or a character.
    /// </summary>
    internal static (string Text, bool Cut) Escape(string text, int budget)
    {
        var builder = new StringBuilder(Math.Min(text.Length + 16, budget));
        var used = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string? escaped = c switch
            {
                '\\' => @"\\",
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ when NeedsUnicodeEscape(c) => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => null,
            };

            int bytes;
            if (escaped is not null)
            {
                bytes = escaped.Length;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                bytes = 4;
            }
            else
            {
                bytes = Encoding.UTF8.GetByteCount([c]);
            }

            if (used + bytes > budget)
            {
                return (builder.ToString(), true);
            }

            used += bytes;
            if (escaped is not null)
            {
                builder.Append(escaped);
            }
            else if (bytes == 4)
            {
                builder.Append(c).Append(text[++i]);
            }
            else
            {
                builder.Append(c);
            }
        }

        return (builder.ToString(), false);
    }

    /// <summary>Other control characters, and the bidirectional controls that can reorder what a terminal shows.</summary>
    private static bool NeedsUnicodeEscape(char c) =>
        char.IsControl(c)
        || c is '\u061c' or '\u200e' or '\u200f' or (>= '\u202a' and <= '\u202e') or (>= '\u2066' and <= '\u2069');

    private sealed class Record(long seq, bool truncated)
    {
        public long Seq { get; } = seq;

        public bool Truncated { get; set; } = truncated;

        /// <summary>The unmasked text, kept only while the record is open.</summary>
        public StringBuilder? Raw { get; set; }

        /// <summary>Text was added since the record was last formatted.</summary>
        public bool Dirty { get; set; } = true;

        public string Line { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>The record reached <see cref="MaxRecordBytes"/>, or was withheld; nothing more is added to it.</summary>
        public bool Full { get; set; }
    }
}

/// <summary>The first line of a markers file, after <see cref="VrchatMarkerLog.HeaderPrefix"/>.</summary>
internal sealed class MarkerFileHeader
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    [JsonPropertyName("clientGeneration")]
    public long ClientGeneration { get; init; }

    [JsonPropertyName("observationEpoch")]
    public long ObservationEpoch { get; init; }

    /// <summary>The log has not been read to its end yet; what follows may be incomplete.</summary>
    [JsonPropertyName("catchingUp")]
    public bool CatchingUp { get; init; }

    [JsonPropertyName("droppedRecords")]
    public long DroppedRecords { get; init; }

    /// <summary>The newest seq among the dropped records; records at or before it may be missing.</summary>
    [JsonPropertyName("droppedThroughSeq")]
    public long DroppedThroughSeq { get; init; }

    [JsonPropertyName("prefixes")]
    public IReadOnlyList<string> Prefixes { get; init; } = [];
}

namespace VRCRemoteTest.Bridge.VRChat;

internal enum VrchatLogScanOutcome
{
    /// <summary>Read what was appended, up to the budget.</summary>
    Read,

    /// <summary>The file is shorter than what was read before: it must be read again from the start.</summary>
    Shrunk,

    /// <summary>The file could not be opened or read now; nothing changed.</summary>
    Unreadable,
}

/// <param name="ReachedEnd">The read stopped at the end of the file rather than at the budget.</param>
internal sealed record VrchatLogScanResult(
    VrchatLogScanOutcome Outcome, IReadOnlyList<VrchatLogPhysicalLine> Lines, long BytesRead, bool ReachedEnd);

/// <summary>
/// Reads one output_log incrementally from its start: each call reads only what was
/// appended since the previous one, at most a budget of bytes, in 64 KiB chunks
/// (client-observation plan F3). Lines are split at CRLF as <see cref="VrchatLogLine"/>
/// describes; a lone LF stays in the line. The bytes of an unfinished line are kept between
/// calls and decoded only once the line is complete, so a character split by a read
/// boundary is decoded whole, as a decoder that keeps its state across reads would.
///
/// A line longer than <see cref="MaxLineBytes"/> without a CRLF keeps its first
/// <see cref="TruncatedKeepBytes"/> and the rest is dropped up to the next CRLF; it is
/// marked truncated. A world can write such a line: S5b F wrote 65,593 bytes in one line.
/// What is kept ends at its last space or tab, so a value the cut went through (a token, a
/// URL whose "@" was cut off) is dropped whole rather than kept where masking could no
/// longer recognize it.
///
/// One instance belongs to one file of one client and is used from one loop; whoever owns
/// it decides when the file has been replaced and starts a new instance.
/// </summary>
internal sealed class VrchatLogScanner
{
    internal const int MaxLineBytes = 64 * 1024;
    internal const int TruncatedKeepBytes = 4 * 1024;
    internal const int ChunkBytes = 64 * 1024;

    private readonly byte[] _line = new byte[MaxLineBytes];
    private int _lineLength;
    private bool _discarding;
    private byte _previous;

    public VrchatLogScanner(string path, DateTime fileCreationUtc)
    {
        Path = path;
        FileCreationUtc = fileCreationUtc;
    }

    public string Path { get; }

    /// <summary>The creation time the file had when this scan started; another one means another file.</summary>
    public DateTime FileCreationUtc { get; }

    /// <summary>Bytes read from the start of the file.</summary>
    public long Offset { get; private set; }

    /// <summary>The number of complete lines read, which is also the last line's seq.</summary>
    public long Seq { get; private set; }

    /// <summary>The file's length when it was last read.</summary>
    public long LastLength { get; private set; }

    /// <summary>How many lines were truncated.</summary>
    public long TruncatedLines { get; private set; }

    public VrchatLogScanResult Read(long budget, CancellationToken cancellationToken)
    {
        var lines = new List<VrchatLogPhysicalLine>();
        long bytesRead = 0;
        var reachedEnd = false;
        try
        {
            using var stream = new FileStream(
                Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            var length = stream.Length;
            LastLength = length;
            if (length < Offset)
            {
                return new VrchatLogScanResult(VrchatLogScanOutcome.Shrunk, [], 0, false);
            }

            stream.Seek(Offset, SeekOrigin.Begin);
            var buffer = new byte[ChunkBytes];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = (int)Math.Min(ChunkBytes, budget - bytesRead);
                if (want <= 0)
                {
                    break;
                }

                var read = stream.Read(buffer, 0, want);
                if (read == 0)
                {
                    reachedEnd = true;
                    break;
                }

                Process(buffer.AsSpan(0, read), lines);
                Offset += read;
                bytesRead += read;
            }

            LastLength = Math.Max(LastLength, Offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new VrchatLogScanResult(VrchatLogScanOutcome.Unreadable, lines, bytesRead, false);
        }

        return new VrchatLogScanResult(VrchatLogScanOutcome.Read, lines, bytesRead, reachedEnd);
    }

    private void Process(ReadOnlySpan<byte> chunk, List<VrchatLogPhysicalLine> lines)
    {
        foreach (var b in chunk)
        {
            if (b == (byte)'\n' && _previous == (byte)'\r')
            {
                if (!_discarding && _lineLength > 0 && _line[_lineLength - 1] == (byte)'\r')
                {
                    _lineLength--;
                }

                Emit(lines);
            }
            else
            {
                Append(b);
            }

            _previous = b;
        }
    }

    private void Append(byte b)
    {
        if (_discarding)
        {
            return;
        }

        if (_lineLength == MaxLineBytes)
        {
            _lineLength = TruncatedKeepBytes;
            _discarding = true;
            return;
        }

        _line[_lineLength++] = b;
    }

    private void Emit(List<VrchatLogPhysicalLine> lines)
    {
        var length = _lineLength;
        var truncated = _discarding;
        if (truncated)
        {
            length = CutForTruncation(_line.AsSpan(0, length), _line[length]);
            TruncatedLines++;
        }

        Seq++;
        lines.Add(new VrchatLogPhysicalLine(Seq, VrchatLogLine.Decode(_line.AsSpan(0, length)), truncated));
        _lineLength = 0;
        _discarding = false;
    }

    /// <summary>
    /// Backs off to a UTF-8 character boundary (<paramref name="next"/> is the first byte
    /// that was dropped), then to the last space, tab or LF. A line's header has spaces, so
    /// at least the header stays.
    /// </summary>
    private static int CutForTruncation(ReadOnlySpan<byte> kept, byte next)
    {
        var length = kept.Length;
        if ((next & 0xC0) == 0x80)
        {
            while (length > 0 && (kept[length - 1] & 0xC0) == 0x80)
            {
                length--;
            }

            if (length > 0 && kept[length - 1] >= 0xC0)
            {
                length--;
            }
        }

        var space = kept[..length].LastIndexOfAny((byte)' ', (byte)'\t', (byte)'\n');
        return Math.Max(space, 0);
    }
}

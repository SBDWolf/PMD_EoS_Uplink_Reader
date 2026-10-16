using System.Buffers.Binary;

namespace UplinkReader.Protocol;

/// <summary>
/// Incremental parser for the uplink byte stream (protocol v2):
/// resynchronizes on the "PM" magic, validates the variable-length frame
/// header (sample_count 1..256) and checksums, and separates complete
/// frames from stray text (device reply lines). Binary garbage between
/// frames (e.g. the tail of a frame the mod aborted mid-ship) is treated
/// as stray bytes; the client only reacts to lines with known prefixes.
/// </summary>
public sealed class StreamProcessor
{
    private const int StrayFlushLimit = 4096;

    private byte[] _buf = new byte[8192];
    private int _bufLen;
    private byte[] _stray = new byte[8192];
    private int _strayLen;

    /// <summary>Frames consumed with a failing checksum.</summary>
    public int BadChecksums { get; private set; }

    /// <summary>Frames rejected for a bad header (magic/count) or dropped as truncated.</summary>
    public int Resyncs { get; private set; }

    private readonly Action<string>? _onBadFrame;

    public StreamProcessor(Action<string>? onBadFrame = null)
    {
        _onBadFrame = onBadFrame;
    }

    public void Reset()
    {
        _bufLen = 0;
        _strayLen = 0;
        BadChecksums = 0;
        Resyncs = 0;
    }

    /// <summary>
    /// Feeds raw bytes into the parser. onFrame receives each complete,
    /// checksum-verified frame (a copy — safe to keep); onLine receives
    /// complete stray text lines, trimmed, without newlines. onBadFrame
    /// (optional) receives a diagnostic line for every rejected frame
    /// (hex dump + stored vs expected checksum) — used to pinpoint
    /// encoder/decoder mismatches from a device log.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, Action<UplinkSample> onFrame, Action<string> onLine,
        Action<string>? onBadFrame = null)
    {
        _buf = GrowBuf(_bufLen + data.Length);
        data.CopyTo(_buf.AsSpan(_bufLen));
        _bufLen += data.Length;

        for (;;)
        {
            int idx = FindMagic();
            if (idx < 0)
            {
                if (_bufLen > 0)
                {
                    if (_buf[_bufLen - 1] == UplinkProtocol.Magic0)
                    {
                        // Possible split magic: keep the trailing 'P', the
                        // rest is stray data.
                        AppendStray(_buf.AsSpan(0, _bufLen - 1));
                        _buf[0] = _buf[_bufLen - 1];
                        _bufLen = 1;
                    }
                    else
                    {
                        AppendStray(_buf.AsSpan(0, _bufLen));
                        _bufLen = 0;
                    }
                }
                FlushStray(onLine);
                return;
            }

            if (idx > 0)
            {
                AppendStray(_buf.AsSpan(0, idx));
                Array.Copy(_buf, idx, _buf, 0, _bufLen - idx);
                _bufLen -= idx;
                FlushStray(onLine);
            }

            if (_bufLen < UplinkProtocol.FrameHeaderLen)
                return;

            if (!UplinkProtocol.TryParseHeader(_buf.AsSpan(0, UplinkProtocol.FrameHeaderLen),
                    out _, out _, out int count, out _))
            {
                // Corrupt header (magic ok, count invalid): the frame length
                // is unknowable, so drop just the 'P' and rescan. A real
                // frame's sample bytes may contain 'P','M', so never skip
                // more than one byte blindly.
                Resyncs++;
                (onBadFrame ?? _onBadFrame)?.Invoke($"bad header, count={BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(10))} " +
                                   $"header={Hex(_buf.AsSpan(0, Math.Min(16, _bufLen)))}");
                DropFirstByte();
                continue;
            }

            int frameLen = UplinkProtocol.FrameLength(count);
            if (_bufLen < frameLen)
                return; // wait for the rest of the frame

            var frame = _buf.AsSpan(0, frameLen);
            var samples = new uint[count];
            if (!UplinkProtocol.TryParseFrame(frame, out uint seq, out uint gameFrame, out _, out int epoch, samples))
            {
                BadChecksums++;
                (onBadFrame ?? _onBadFrame)?.Invoke($"seq={seq} len={frameLen} " +
                                   $"stored=0x{BinaryPrimitives.ReadUInt16LittleEndian(frame[(frameLen - 2)..]):X4} " +
                                   $"expected=0x{Checksum(frame, frameLen):X4} bytes={Hex(frame)}");
                // Consume the claimed frame and prepend a 'P' (v1 trick) so
                // the scan resumes at the following bytes and a split magic
                // at the boundary is still found.
                Array.Copy(_buf, frameLen, _buf, 0, _bufLen - frameLen);
                _bufLen -= frameLen;
                _buf = GrowBuf(_bufLen + 1);
                Array.Copy(_buf, 0, _buf, 1, _bufLen);
                _buf[0] = UplinkProtocol.Magic0;
                _bufLen++;
                continue;
            }

            var frameCopy = frame.ToArray();
            Array.Copy(_buf, frameLen, _buf, 0, _bufLen - frameLen);
            _bufLen -= frameLen;
            onFrame(new UplinkSample
            {
                Seq = seq,
                GameFrame = gameFrame,
                Epoch = epoch,
                SampleCount = count,
                RawValues = samples,
                RawFrame = frameCopy,
            });
        }
    }

    private void DropFirstByte()
    {
        Array.Copy(_buf, 1, _buf, 0, _bufLen - 1);
        _bufLen--;
    }

    private static uint Checksum(ReadOnlySpan<byte> frame, int len)
    {
        uint sum = 0;
        for (int i = 0; i < len - 2; i++)
            sum += frame[i];
        return sum;
    }

    private static string Hex(ReadOnlySpan<byte> data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private int FindMagic()
    {
        for (int i = 0; i + 1 < _bufLen; i++)
        {
            if (_buf[i] == UplinkProtocol.Magic0 && _buf[i + 1] == UplinkProtocol.Magic1)
                return i;
        }
        return -1;
    }

    private void AppendStray(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;
        _stray = GrowStray(_strayLen + data.Length);
        data.CopyTo(_stray.AsSpan(_strayLen));
        _strayLen += data.Length;
    }

    private void FlushStray(Action<string> onLine)
    {
        if (_strayLen == 0)
            return;

        int newline = Array.IndexOf(_stray, (byte)'\n', 0, _strayLen);
        if (newline < 0 && _strayLen <= StrayFlushLimit)
            return;

        int end = newline < 0 ? _strayLen : newline + 1;
        EmitLines(_stray.AsMemory(0, end), onLine);
        Array.Copy(_stray, end, _stray, 0, _strayLen - end);
        _strayLen -= end;
    }

    // Splits on newlines, trims, and recovers reply lines that got merged
    // with preceding frame bytes (the mod streams frames and replies over
    // the same CDC stream, so a reply can arrive as
    // "PPPP...OK CONFIG ..."). Everything before the LAST known reply
    // prefix is cut so the client's StartsWith checks still match; this
    // also covers the v1-style "PUPLINK" merge.
    private static readonly string[] ReplyPrefixes =
    {
        "OK CONFIG", "OK ENTRIES", "OK STREAMING", "OK ABORTED", "ERR ", "UPLINK ",
    };

    private static void EmitLines(ReadOnlyMemory<byte> chunk, Action<string> onLine)
    {
        string text = System.Text.Encoding.ASCII.GetString(chunk.Span);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim('\r', ' ', '\t');
            int best = -1;
            foreach (var prefix in ReplyPrefixes)
            {
                int idx = line.LastIndexOf(prefix, StringComparison.Ordinal);
                if (idx > best)
                    best = idx;
            }
            if (best > 0)
                line = line[best..];
            if (line.Length > 0)
                onLine(line);
        }
    }

    private byte[] GrowBuf(int needed)
    {
        if (needed <= _buf.Length)
            return _buf;
        int cap = _buf.Length;
        while (cap < needed)
            cap *= 2;
        var grown = new byte[cap];
        Array.Copy(_buf, grown, _bufLen);
        return grown;
    }

    private byte[] GrowStray(int needed)
    {
        if (needed <= _stray.Length)
            return _stray;
        int cap = _stray.Length;
        while (cap < needed)
            cap *= 2;
        var grown = new byte[cap];
        Array.Copy(_stray, grown, _strayLen);
        return grown;
    }
}

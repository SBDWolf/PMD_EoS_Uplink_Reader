namespace UplinkReader.Protocol;

/// <summary>
/// Incremental parser for the uplink byte stream: resynchronizes on the
/// "PM" magic, validates frame checksums, and separates complete 70-byte
/// frames from stray text (e.g. PING replies). The logic mirrors
/// tools/pc_reader.py, including its resync quirks.
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

    public void Reset()
    {
        _bufLen = 0;
        _strayLen = 0;
        BadChecksums = 0;
    }

    /// <summary>
    /// Feeds raw bytes into the parser. onFrame receives each complete,
    /// checksum-verified 70-byte frame (a span into internal storage —
    /// consume it before returning); onLine receives complete stray text
    /// lines, trimmed, without newlines.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> data, Action<byte[]> onFrame, Action<byte[]> onLine)
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

            if (_bufLen < UplinkProtocol.FrameLen)
                return;

            var frame = _buf.AsSpan(0, UplinkProtocol.FrameLen);
            Array.Copy(_buf, UplinkProtocol.FrameLen, _buf, 0, _bufLen - UplinkProtocol.FrameLen);
            _bufLen -= UplinkProtocol.FrameLen;

            var samples = new uint[UplinkProtocol.SampleCount];
            if (!UplinkProtocol.TryParseFrame(frame, out _, out _, samples))
            {
                BadChecksums++;
                // Resync one byte later (pc_reader.py inserts 'P' here) so
                // the next magic is re-scanned from a 'P'.
                _buf = GrowBuf(_bufLen + 1);
                Array.Copy(_buf, 0, _buf, 1, _bufLen);
                _buf[0] = UplinkProtocol.Magic0;
                _bufLen++;
                continue;
            }

            var frameCopy = new byte[UplinkProtocol.FrameLen];
            frame.CopyTo(frameCopy);
            onFrame(frameCopy);
        }
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

    private void FlushStray(Action<byte[]> onLine)
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

    // Splits on newlines, trims, and strips the stray leading 'P' that
    // resync can merge in front of a ping reply ("PUPLINK ...").
    private static void EmitLines(ReadOnlyMemory<byte> chunk, Action<byte[]> onLine)
    {
        string text = System.Text.Encoding.ASCII.GetString(chunk.Span);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("PUPLINK", StringComparison.Ordinal))
                line = line[1..];
            if (line.Length > 0)
                onLine(System.Text.Encoding.ASCII.GetBytes(line));
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

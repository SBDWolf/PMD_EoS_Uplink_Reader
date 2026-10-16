using System.Buffers.Binary;
using System.Text;

namespace UplinkReader.Protocol;

/// <summary>
/// Constants and pure helpers for the PMDSky uplink wire protocol v2, as
/// implemented by src/uplink/uplink.c and src/uplink/uplink_sampler.c.
/// The authoritative wire spec is docs/uplink-protocol.md in the mod repo.
///
/// Telemetry (device -> host, raw bytes, no line framing): variable-length
/// sample frames, L = 16 + 4*N + 2 bytes for N configured entries:
///   [0..1]   magic 'P' 'M'
///   [2..5]   seq           u32 LE
///   [6..9]   game_frame    u32 LE (PLAY_TIME seconds*60 + frames)
///   [10..11] sample_count  u16 LE (= N, lets the parser self-synchronize)
///   [12..13] epoch         u16 LE (bumped on every CONFIG_COMMIT)
///   [14..]   samples[N]    u32 LE
///   [L-2..L-1] checksum    u16 LE, byte sum of bytes [0..L-3)
///
/// Host commands (host -> device, raw bytes, self-delimiting):
///   0x01 START, 0x02 STOP, 0x04 PING (1 byte each)
///   0x10 CONFIG_BEGIN: ver u8, count u16 LE, reserved u8 (5 bytes)
///   0x11 CONFIG_ENTRY: idx u16 LE, base u32 LE, meta u8, steps (8 + 3*S)
///         meta bits 0-1: width code (0=1, 1=2, 2=4 bytes; 3 invalid)
///         meta bits 2-4: step count S (0..4)
///         each step: offset u16 LE (<= 0x7FFF), flags u8 (bit 0 = deref)
///   0x12 CONFIG_COMMIT, 0x13 CONFIG_ABORT (1 byte each)
///
/// Device replies are ASCII lines (OK .../ERR .../UPLINK ...) interleaved
/// with the binary frames; StreamProcessor separates them.
/// </summary>
public static class UplinkProtocol
{
    public const ushort UpLinkVid = 0x2020;
    public const ushort UpLinkPid = 0xD801;
    public const int DefaultBaud = 115200; // CDC ignores it; the driver needs a value

    public const int FrameHeaderLen = 16;
    // Protocol limits. The mod may report a smaller MaxEntries in its
    // "OK CONFIG ... max=" reply; honor that value at upload time.
    public const int MaxSamples = 256;
    public const int MaxSteps = 4;
    public const int MaxOffset = 0x7FFF;
    // Host command writes are kept to ONE entry each so every Write is a
    // single USB packet (see AddressConfig.BuildBatches).

    public const byte Magic0 = (byte)'P';
    public const byte Magic1 = (byte)'M';

    public const byte CmdStart = 0x01;
    public const byte CmdStop = 0x02;
    public const byte CmdPing = 0x04;
    public const byte CmdConfigBegin = 0x10;
    public const byte CmdConfigEntry = 0x11;
    public const byte CmdConfigCommit = 0x12;
    public const byte CmdConfigAbort = 0x13;

    public const byte ConfigVersion = 1;

    public static readonly byte[] StartCommand = { CmdStart };
    public static readonly byte[] StopCommand = { CmdStop };
    public static readonly byte[] PingCommand = { CmdPing };
    public static readonly byte[] ConfigCommitCommand = { CmdConfigCommit };
    public static readonly byte[] ConfigAbortCommand = { CmdConfigAbort };

    public static int FrameLength(int sampleCount) => FrameHeaderLen + 4 * sampleCount + 2;

    public static byte[] ConfigBeginCommand(int count)
    {
        var cmd = new byte[5];
        cmd[0] = CmdConfigBegin;
        cmd[1] = ConfigVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(2), (ushort)count);
        cmd[4] = 0; // reserved
        return cmd;
    }

    /// <summary>
    /// Encodes one CONFIG_ENTRY command (see the module docs for the layout).
    /// </summary>
    public static byte[] ConfigEntryCommand(int index, uint baseAddress, int width,
        IReadOnlyList<(ushort Offset, bool Deref)> steps)
    {
        int widthCode = width switch { 1 => 0, 2 => 1, 4 => 2, _ => throw new ArgumentException($"width {width}") };
        if (steps.Count > MaxSteps)
            throw new ArgumentException($"too many steps ({steps.Count})");
        var cmd = new byte[8 + 3 * steps.Count];
        cmd[0] = CmdConfigEntry;
        BinaryPrimitives.WriteUInt16LittleEndian(cmd.AsSpan(1), (ushort)index);
        BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(3), baseAddress);
        cmd[7] = (byte)(widthCode | (steps.Count << 2));
        for (int i = 0; i < steps.Count; i++)
        {
            var (offset, deref) = steps[i];
            if (offset > MaxOffset)
                throw new ArgumentException($"step offset 0x{offset:X} > 0x{MaxOffset:X}");
            cmd[8 + 3 * i] = (byte)(offset & 0xFF);
            cmd[9 + 3 * i] = (byte)(offset >> 8);
            cmd[10 + 3 * i] = (byte)(deref ? 1 : 0);
        }
        return cmd;
    }

    /// <summary>
    /// Parses the fixed 16-byte frame header. Returns false when the magic
    /// is wrong or sample_count is outside 1..MaxSamples (the mod never
    /// streams with an empty table, so 0 is also corrupt).
    /// </summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> header, out uint seq, out uint gameFrame,
        out int sampleCount, out int epoch)
    {
        seq = 0;
        gameFrame = 0;
        sampleCount = 0;
        epoch = 0;
        if (header.Length < FrameHeaderLen)
            return false;
        if (header[0] != Magic0 || header[1] != Magic1)
            return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        if (count < 1 || count > MaxSamples)
            return false;
        seq = BinaryPrimitives.ReadUInt32LittleEndian(header[2..]);
        gameFrame = BinaryPrimitives.ReadUInt32LittleEndian(header[6..]);
        epoch = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        sampleCount = count;
        return true;
    }

    /// <summary>
    /// Validates magic, sample_count range, length and checksum, then
    /// decodes the frame into <paramref name="samples"/> (length >= count).
    /// </summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> frame, out uint seq, out uint gameFrame,
        out int sampleCount, out int epoch, Span<uint> samples)
    {
        seq = 0;
        gameFrame = 0;
        sampleCount = 0;
        epoch = 0;
        if (!TryParseHeader(frame, out seq, out gameFrame, out sampleCount, out epoch))
            return false;
        int len = FrameLength(sampleCount);
        if (frame.Length < len || samples.Length < sampleCount)
            return false;

        uint sum = 0;
        for (int i = 0; i < len - 2; i++)
            sum += frame[i];
        ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(frame[(len - 2)..]);
        if (unchecked((ushort)sum) != stored)
            return false;

        for (int i = 0; i < sampleCount; i++)
            samples[i] = BinaryPrimitives.ReadUInt32LittleEndian(frame[(FrameHeaderLen + 4 * i)..]);
        return true;
    }

    /// <summary>Builds a frame exactly like the mod does (used by the self-test).</summary>
    public static byte[] BuildFrame(uint seq, uint gameFrame, int epoch, ReadOnlySpan<uint> samples)
    {
        int len = FrameLength(samples.Length);
        var frame = new byte[len];
        frame[0] = Magic0;
        frame[1] = Magic1;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), gameFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), (ushort)samples.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12), (ushort)epoch);
        ushort sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(FrameHeaderLen + 4 * i), samples[i]);
        }
        for (int i = 0; i < len - 2; i++)
            sum = (ushort)(sum + frame[i]);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(len - 2), sum);
        return frame;
    }

    /// <summary>Human-readable form of a device reply line for logs.</summary>
    public static string DescribeLine(string line) => line.Length > 120 ? line[..120] + "..." : line;

    /// <summary>ASCII bytes of a line (replies are pure ASCII).</summary>
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}

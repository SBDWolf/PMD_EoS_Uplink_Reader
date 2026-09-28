using System.Buffers.Binary;

namespace UplinkReader.Protocol;

/// <summary>
/// Constants and pure helpers for the PMDSky uplink wire protocol, as
/// implemented by src/uplink/uplink_sampler.c and src/uplink/uplink.c.
/// (See the tools/pc_reader.py header for the original documentation of
/// this stream.)
///
/// Telemetry (device -> host, raw bytes, no line framing): 70-byte frames
/// sent in 490-byte blocks (7 frames per 512-byte DSpico card phase):
///   [0..1]   magic 'P' 'M'
///   [2..5]   seq          frame index (u32 LE)
///   [6..9]   game_frame   PLAY_TIME as seconds*60 + frames (u32 LE)
///   [10..65] samples[14]  u32 LE, one per sample slot
///   [66..67] checksum     sum of bytes [0..65] (u16 LE)
///   [68..69] padding      zero
///
/// Host commands (host -> device, raw bytes):
///   0x01        start sampling
///   0x02        stop sampling
///   0x03 idx u32 retarget sample slot idx to address (0 disables)
///   0x04        ping; device replies with
///               "UPLINK seq=<n> sent=<n> drop=<n>\r\n" on the same stream
/// </summary>
public static class UplinkProtocol
{
    public const ushort UpLinkVid = 0x2020;
    public const ushort UpLinkPid = 0xD801;
    public const int DefaultBaud = 115200; // CDC ignores it; the driver needs a value

    public const int FrameLen = 70;
    public const int SampleCount = 14;
    public const byte Magic0 = (byte)'P';
    public const byte Magic1 = (byte)'M';

    public const byte CmdStart = 0x01;
    public const byte CmdStop = 0x02;
    public const byte CmdSetAddr = 0x03;
    public const byte CmdPing = 0x04;

    public static readonly byte[] StartCommand = { CmdStart };
    public static readonly byte[] StopCommand = { CmdStop };
    public static readonly byte[] PingCommand = { CmdPing };

    public static byte[] SetAddrCommand(int slot, uint address)
    {
        if (slot < 0 || slot >= SampleCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        var cmd = new byte[6];
        cmd[0] = CmdSetAddr;
        cmd[1] = (byte)slot;
        BinaryPrimitives.WriteUInt32LittleEndian(cmd.AsSpan(2), address);
        return cmd;
    }

    /// <summary>
    /// Validates magic and checksum, then decodes seq/game_frame/samples.
    /// Returns false when the frame is not a valid uplink frame.
    /// </summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> frame, out uint seq, out uint gameFrame, Span<uint> samples)
    {
        seq = 0;
        gameFrame = 0;
        if (frame.Length < FrameLen)
            return false;
        if (frame[0] != Magic0 || frame[1] != Magic1)
            return false;

        seq = BinaryPrimitives.ReadUInt32LittleEndian(frame[2..]);
        gameFrame = BinaryPrimitives.ReadUInt32LittleEndian(frame[6..]);
        for (int i = 0; i < SampleCount; i++)
            samples[i] = BinaryPrimitives.ReadUInt32LittleEndian(frame[(10 + 4 * i)..]);

        ushort stored = BinaryPrimitives.ReadUInt16LittleEndian(frame[66..]);
        uint sum = 0;
        for (int i = 0; i < FrameLen - 4; i++)
            sum += frame[i];
        return unchecked((ushort)sum) == stored;
    }
}

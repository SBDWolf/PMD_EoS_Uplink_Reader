using System.Buffers.Binary;
using System.Text;
using UplinkReader.Protocol;

namespace UplinkReader.SelfTest;

/// <summary>
/// Offline protocol verification (no device needed): frame encode/
/// decode, checksum validation, stream resync, stray-line handling, and
/// command encoding. Mirrors the behaviors pc_reader.py relies on.
/// </summary>
public static class SelfTestRunner
{
    public static int Run()
    {
        int failures = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok)
                failures++;
        }

        // 1. Frame roundtrip.
        var frame = BuildFrame(0xA, 0xB,
            Enumerable.Range(0, UplinkProtocol.SampleCount).Select(i => (uint)(i * 7 + 1)).ToArray());
        var samples = new uint[UplinkProtocol.SampleCount];
        bool ok = UplinkProtocol.TryParseFrame(frame, out uint seq, out uint gf, samples);
        Check(ok && seq == 0xA && gf == 0xB && samples[3] == 22, "frame roundtrip (magic/fields/checksum)");

        // 2. Corrupted frame.
        var bad = (byte[])frame.Clone();
        bad[12] ^= 0xFF;
        Check(!UplinkProtocol.TryParseFrame(bad, out _, out _, samples), "corrupted frame rejected");

        // 3. Wrong magic.
        var nomagic = (byte[])frame.Clone();
        nomagic[1] = (byte)'X';
        Check(!UplinkProtocol.TryParseFrame(nomagic, out _, out _, samples), "wrong magic rejected");

        // 4. Stream resync + stray handling.
        var proc = new StreamProcessor();
        var seqs = new List<uint>();
        var lines = new List<string>();
        void OnFrame(byte[] f)
        {
            var ss = new uint[UplinkProtocol.SampleCount];
            if (UplinkProtocol.TryParseFrame(f, out var s, out _, ss))
                seqs.Add(s);
        }
        void OnLine(byte[] l) => lines.Add(Encoding.ASCII.GetString(l));

        var f1 = BuildFrame(1, 100, new uint[UplinkProtocol.SampleCount]);
        proc.Feed(f1.AsSpan(0, 10), OnFrame, OnLine);
        Check(seqs.Count == 0, "partial frame held back");
        proc.Feed(f1.AsSpan(10), OnFrame, OnLine);
        Check(seqs.Count == 1 && seqs[0] == 1, "frame reassembled across feeds");

        proc.Feed(Encoding.ASCII.GetBytes("UPLINK seq=1 sent=2 drop=0\r\n"), OnFrame, OnLine);
        Check(lines.Count == 1 && lines[0] == "UPLINK seq=1 sent=2 drop=0", "ping reply line captured");

        var f2 = BuildFrame(2, 200, new uint[UplinkProtocol.SampleCount]);
        f2[20] ^= 0x55;
        var f3 = BuildFrame(3, 300, new uint[UplinkProtocol.SampleCount]);
        proc.Feed(f2, OnFrame, OnLine);
        proc.Feed(f3, OnFrame, OnLine);
        Check(proc.BadChecksums == 1, "bad checksum counted");
        Check(seqs.Count == 2 && seqs[1] == 3, "resync recovered the next frame");

        proc.Feed(Encoding.ASCII.GetBytes("UPLINK seq=3 sent=4 drop=0\r\n"), OnFrame, OnLine);
        Check(lines.Count == 2 && lines[1] == "UPLINK seq=3 sent=4 drop=0", "stray 'P' stripped from ping line");

        // 5. SET_ADDR command bytes.
        var cmd = UplinkProtocol.SetAddrCommand(5, 0x040001A4);
        Check(cmd.SequenceEqual(new byte[] { 0x03, 0x05, 0xA4, 0x01, 0x00, 0x04 }), "SET_ADDR command encoding");

        Console.WriteLine(failures == 0 ? "all self-tests passed" : $"{failures} self-test(s) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static byte[] BuildFrame(uint seq, uint gameFrame, uint[] samples)
    {
        var b = new byte[UplinkProtocol.FrameLen];
        b[0] = UplinkProtocol.Magic0;
        b[1] = UplinkProtocol.Magic1;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(6), gameFrame);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(10 + 4 * i), samples[i]);
        uint sum = 0;
        for (int i = 0; i < UplinkProtocol.FrameLen - 4; i++)
            sum += b[i];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(66), unchecked((ushort)sum));
        return b;
    }
}

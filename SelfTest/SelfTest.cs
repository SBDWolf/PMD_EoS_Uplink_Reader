using System.Buffers.Binary;
using System.Text;
using UplinkReader.Config;
using UplinkReader.Protocol;

namespace UplinkReader.SelfTest;

/// <summary>
/// Offline protocol verification (no device needed): variable-length
/// frame encode/decode, checksum validation, header validation, stream
/// resync, stray-line handling, address-config parsing/validation, entry
/// encoding and batch packing, and the memory-map block layout (format
/// v3, name -> value). Run with `dotnet run -- --selftest`.
/// </summary>
public static class SelfTestRunner
{
    public static int Run()
    {
        int failures = 0;
        var report = new StringBuilder();
        void Check(bool ok, string name)
        {
            string line = $"{(ok ? "PASS" : "FAIL")}  {name}";
            Console.WriteLine(line);
            report.AppendLine(line);
            if (!ok)
                failures++;
        }

        //------------------------------------------------------------------
        // 1. Frame roundtrip (variable length)
        //------------------------------------------------------------------
        var samples = Enumerable.Range(0, 14).Select(i => (uint)(i * 7 + 1)).ToArray();
        var frame = UplinkProtocol.BuildFrame(0xA, 0xB, 3, samples);
        Check(frame.Length == UplinkProtocol.FrameLength(14), "frame length = 16 + 4N + 2");
        var parsed = new uint[14];
        bool ok = UplinkProtocol.TryParseFrame(frame, out uint seq, out uint gf, out int count, out int epoch, parsed);
        Check(ok && seq == 0xA && gf == 0xB && count == 14 && epoch == 3 && parsed[3] == 22,
            "frame roundtrip (magic/fields/checksum)");

        // 2. Corrupted frame rejected.
        var bad = (byte[])frame.Clone();
        bad[12] ^= 0xFF;
        Check(!UplinkProtocol.TryParseFrame(bad, out _, out _, out _, out _, parsed), "corrupted frame rejected");

        // 3. Wrong magic rejected.
        var nomagic = (byte[])frame.Clone();
        nomagic[1] = (byte)'X';
        Check(!UplinkProtocol.TryParseHeader(nomagic, out _, out _, out _, out _), "wrong magic rejected");

        // 4. sample_count validation: 0 and >256 rejected.
        var zeroCount = (byte[])frame.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(zeroCount.AsSpan(10), 0);
        Check(!UplinkProtocol.TryParseHeader(zeroCount, out _, out _, out _, out _), "sample_count 0 rejected");
        var bigCount = (byte[])frame.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(bigCount.AsSpan(10), 257);
        Check(!UplinkProtocol.TryParseHeader(bigCount, out _, out _, out _, out _), "sample_count 257 rejected");

        // 5. Max-size frame roundtrip.
        var maxSamples = new uint[UplinkProtocol.MaxSamples];
        maxSamples[255] = 0xDEADBEEF;
        var maxFrame = UplinkProtocol.BuildFrame(1, 2, 1, maxSamples);
        Check(maxFrame.Length == 1042, "max frame length 1042");
        var maxParsed = new uint[UplinkProtocol.MaxSamples];
        Check(UplinkProtocol.TryParseFrame(maxFrame, out _, out _, out int maxCount, out _, maxParsed)
              && maxCount == 256 && maxParsed[255] == 0xDEADBEEF, "max-size frame roundtrip");

        //------------------------------------------------------------------
        // 6. Stream: reassembly, resync, stray lines
        //------------------------------------------------------------------
        var proc = new StreamProcessor();
        var seqs = new List<uint>();
        var lines = new List<string>();
        void OnFrame(UplinkSample s) => seqs.Add(s.Seq);
        void OnLine(string l) => lines.Add(l);

        var f1 = UplinkProtocol.BuildFrame(1, 100, 1, new uint[14]);
        proc.Feed(f1.AsSpan(0, 10), OnFrame, OnLine);
        Check(seqs.Count == 0, "partial frame held back");
        proc.Feed(f1.AsSpan(10), OnFrame, OnLine);
        Check(seqs.Count == 1 && seqs[0] == 1, "frame reassembled across feeds");

        proc.Feed(Encoding.ASCII.GetBytes("OK CONFIG count=14 max=128\r\n"), OnFrame, OnLine);
        Check(lines.Count == 1 && lines[0] == "OK CONFIG count=14 max=128", "reply line captured");

        var f2 = UplinkProtocol.BuildFrame(2, 200, 1, new uint[14]);
        f2[20] ^= 0x55;
        var f3 = UplinkProtocol.BuildFrame(3, 300, 1, new uint[14]);
        proc.Feed(f2, OnFrame, OnLine);
        proc.Feed(f3, OnFrame, OnLine);
        Check(proc.BadChecksums == 1, "bad checksum counted");
        Check(seqs.Count == 2 && seqs[1] == 3, "resync recovered the next frame");

        proc.Feed(Encoding.ASCII.GetBytes("UPLINK seq=3 sent=4 drop=0 stack=900\r\n"), OnFrame, OnLine);
        Check(lines.Count == 2 && lines[1].StartsWith("UPLINK seq=3"), "stray 'P' stripped from reply line");

        // 7. Truncated frame self-heals: a partial frame followed by
        //    enough full frames must resync (the partial bytes are
        //    consumed as garbage; a later frame is decoded). Matches the
        //    real stream: the mod keeps sending frames, so the claimed
        //    length is eventually exceeded and the checksum rejects the
        //    mixed bytes.
        var proc2 = new StreamProcessor();
        var seqs2 = new List<uint>();
        var truncated = UplinkProtocol.BuildFrame(9, 0, 1, new uint[100])[..300]; // cut mid-frame (claimed 418)
        var goodA = UplinkProtocol.BuildFrame(10, 0, 1, new uint[14]);
        var goodB = UplinkProtocol.BuildFrame(11, 0, 1, new uint[14]);
        var goodC = UplinkProtocol.BuildFrame(12, 0, 1, new uint[14]);
        proc2.Feed(truncated, s => seqs2.Add(s.Seq), OnLine);
        proc2.Feed(goodA, s => seqs2.Add(s.Seq), OnLine);
        proc2.Feed(goodB, s => seqs2.Add(s.Seq), OnLine);
        proc2.Feed(goodC, s => seqs2.Add(s.Seq), OnLine);
        Check(seqs2.Count >= 1 && seqs2[^1] == 12 && !seqs2.Contains(9u),
            "truncated frame skipped, later frames decoded");

        // 8. In-frame 'P','M' bytes (sample values) must not break parsing.
        var proc3 = new StreamProcessor();
        var seqs3 = new List<uint>();
        var tricky = UplinkProtocol.BuildFrame(11, 0, 1, new uint[] { 0x4D504D50, 0x504D504D });
        proc3.Feed(tricky, s => seqs3.Add(s.Seq), OnLine);
        Check(seqs3.Count == 1 && seqs3[0] == 11, "frame with 'PM' inside samples parses");

        // 8b. Bad frames reach the diagnostic callback with stored/expected.
        string? badMsg = null;
        var proc4 = new StreamProcessor(msg => badMsg = msg);
        var badFrame = UplinkProtocol.BuildFrame(21, 0, 1, new uint[14]);
        badFrame[20] ^= 0xFF;
        proc4.Feed(badFrame, s => seqs3.Add(s.Seq), OnLine);
        Check(badMsg is not null && badMsg.Contains("seq=21") && badMsg.Contains("stored=0x") && badMsg.Contains("expected=0x"),
            "bad frame reported with stored/expected checksum");

        //------------------------------------------------------------------
        // 9. Command encoding
        //------------------------------------------------------------------
        var begin = UplinkProtocol.ConfigBeginCommand(14);
        Check(begin.SequenceEqual(new byte[] { 0x10, 0x01, 0x0E, 0x00, 0x00 }), "CONFIG_BEGIN encoding");

        var entry = UplinkProtocol.ConfigEntryCommand(11, 0x02354138, 1,
            new List<(ushort, bool)> { (0, true), (0x749, false) });
        var expectedEntry = new byte[]
        {
            0x11, 0x0B, 0x00, 0x38, 0x41, 0x35, 0x02, 0x08, // idx=11, base, meta: w=1(0), S=2(<<2)=8
            0x00, 0x00, 0x01, // step 0: offset 0, deref
            0x49, 0x07, 0x00, // step 1: offset 0x749, no deref
        };
        Check(entry.SequenceEqual(expectedEntry), "CONFIG_ENTRY encoding (2-step path)");

        //------------------------------------------------------------------
        // 10. Address config parsing + validation
        //------------------------------------------------------------------
        const string json = """
            {
              "version": 1,
              "comment": "test",
              "addresses": [
                { "name": "direct", "address": "0x022ABFD4", "width": 4, "comment": "hi" },
                { "name": "path", "path": ["0x02354138", "*+0x0", "+0x749"], "width": 1 },
                { "name": "bare_star", "path": ["0x02354138", "*"] }
              ]
            }
            """;
        AddressConfig? cfg = null;
        try
        {
            cfg = AddressConfig.Parse(json);
        }
        catch (Exception e)
        {
            Check(false, $"config parse: {e.Message}");
        }
        Check(cfg is not null && cfg.Count == 3, "config parse (3 entries)");
        if (cfg is not null)
        {
            Check(cfg.Names[0] == "direct" && cfg.Addresses[0].Comment == "hi", "config names/comments");
            var p = cfg.Addresses[1];
            Check(p.BaseAddress == 0x02354138 && p.Steps.Count == 2 &&
                  p.Steps[0] == new PathStep(0, true) && p.Steps[1] == new PathStep(0x749, false),
                  "path step parsing");
            var star = cfg.Addresses[2];
            Check(star.Steps.Count == 1 && star.Steps[0] == new PathStep(0, true), "bare '*' = deref at cur+0");
            Check(cfg.WireSize() == 8 + 8 + 6 + 8 + 3, "wire size (8 + 8+6 + 8+3)");

            var batches = cfg.BuildBatches();
            Check(batches.Count == 1, "small config packs into one batch (3 entries, 33 bytes <= cap)");
            Check(batches.Sum(b => b.Length) == cfg.WireSize(), "batches cover all entries");
        }

        // 11. Config validation errors.
        void CheckThrows(string j, string what)
        {
            try
            {
                AddressConfig.Parse(j);
                Check(false, $"{what} (no exception)");
            }
            catch (AddressConfig.ConfigException)
            {
                Check(true, what);
            }
        }
        CheckThrows("""{ "version": 2, "addresses": [ { "name": "a", "address": "0x02000000" } ] }""",
            "wrong version rejected");
        CheckThrows("""{ "version": 1, "addresses": [ { "name": "a", "address": "0x04000000" } ] }""",
            "non-RAM address rejected");
        CheckThrows("""{ "version": 1, "addresses": [ { "name": "a", "address": "0x02000000" }, { "name": "a", "address": "0x02000001" } ] }""",
            "duplicate name rejected");
        CheckThrows("""{ "version": 1, "addresses": [ { "name": "a", "address": "0x02000000", "path": ["0x02000001"] } ] }""",
            "address+path rejected");
        CheckThrows("""{ "version": 1, "addresses": [ { "name": "a", "path": ["0x02000000", "+0x8000"] } ] }""",
            "offset > 0x7FFF rejected");
        CheckThrows("""{ "version": 1, "addresses": [ { "name": "a", "path": ["0x02000000", "+0x1", "+0x1", "+0x1", "+0x1", "+0x1"] } ] }""",
            "5 steps rejected");
        CheckThrows("""{ "version": 1, "addresses": [ { "name": "a", "address": "0x02000000", "width": 3 } ] }""",
            "width 3 rejected");

        // 12b. Pointer-path bases are pointer locations, not reads: they
        //      are accepted anywhere (the mod samples 0 until the chain
        //      lands in RAM). Only direct "address" entries are checked.
        var loosePath = AddressConfig.Parse(
            """{ "version": 1, "addresses": [ { "name": "p", "path": ["0x99999999", "*+0x0"] } ] }""");
        Check(loosePath.Count == 1 && loosePath.Addresses[0].BaseAddress == 0x99999999,
            "out-of-RAM path base accepted");

        // 12. Example/default file parses and encodes.
        try
        {
            var example = AddressConfig.Parse(AddressConfig.ExampleJson, "example");
            Check(example.Count == 14, "example config parses (14 entries)");
            var eb = example.BuildBatches();
            Check(eb.Count > 1 && eb.Count <= 4, "example config splits into a few single-packet batches (14 entries, ~150 B)");
            Check(eb.Sum(b => b.Length) == example.WireSize(), "example batches cover all entries");
            Check(eb.All(b => b.Length <= UplinkProtocol.BatchByteCap), "every batch fits one USB packet (<= BatchByteCap)");
        }
        catch (Exception e)
        {
            Check(false, $"example config: {e.Message}");
        }

        //------------------------------------------------------------------
        // 13. Memory-map block (format v3: name -> value table)
        //------------------------------------------------------------------

        // Consumer-side helpers, mirroring how the autosplitter reads the
        // block: validate the header, verify the checksum, look up by name.
        static ushort StoredChecksum(byte[] b) =>
            BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(UplinkMemoryMarker.ChecksumOffset));
        static ushort ComputedChecksum(byte[] b)
        {
            uint sum = 0;
            for (int i = 0; i < UplinkMemoryMarker.ChecksumOffset; i++)
                sum += b[i];
            return (ushort)sum;
        }
        static bool BlockLayoutOk(byte[] b) =>
            b.Length == UplinkMemoryMarker.BlockSize
            && b[0] == (byte)'P' && b[1] == (byte)'M'
            && b[2] == UplinkMemoryMarker.FormatVersion && b[3] == 0
            && BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)) >= 1
            && BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4)) <= UplinkMemoryMarker.MaxEntries
            && StoredChecksum(b) == ComputedChecksum(b)
            && (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(UplinkMemoryMarker.SeqlockOffset)) & 1) == 0;
        static ulong? Lookup(byte[] b, int count, string name)
        {
            var nb = Encoding.ASCII.GetBytes(name);
            for (int i = 0; i < count; i++)
            {
                int off = UplinkMemoryMarker.EntriesOffset + i * UplinkMemoryMarker.EntryStride;
                if (nb.Length <= UplinkMemoryMarker.MaxNameLen
                    && b.AsSpan(off, nb.Length).SequenceEqual(nb)
                    && b[off + nb.Length] == 0)
                {
                    return BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(off + 36));
                }
            }
            return null;
        }

        // UpdateValues before any PublishTable describes a table: no-op.
        UplinkMemoryMarker.UpdateValues(new uint[] { 1 }, seq: 1, gameFrame: 1, epoch: 1);
        var fresh = UplinkMemoryMarker.Snapshot();
        Check(fresh.Length == UplinkMemoryMarker.BlockSize, "block size 6168");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(fresh.AsSpan(4)) == 0,
            "update before publish is a no-op");

        var tableNames = new[] { "alpha", "beta", "gamma" };
        var tableWidths = new[] { 4, 1, 2 };
        UplinkMemoryMarker.PublishTable(tableNames, tableWidths, epoch: 5);
        var snap = UplinkMemoryMarker.Snapshot();
        Check(BlockLayoutOk(snap), "published block: header/checksum/seqlock");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(snap.AsSpan(4)) == 3, "published entry_count");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(snap.AsSpan(6)) == 5, "published epoch");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(8)) == 0, "published seq = 0");
        Check(snap[UplinkMemoryMarker.EntriesOffset + 5] == 0, "published name NUL-padded");
        Check(snap[UplinkMemoryMarker.EntriesOffset + 32] == 4, "published width");
        Check(Lookup(snap, 3, "beta") == 0, "published values are zero");
        Check(Lookup(snap, 3, "missing") is null, "unknown name not found");
        Check(snap[UplinkMemoryMarker.EntriesOffset + 3 * UplinkMemoryMarker.EntryStride] == 0,
            "unused slot zeroed");

        UplinkMemoryMarker.UpdateValues(new uint[] { 0xDEADBEEF, 0x7F, 0x8000 },
            seq: 9, gameFrame: 1234, epoch: 5);
        snap = UplinkMemoryMarker.Snapshot();
        Check(BlockLayoutOk(snap), "updated block: header/checksum/seqlock");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(8)) == 9, "updated seq");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(snap.AsSpan(12)) == 1234, "updated game_frame");
        Check(Lookup(snap, 3, "alpha") == 0xDEADBEEF, "width-4 value");
        Check(Lookup(snap, 3, "beta") == 0x7F, "width-1 value zero-extended");
        Check(Lookup(snap, 3, "gamma") == 0x8000, "width-2 value zero-extended");
        Check(snap[UplinkMemoryMarker.EntriesOffset + 32] == 4, "width unchanged by update");

        bool threw = false;
        try { UplinkMemoryMarker.UpdateValues(new uint[] { 1, 2 }, seq: 1, gameFrame: 1, epoch: 5); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "UpdateValues rejects a mismatched sample count");

        threw = false;
        try { UplinkMemoryMarker.PublishTable(new[] { new string('a', 32) }, new[] { 4 }, epoch: 1); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "PublishTable rejects a 32-character name");

        threw = false;
        try { UplinkMemoryMarker.PublishTable(new[] { "x" }, new[] { 3 }, epoch: 1); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "PublishTable rejects width 3");

        var bigNames = Enumerable.Range(0, UplinkMemoryMarker.MaxEntries + 1).Select(i => $"n{i}").ToArray();
        var bigWidths = Enumerable.Repeat(4, bigNames.Length).ToArray();
        threw = false;
        try { UplinkMemoryMarker.PublishTable(bigNames, bigWidths, epoch: 1); }
        catch (ArgumentException) { threw = true; }
        Check(threw, "PublishTable rejects over-capacity tables");

        // Republishing replaces the table structure (and clears unused slots).
        UplinkMemoryMarker.PublishTable(new[] { "delta" }, new[] { 2 }, epoch: 6);
        snap = UplinkMemoryMarker.Snapshot();
        Check(BlockLayoutOk(snap), "republished block: header/checksum/seqlock");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(snap.AsSpan(4)) == 1, "republished entry_count");
        Check(Lookup(snap, 1, "delta") == 0, "republished table present");
        Check(Lookup(snap, 1, "alpha") is null, "old entry gone after republish");

        // Config-level name/capacity validation.
        CheckThrows(
            """{ "version": 1, "addresses": [ { "name": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "address": "0x02000000" } ] }""",
            "32-character name rejected");
        CheckThrows("""{ "version": 1, "addresses": [ { "name": "café", "address": "0x02000000" } ] }""",
            "non-ASCII name rejected");
        var tooMany = string.Join(", ", Enumerable.Range(0, UplinkMemoryMarker.MaxEntries + 1)
            .Select(i => $"{{ \"name\": \"n{i}\", \"address\": \"0x02000000\" }}"));
        CheckThrows($"{{ \"version\": 1, \"addresses\": [ {tooMany} ] }}", "over-capacity table rejected");

        string summary = failures == 0 ? "all self-tests passed" : $"{failures} self-test(s) FAILED";
        Console.WriteLine(summary);
        report.AppendLine(summary);
        // Also persist the report: task/CI runs may not capture stdout.
        try
        {
            File.WriteAllText("selftest-results.txt", report.ToString());
        }
        catch
        {
            // read-only dir etc.; stdout already has the results
        }
        return failures == 0 ? 0 : 1;
    }
}

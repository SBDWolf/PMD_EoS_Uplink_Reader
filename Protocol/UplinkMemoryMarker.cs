using System;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace UplinkReader.Protocol;

public static unsafe class UplinkMemoryMarker
{
    /// <summary>
    /// Name the memory-mapped file is published under. The WASM
    /// autosplitter cannot open named kernel objects, so it finds the
    /// block by scanning this process's memory; ASL-style consumers can
    /// open it by name.
    /// </summary>
    public const string MapName = "PMUplinkFrame";

    // Layout (format v3, a self-describing name -> value table):
    //   [0 .. 15]     header:
    //                   [0..1]   magic 'P' 'M'
    //                   [2]      format version (0x03)
    //                   [3]      flags (0)
    //                   [4..5]   entry_count u16 LE
    //                   [6..7]   epoch u16 LE (mod's CONFIG_COMMIT counter)
    //                   [8..11]  seq u32 LE (device frame sequence)
    //                   [12..15] game_frame u32 LE (PLAY_TIME seconds*60 + frames)
    //   [16 + 48*i]   entry i (48 bytes):
    //                   [0..31]  name, ASCII, NUL-padded (max 31 chars)
    //                   [32]     width (1, 2 or 4)
    //                   [33..35] reserved (0)
    //                   [36..43] value u64 LE (sample, zero-extended)
    //                   [44..47] reserved (0)
    //   [6160..6161]  checksum u16 LE (byte sum of [0..6160))
    //   [6164..6167]  u32 seqlock (odd = update in progress)
    public const int HeaderLen = 16;
    public const int EntryStride = 48;
    public const int NameLen = 32;
    public const int MaxEntries = 128;
    public const int EntriesOffset = HeaderLen;
    public const int EntriesLen = MaxEntries * EntryStride;
    public const int ChecksumOffset = EntriesOffset + EntriesLen; // 6160
    public const int SeqlockOffset = ChecksumOffset + 4;          // 6164 (2 bytes padding)
    public const int BlockSize = SeqlockOffset + sizeof(uint);    // 6168
    public const byte FormatVersion = 0x03;
    public const int MaxNameLen = NameLen - 1; // keep at least one NUL terminator

    private static readonly MemoryMappedFile _mmf;
    private static readonly MemoryMappedViewAccessor _accessor;
    private static readonly byte* _block;

    /// <summary>Entries described in the block (0 until the first PublishTable).</summary>
    private static int _entryCount;

    static UplinkMemoryMarker()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, BlockSize, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, BlockSize, MemoryMappedFileAccess.ReadWrite);

        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _block = ptr;

        new Span<byte>(_block, BlockSize).Clear();

        // The block stays zero until the first PublishTable() (no magic,
        // no entries). Both _mmf and _accessor are intentionally never
        // disposed — kept alive for the process lifetime, same as before.
    }

    /// <summary>
    /// Writes the table structure: the header (entry count + epoch) and one
    /// entry per name with its width, values zeroed, every unused slot
    /// cleared. Call whenever the streamed table (re)starts — the caller
    /// tracks the mod's epoch and republishes when it changes. Values are
    /// filled in by <see cref="UpdateValues"/>.
    /// </summary>
    public static void PublishTable(IReadOnlyList<string> names, IReadOnlyList<int> widths, int epoch)
    {
        if (names.Count != widths.Count)
            throw new ArgumentException("names and widths must have the same length", nameof(widths));
        if (names.Count > MaxEntries)
            throw new ArgumentException(
                $"{names.Count} entries exceeds the block capacity ({MaxEntries})", nameof(names));
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].Length > MaxNameLen)
                throw new ArgumentException(
                    $"name \"{names[i]}\" is longer than {MaxNameLen} characters", nameof(names));
            if (widths[i] is not (1 or 2 or 4))
                throw new ArgumentException($"width {widths[i]} is not 1, 2 or 4", nameof(widths));
        }

        BumpSeqlock(); // odd = update in progress
        new Span<byte>(_block, SeqlockOffset).Clear();
        WriteHeader(names.Count, epoch, seq: 0, gameFrame: 0);
        for (int i = 0; i < names.Count; i++)
            WriteEntry(i, names[i], widths[i], value: 0);
        WriteChecksum();
        _entryCount = names.Count; // the block now describes this table
        BumpSeqlock(); // even = stable
    }

    /// <summary>
    /// Updates the header fields and every entry's value from one decoded
    /// frame (names/widths stay as published). The sample order must match
    /// the published table — the caller only calls this once the frame's
    /// sample count matches the loaded address table. A call before any
    /// PublishTable is a no-op.
    /// </summary>
    public static void UpdateValues(ReadOnlySpan<uint> samples, uint seq, uint gameFrame, int epoch)
    {
        if (_entryCount == 0)
            return; // nothing published yet
        if (samples.Length != _entryCount)
            throw new ArgumentException(
                $"frame has {samples.Length} samples but the published table has {_entryCount} entries",
                nameof(samples));

        BumpSeqlock(); // odd = update in progress
        WriteHeader(_entryCount, epoch, seq, gameFrame);
        for (int i = 0; i < _entryCount; i++)
        {
            byte* entry = _block + EntriesOffset + i * EntryStride;
            BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(entry + 36, 8), samples[i]);
        }
        WriteChecksum();
        BumpSeqlock(); // even = stable
    }

    /// <summary>A copy of the whole block (for tests and diagnostics).</summary>
    public static byte[] Snapshot()
    {
        var copy = new byte[BlockSize];
        new ReadOnlySpan<byte>(_block, BlockSize).CopyTo(copy);
        return copy;
    }

    private static void WriteHeader(int count, int epoch, uint seq, uint gameFrame)
    {
        _block[0] = UplinkProtocol.Magic0;
        _block[1] = UplinkProtocol.Magic1;
        _block[2] = FormatVersion;
        _block[3] = 0; // flags
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_block + 4, 2), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_block + 6, 2), (ushort)epoch);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(_block + 8, 4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(_block + 12, 4), gameFrame);
    }

    private static void WriteEntry(int index, string name, int width, uint value)
    {
        byte* entry = _block + EntriesOffset + index * EntryStride;
        // ASCII name, NUL-padded (non-ASCII input is rejected by the
        // config loader; Encoding.ASCII maps any leftover to '?').
        int written = Encoding.ASCII.GetBytes(name, new Span<byte>(entry, NameLen));
        new Span<byte>(entry + written, NameLen - written).Clear();
        entry[NameLen] = (byte)width; // [32] width, [33..35] reserved (cleared)
        BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(entry + 36, 8), value);
        // [44..47] reserved (cleared)
    }

    /// <summary>u16 byte sum of the checksummed region (same style as the wire frames).</summary>
    private static ushort ComputeChecksum()
    {
        uint sum = 0;
        for (int i = 0; i < ChecksumOffset; i++)
            sum += _block[i];
        return (ushort)sum;
    }

    private static void WriteChecksum() =>
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_block + ChecksumOffset, 2), ComputeChecksum());

    private static void BumpSeqlock()
    {
        ref int sequence = ref Unsafe.AsRef<int>(_block + SeqlockOffset);
        Interlocked.Increment(ref sequence);
    }
}

using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace UplinkReader.Protocol;

public static unsafe class UplinkMemoryMarker
{
    /// <summary>
    /// Name the autosplitter opens via MemoryMappedFile.OpenExisting.
    /// No magic bytes needed anymore — this name is the identifier.
    /// </summary>
    public const string MapName = "PMUplinkFrame";

    public const int FrameOffset = 0;
    public const int SeqlockOffset = FrameOffset + UplinkProtocol.FrameLen; // 70
    public const int BlockSize = SeqlockOffset + sizeof(uint);              // 74

    private static readonly MemoryMappedFile _mmf;
    private static readonly MemoryMappedViewAccessor _accessor;
    private static readonly byte* _block;

    static UplinkMemoryMarker()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, BlockSize, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, BlockSize, MemoryMappedFileAccess.ReadWrite);

        byte* ptr = null;
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _block = ptr;

        new Span<byte>(_block, BlockSize).Clear();

        // Frame and seqlock remain zero until the first Update().
        // Both _mmf and _accessor are intentionally never disposed —
        // kept alive for the process lifetime, same as before.
    }

    public static void Update(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != UplinkProtocol.FrameLen)
            throw new ArgumentException($"expected {UplinkProtocol.FrameLen} bytes", nameof(frame));

        BumpSeqlock(); // odd = update in progress
        frame.CopyTo(new Span<byte>(_block + FrameOffset, UplinkProtocol.FrameLen));
        BumpSeqlock(); // even = stable
        Console.WriteLine(frame.ToString());
    }

    private static void BumpSeqlock()
    {
        ref int sequence = ref Unsafe.AsRef<int>(_block + SeqlockOffset);
        Interlocked.Increment(ref sequence);
    }
}
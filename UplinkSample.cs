namespace UplinkReader;

/// <summary>
/// One decoded variable-length uplink frame (protocol v2). The values map
/// 1:1 to the address table the reader uploaded to the mod (see
/// UplinkClient.ActiveConfig); use the config's Names to label RawValues.
/// A sample is 0 when its pointer path failed a safety check that frame.
/// </summary>
public sealed class UplinkSample
{
    public required uint Seq { get; init; }

    /// <summary>PLAY_TIME at sample time: seconds*60 + frames.</summary>
    public required uint GameFrame { get; init; }

    /// <summary>Bumped by the mod on every CONFIG_COMMIT (detects reconfigures).</summary>
    public required int Epoch { get; init; }

    /// <summary>Number of samples in this frame (= the uploaded entry count).</summary>
    public required int SampleCount { get; init; }

    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;

    public required uint[] RawValues { get; init; }

    /// <summary>The raw frame bytes as received (published to the memory map).</summary>
    public required byte[] RawFrame { get; init; }

    public uint this[int slot] => RawValues[slot];

    /// <summary>GameFrame as h:mm:ss (seconds*60 + frames, like the mod's PLAY_TIME).</summary>
    public string FormatGameTime()
    {
        long totalSeconds = GameFrame / 60;
        int frames = (int)(GameFrame % 60);
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}:{frames:00}";
    }
}

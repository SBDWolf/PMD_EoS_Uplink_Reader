namespace UplinkReader;

/// <summary>
/// One decoded 70-byte uplink frame. The named properties mirror the
/// mod's default sample slots (uplink_sampler_init in
/// src/uplink/uplink_sampler.c); RawValues keeps the raw slot array in
/// case slots are retargeted on-device.
/// </summary>
public sealed class UplinkSample
{
    public required uint Seq { get; init; }

    /// <summary>PLAY_TIME at sample time: seconds*60 + frames.</summary>
    public required uint GameFrame { get; init; }

    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;

    public required uint[] RawValues { get; init; }

    /// <summary>Default slot names, in slot order (0-13).</summary>
    public static readonly string[] SlotNames =
    {
        "PLAY_TIME_SECONDS", "PLAY_TIME_FRAME_COUNTER", "SCENARIO_MAIN_FLAG_MAIN",
        "SCENARIO_MAIN_FLAG_SUB", "REQUEST_CLEAR_COUNT", "REQUEST_CLEAR_COUNT_U16",
        "magic_number", "overlay1_start", "dungeon_ptr",
        "script_id_part1", "script_id_part2", "dungeon_is_clearing_floor",
        "dungeon_current_dungeon_id", "dungeon_current_floor",
    };

    public uint PlayTimeSeconds => RawValues[0];
    public uint PlayTimeFrameCounter => RawValues[1];
    public uint ScenarioMainFlagMain => RawValues[2];
    public uint ScenarioMainFlagSub => RawValues[3];
    public uint RequestClearCount => RawValues[4];
    public uint RequestClearCountU16 => RawValues[5];
    public uint MagicNumber => RawValues[6];
    public uint Overlay1Start => RawValues[7];
    public uint DungeonPtr => RawValues[8];
    public uint ScriptIdPart1 => RawValues[9];
    public uint ScriptIdPart2 => RawValues[10];
    public uint DungeonIsClearingFloor => RawValues[11];
    public uint DungeonCurrentDungeonId => RawValues[12];
    public uint DungeonCurrentFloor => RawValues[13];

    public uint this[int slot] => RawValues[slot];

    /// <summary>GameFrame as h:mm:ss (seconds*60 + frames, like the mod's PLAY_TIME).</summary>
    public string FormatGameTime()
    {
        long totalSeconds = GameFrame / 60;
        int frames = (int)(GameFrame % 60);
        return $"{totalSeconds / 60}:{totalSeconds % 60:00}:{frames:00}";
    }
}

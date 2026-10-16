using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using UplinkReader.Protocol;

namespace UplinkReader.Config;

/// <summary>One step of a pointer path: add <c>Offset</c>, then optionally dereference.</summary>
public readonly record struct PathStep(ushort Offset, bool Deref);

/// <summary>One configured address: a direct address or a pointer path.</summary>
public sealed class AddressEntry
{
    public required string Name { get; init; }
    public required uint BaseAddress { get; init; }
    public required IReadOnlyList<PathStep> Steps { get; init; }
    /// <summary>Read width in bytes: 1, 2 or 4.</summary>
    public required int Width { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// The human-editable address table (addresses.json) uploaded to the mod
/// on every connect. Schema (see addresses.example.json):
///   {
///     "version": 1,
///     "comment": "optional free-text note",
///     "addresses": [
///       { "name": "...", "address": "0x022ABFD4", "width": 4, "comment": "..." },
///       { "name": "...", "path": ["0x02354138", "*+0x0", "+0x749"], "width": 1 }
///     ]
///   }
/// A "path" starts with the base address; every following element is a
/// step: "*+0xN" dereferences at cur+N, "+0xN" only adds N, and a bare "*"
/// dereferences at cur+0. The mod re-walks paths every frame and samples 0
/// whenever a dereference or the final read would leave ARM9 main RAM
/// (null pointers included), so paths are safe to point at "maybe
/// uninitialized" pointers. A path's BASE is a pointer location, not a
/// read, so it may be any address — only direct "address" entries are
/// RAM-checked at load time.
/// </summary>
public sealed class AddressConfig
{
    public required int Version { get; init; }
    public string? Comment { get; init; }
    public required IReadOnlyList<AddressEntry> Addresses { get; init; }

    public int Count => Addresses.Count;

    /// <summary>Names in slot order (index = sample index in each frame).</summary>
    public IReadOnlyList<string> Names => _names ??= Addresses.Select(a => a.Name).ToArray();
    private string[]? _names;

    /// <summary>Read widths in slot order (index = sample index in each frame).</summary>
    public IReadOnlyList<int> Widths => _widths ??= Addresses.Select(a => a.Width).ToArray();
    private int[]? _widths;

    //------------------------------------------------------------------
    // Loading
    //------------------------------------------------------------------

    private sealed class FileDto
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("comment")] public string? Comment { get; set; }
        [JsonPropertyName("addresses")] public List<EntryDto>? Addresses { get; set; }
    }

    private sealed class EntryDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("path")] public List<string>? Path { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("comment")] public string? Comment { get; set; }
    }

    public sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message) { }
    }

    public static AddressConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new ConfigException($"address file not found: {path}");
        string json = File.ReadAllText(path);
        return Parse(json, path);
    }

    public static AddressConfig Parse(string json, string? source = null)
    {
        FileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FileDto>(json, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new ConfigException($"{source ?? "address file"}: invalid JSON: {e.Message}");
        }
        if (dto is null)
            throw new ConfigException($"{source ?? "address file"}: empty file");
        if (dto.Version != UplinkProtocol.ConfigVersion)
            throw new ConfigException($"{source ?? "address file"}: unsupported version {dto.Version} (expected {UplinkProtocol.ConfigVersion})");
        if (dto.Addresses is null || dto.Addresses.Count == 0)
            throw new ConfigException($"{source ?? "address file"}: no addresses");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<AddressEntry>(dto.Addresses.Count);
        for (int i = 0; i < dto.Addresses.Count; i++)
        {
            var d = dto.Addresses[i];
            string where = $"{source ?? "address file"}: entry {i}";
            string name = d.Name?.Trim() ?? "";
            if (name.Length == 0)
                throw new ConfigException($"{where}: missing \"name\"");
            if (!seen.Add(name))
                throw new ConfigException($"{where} (\"{name}\"): duplicate name");
            if (name.Length > UplinkMemoryMarker.MaxNameLen)
                throw new ConfigException(
                    $"{where} (\"{name}\"): name is {name.Length} characters (max {UplinkMemoryMarker.MaxNameLen} - the memory-map name field is fixed-width)");
            if (name.Any(c => c > 0x7F))
                throw new ConfigException(
                    $"{where} (\"{name}\"): name must be plain ASCII (memory-map consumers match it byte-for-byte)");

            bool hasAddress = !string.IsNullOrWhiteSpace(d.Address);
            bool hasPath = d.Path is { Count: > 0 };
            if (hasAddress == hasPath)
                throw new ConfigException($"{where} (\"{name}\"): exactly one of \"address\" or \"path\" is required");

            uint baseAddress;
            var steps = new List<PathStep>();
            if (hasAddress)
            {
                baseAddress = ParseHex(d.Address!, $"{where} (\"{name}\") address", requireRam: true);
            }
            else
            {
                // Path bases are pointer locations, not reads: accept any
                // address (the mod samples 0 until the chain lands in RAM).
                baseAddress = ParseHex(d.Path![0], $"{where} (\"{name}\") path[0]", requireRam: false);
                for (int s = 1; s < d.Path.Count; s++)
                    steps.Add(ParseStep(d.Path[s], $"{where} (\"{name}\") path[{s}]"));
            }

            int width = d.Width ?? 4;
            if (width is not (1 or 2 or 4))
                throw new ConfigException($"{where} (\"{name}\"): width must be 1, 2 or 4 (got {width})");

            if (steps.Count > UplinkProtocol.MaxSteps)
                throw new ConfigException($"{where} (\"{name}\"): {steps.Count} path steps (max {UplinkProtocol.MaxSteps})");

            entries.Add(new AddressEntry
            {
                Name = name,
                BaseAddress = baseAddress,
                Steps = steps,
                Width = width,
                Comment = string.IsNullOrWhiteSpace(d.Comment) ? null : d.Comment!.Trim(),
            });
        }

        if (entries.Count > UplinkProtocol.MaxSamples)
            throw new ConfigException(
                $"{source ?? "address file"}: {entries.Count} addresses (protocol max {UplinkProtocol.MaxSamples})");
        if (entries.Count > UplinkMemoryMarker.MaxEntries)
            throw new ConfigException(
                $"{source ?? "address file"}: {entries.Count} addresses (memory-map capacity {UplinkMemoryMarker.MaxEntries})");

        return new AddressConfig
        {
            Version = dto.Version,
            Comment = string.IsNullOrWhiteSpace(dto.Comment) ? null : dto.Comment!.Trim(),
            Addresses = entries,
        };
    }

    private static uint ParseHex(string text, string where, bool requireRam)
    {
        string t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("&H", StringComparison.OrdinalIgnoreCase))
            t = t[2..];
        if (!uint.TryParse(t, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint v))
            throw new ConfigException($"{where}: \"{text}\" is not a hex address");
        if (requireRam && (v < 0x02000000u || v > 0x023FFFFFu))
            throw new ConfigException($"{where}: 0x{v:X8} is outside ARM9 main RAM (0x02000000-0x023FFFFF)");
        return v;
    }

    private static PathStep ParseStep(string text, string where)
    {
        string t = text.Trim();
        bool deref = t.StartsWith('*');
        if (deref)
            t = t[1..];
        if (t.Length == 0)
            return new PathStep(0, deref); // bare "*"
        if (!t.StartsWith('+'))
            throw new ConfigException($"{where}: \"{text}\" must be \"*+0xN\", \"+0xN\" or \"*\"");
        t = t[1..];
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t[2..];
        if (!uint.TryParse(t, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint v))
            throw new ConfigException($"{where}: \"{text}\" offset is not hex");
        if (v > UplinkProtocol.MaxOffset)
            throw new ConfigException($"{where}: offset 0x{v:X} > 0x{UplinkProtocol.MaxOffset:X}");
        return new PathStep((ushort)v, deref);
    }

    //------------------------------------------------------------------
    // Wire encoding
    //------------------------------------------------------------------

    /// <summary>Encodes entry <paramref name="index"/> as a CONFIG_ENTRY command.</summary>
    public byte[] EncodeEntryCommand(int index)
    {
        var e = Addresses[index];
        return UplinkProtocol.ConfigEntryCommand(index, e.BaseAddress, e.Width,
            e.Steps.Select(s => (s.Offset, s.Deref)).ToList());
    }

    /// <summary>
    /// Splits the entries into host command batches. ONE ENTRY PER BATCH:
    /// each serial Write is then a single USB packet (8-14 bytes), which
    /// is the only delivery mode the mod has ever processed reliably
    /// (multi-packet writes — the old ≤440-byte batches = 3+ USB packets
    /// — never reached the mod's parser across 200+ sends, while every
    /// single-packet command did). Even if the OS driver coalesces
    /// adjacent writes, a few 8-14-byte entries still fit one 64-byte
    /// packet.
    /// </summary>
    public List<byte[]> BuildBatches()
    {
        var batches = new List<byte[]>(Count);
        for (int i = 0; i < Count; i++)
            batches.Add(EncodeEntryCommand(i));
        return batches;
    }

    /// <summary>Total wire size of the whole config (for pre-upload checks).</summary>
    public int WireSize() => Enumerable.Range(0, Count).Sum(i => EncodeEntryCommand(i).Length);

    //------------------------------------------------------------------
    // Default file (written on first run; same content as
    // addresses.example.json — the mod's original 14 built-in samples)
    //------------------------------------------------------------------

    public const string ExampleJson = """
        {
          "version": 1,
          "comment": "Default address table: the mod's original 14 built-in samples (EU). Edit freely - the reader uploads this to the DS on every connect.",
          "addresses": [
            { "name": "play_time_seconds",      "address": "0x022ABFD4", "width": 4, "comment": "PLAY_TIME seconds" },
            { "name": "play_time_frame_counter","address": "0x022ABFD8", "width": 1, "comment": "PLAY_TIME frames (0-59)" },
            { "name": "scenario_main_flag_main","address": "0x022ABAA8", "width": 1 },
            { "name": "scenario_main_flag_sub", "address": "0x022ABAA9", "width": 1 },
            { "name": "request_clear_count",    "address": "0x022ABADB", "width": 1 },
            { "name": "brightness",             "address": "0x022A40E4", "width": 2, "comment": "screen fade value" },
            { "name": "magic_number",           "address": "0x022A3670", "width": 4 },
            { "name": "overlay1_start",         "address": "0x02329D40", "width": 2 },
            { "name": "dungeon_ptr",            "address": "0x02354138", "width": 4, "comment": "raw pointer; 0 outside dungeons" },
            { "name": "current_script_id_part1","address": "0x02325ACA", "width": 4 },
            { "name": "current_script_id_part2","address": "0x02325ACE", "width": 4 },
            { "name": "dungeon_is_clearing_floor", "path": ["0x02354138", "*+0x0", "+0x6"],   "width": 1, "comment": "dungeon_ptr -> +0x6" },
            { "name": "dungeon_current_dungeon_id","path": ["0x02354138", "*+0x0", "+0x748"], "width": 1, "comment": "dungeon_ptr -> +0x748" },
            { "name": "dungeon_current_floor",     "path": ["0x02354138", "*+0x0", "+0x749"], "width": 1, "comment": "dungeon_ptr -> +0x749" }
          ]
        }
        """;

    /// <summary>
    /// Resolves the address table location and creates the default table
    /// if no file exists yet. The table is hardcoded to live next to the
    /// executable (addresses.json in AppContext.BaseDirectory); the build
    /// copies the checked-in repo file there (PreserveNewest), so the
    /// repo file is the editable source of truth and the exe-adjacent
    /// copy is what the reader loads.
    /// </summary>
    public static string EnsureAddressFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "addresses.json");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, ExampleJson);
        }
        return path;
    }
}

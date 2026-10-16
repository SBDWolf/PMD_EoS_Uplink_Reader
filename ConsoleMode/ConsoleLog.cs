using UplinkReader.Link;

namespace UplinkReader.ConsoleMode;

/// <summary>
/// Console log to standard I/O. Compiled in only for Debug builds
/// (every method is a no-op in Release) — the app has no launch
/// parameters to toggle it. Handlers may be called from the client
/// worker thread; Console is thread-safe.
/// </summary>
public static class ConsoleLog
{
    /// <summary>Startup banner: mode line + the configured address table.</summary>
    public static void Banner()
    {
        #if DEBUG
        Console.WriteLine("PMD:EoS uplink reader (debug build; console log on stdout)");
        #endif
    }

    /// <summary>One line per frame: seq, game time, then every named sample.</summary>
    public static void Frame(object? sender, UplinkSample s)
    {
        #if DEBUG
        var client = sender as UplinkClient;
        var names = client?.ActiveConfig?.Names;
        var parts = new string[s.SampleCount];
        for (int i = 0; i < parts.Length; i++)
        {
            string name = names is not null && i < names.Count ? names[i] : $"sample{i}";
            parts[i] = $"{name}={s[i]}";
        }
        Console.WriteLine($"seq={s.Seq,-9} ep={s.Epoch} {s.FormatGameTime()}  {string.Join("  ", parts)}");
        #endif
    }

    /// <summary>Session event line (connect, reconnect, watchdog, ...).</summary>
    public static void Log(object? sender, string message)
    {
        #if DEBUG
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        #endif
    }

    /// <summary>Device line (e.g. the UPLINK ping reply).</summary>
    public static void DeviceLine(object? sender, string line)
    {
        #if DEBUG
        Console.WriteLine($"host> {line}");
        #endif
    }

    /// <summary>Session summary on exit.</summary>
    public static void Summary(UplinkClient client, double seconds)
    {
        #if DEBUG
        double dt = Math.Max(seconds, 1e-9);
        Console.WriteLine();
        Console.WriteLine("--- summary ---");
        Console.WriteLine($"frames received : {client.FramesReceived}");
        Console.WriteLine($"bytes received  : {client.BytesReceived} ({client.BytesReceived / 1024 / dt:F1} KB/s)");
        Console.WriteLine($"checksum errors : {client.ChecksumErrors}");
        Console.WriteLine($"seq gaps        : {client.SeqGaps} ({client.GapFramesLost} frames lost)");
        Console.WriteLine($"device lines    : {client.DeviceLines}");
        Console.WriteLine($"reconnects      : {client.Reconnects}");
        #endif
    }
}
using System.Text.Json;

namespace UplinkReader.Config;

/// <summary>
/// Small JSON config persisted in the platform app-data location
/// (%APPDATA%\PmdskyUplinkReader on Windows, ~/.config/pmdsky-uplink-
/// reader on Linux, ~/Library/Application Support on macOS).
/// </summary>
public sealed class AppConfig
{
    /// <summary>Last port the user selected/connected on.</summary>
    public string? LastPort { get; set; }

    private static string ConfigPath
    {
        get
        {
            string dir;
            if (OperatingSystem.IsWindows())
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PmdskyUplinkReader");
            }
            else if (OperatingSystem.IsMacOS())
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "PmdskyUplinkReader");
            }
            else
            {
                string? xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                dir = string.IsNullOrEmpty(xdg)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".config", "pmdsky-uplink-reader")
                    : Path.Combine(xdg, "pmdsky-uplink-reader");
            }
            return Path.Combine(dir, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch
        {
            // fall through to defaults
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best effort
        }
    }
}

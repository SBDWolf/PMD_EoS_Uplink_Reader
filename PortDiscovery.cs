using System.IO.Ports;
using System.Runtime.Versioning;
using UplinkReader.Protocol;

namespace UplinkReader;

/// <summary>
/// Serial port enumeration with best-effort USB VID/PID identification:
/// registry walk on Windows, /sys on Linux, none on macOS (the user picks
/// the port manually in the UI).
/// </summary>
public static class PortDiscovery
{
    public sealed class PortInfo
    {
        public required string Name { get; init; }
        public ushort? Vid { get; init; }
        public ushort? Pid { get; init; }

        public bool IsUplink => Vid == UplinkProtocol.UpLinkVid && Pid == UplinkProtocol.UpLinkPid;

        public string Display =>
            Vid is { } v && Pid is { } p ? $"{Name}  ({v:X4}:{p:X4})" : Name;
    }

    public static bool VidPidDetectionSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static IReadOnlyList<PortInfo> ListPorts()
    {
        string[] names;
        try
        {
            names = SerialPort.GetPortNames();
        }
        catch
        {
            return Array.Empty<PortInfo>();
        }

        var usb = new Dictionary<string, (ushort Vid, ushort Pid)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (OperatingSystem.IsWindows())
                ScanWindowsRegistry(usb);
            else if (OperatingSystem.IsLinux())
                ScanLinuxSysfs(names, usb);
        }
        catch
        {
            // best effort; fall back to names only
        }

        var list = new List<PortInfo>(names.Length);
        foreach (string name in names)
        {
            if (usb.TryGetValue(name, out var vp))
                list.Add(new PortInfo { Name = name, Vid = vp.Vid, Pid = vp.Pid });
            else
                list.Add(new PortInfo { Name = name });
        }
        return list;
    }

    /// <summary>First port whose VID/PID matches the uplink device, if any.</summary>
    public static string? FindUplinkPort()
    {
        foreach (var p in ListPorts())
        {
            if (p.IsUplink)
                return p.Name;
        }
        return null;
    }

    // Walks HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&PID_yyyy\<inst>
    // and records which COM port each USB device owns.
    [SupportedOSPlatform("windows")]
    private static void ScanWindowsRegistry(Dictionary<string, (ushort Vid, ushort Pid)> into)
    {
        using var usbRoot = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
        if (usbRoot is null)
            return;

        foreach (string devKeyName in usbRoot.GetSubKeyNames())
        {
            if (!TryParseVidPidKey(devKeyName, out ushort vid, out ushort pid))
                continue;
            using var devKey = usbRoot.OpenSubKey(devKeyName);
            if (devKey is null)
                continue;
            foreach (string instName in devKey.GetSubKeyNames())
            {
                using var instKey = devKey.OpenSubKey(instName);
                using var parameters = instKey?.OpenSubKey(@"Device Parameters");
                string? portName = parameters?.GetValue("PortName") as string
                                 ?? parameters?.GetValue("InterfaceName") as string;
                if (!string.IsNullOrEmpty(portName))
                    into[portName] = (vid, pid);
            }
        }
    }

    private static bool TryParseVidPidKey(string keyName, out ushort vid, out ushort pid)
    {
        vid = 0;
        pid = 0;
        if (!keyName.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
            return false;
        int amp = keyName.IndexOf('&');
        if (amp < 5 || !keyName.AsSpan(amp).StartsWith("&PID_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!ushort.TryParse(keyName.AsSpan(4, amp - 4), System.Globalization.NumberStyles.HexNumber, null, out vid))
            return false;
        return ushort.TryParse(keyName.AsSpan(amp + 5), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    // For each tty, follows /sys/class/tty/<name>/device up to the USB
    // device directory and reads idVendor/idProduct.
    private static void ScanLinuxSysfs(string[] names, Dictionary<string, (ushort Vid, ushort Pid)> into)
    {
        foreach (string name in names)
        {
            try
            {
                var deviceLink = new FileInfo(Path.Combine("/sys/class/tty", name, "device"));
                if (deviceLink.LinkTarget is not { Length: > 0 } target)
                    continue;
                string iface = Path.GetFullPath(Path.Combine("/sys/class/tty", target));
                string? usbDev = Path.GetDirectoryName(iface);
                if (usbDev is null)
                    continue;
                string vidHex = File.ReadAllText(Path.Combine(usbDev, "idVendor")).Trim();
                string pidHex = File.ReadAllText(Path.Combine(usbDev, "idProduct")).Trim();
                into[name] = ((ushort)Convert.ToUInt16(vidHex, 16), (ushort)Convert.ToUInt16(pidHex, 16));
            }
            catch
            {
                // not a USB CDC port
            }
        }
    }
}

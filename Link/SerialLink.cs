using System.IO.Ports;

namespace UplinkReader.Link;

/// <summary>
/// Thin wrapper over the uplink CDC serial port (DSpico-backed, VID
/// 0x2020 / PID 0xD801). Reads are non-blocking: ReadAvailable returns
/// whatever is queued right now.
/// </summary>
public sealed class SerialLink : IDisposable
{
    private readonly SerialPort _port;
    private bool _disposed;

    public string PortName => _port.PortName;

    public SerialLink(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 100,
            WriteTimeout = 1000,
        };
        _port.Open();
    }

    /// <summary>Reads whatever is available right now (0 if nothing is queued).</summary>
    public int ReadAvailable(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int available = _port.BytesToRead;
        if (available == 0)
            return 0;
        int n = Math.Min(available, buffer.Length);
        return _port.Read(buffer, 0, n);
    }

    public void Write(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _port.Write(data, 0, data.Length);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _port.Dispose();
        }
        catch
        {
            // the port may already be gone (device soft reset)
        }
    }
}

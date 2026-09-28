using System.Text;
using UplinkReader.Protocol;

namespace UplinkReader.Link;

public enum UplinkState
{
    Disconnected,
    Scanning,
    Connecting,
    Connected,
}

/// <summary>
/// Owns the uplink session: port scanning, connection, the handshake
/// (START, plus a one-shot PING on first connect), streaming, and
/// automatic reconnect on device loss (soft reset, DS sleep, unplug).
/// Mirrors tools/pc_reader.py: 1 s rescan poll, 0.5 s open settle, and a
/// 3 s silence watchdog while sampling is active.
///
/// Thread-safe surface: Start/Stop/SetWantsConnection can be called from
/// any thread; consume Latest, the counters, and the events (which fire
/// on the worker thread — marshal before touching UI state).
/// </summary>
public sealed class UplinkClient : IDisposable
{
    private const double ReconnectPollSeconds = 1.0;
    private const double OpenSettleSeconds = 0.5;
    private const double SilenceWatchdogSeconds = 3.0;

    private readonly StreamProcessor _processor = new();
    private readonly object _linkLock = new();
    private Thread? _worker;
    private volatile bool _stopRequested;
    private volatile bool _wantsConnection;
    private volatile string? _preferredPort;
    private SerialLink? _link;
    private UplinkState _state = UplinkState.Disconnected;
    private UplinkSample? _latest;
    private DateTime _lastRxUtc = DateTime.MinValue;
    private DateTime _lastScanLogUtc = DateTime.MinValue;
    private uint? _lastSeq;
    private bool _pingSent;
    private long _frames;
    private long _bytes;
    private long _seqGaps;
    private long _gapFrames;
    private long _deviceLines;
    private long _reconnects;

    public UplinkState State => _state;

    public uint MagicNumberMarker = 2127059;

    /// <summary>Most recently decoded frame (null until the first frame).</summary>
    public UplinkSample? Latest => _latest;

    /// <summary>
    /// When true the client scans for the device, connects, and keeps
    /// reconnecting for as long as it is true (soft resets included).
    /// </summary>
    public bool WantsConnection
    {
        get => _wantsConnection;
        set => SetWantsConnection(value);
    }

    /// <summary>Port to use when no VID/PID match is found (manual selection).</summary>
    public string? PreferredPort
    {
        get => _preferredPort;
        set => _preferredPort = value;
    }

    public string? ActivePort => _link?.PortName;

    public long FramesReceived => _frames;
    public long BytesReceived => _bytes;
    public long ChecksumErrors => _processor.BadChecksums;
    public long SeqGaps => _seqGaps;
    public long GapFramesLost => _gapFrames;
    public long DeviceLines => _deviceLines;
    public long Reconnects => _reconnects;

    public event EventHandler<UplinkSample>? FrameReceived;
    public event EventHandler<UplinkState>? StateChanged;
    public event EventHandler<string>? Log;
    public event EventHandler<string>? DeviceLine;

    public void Start()
    {
        if (_worker is not null)
            return;
        _stopRequested = false;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "uplink-client" };
        _worker.Start();
    }

    public void Stop() => _stopRequested = true;

    public void SetWantsConnection(bool wants)
    {
        _wantsConnection = wants;
        if (!wants)
            CloseLink(null);
    }

    public void Dispose()
    {
        _stopRequested = true;
        Thread? worker = _worker;
        if (worker is not null)
        {
            worker.Join(TimeSpan.FromSeconds(3));
            _worker = null;
        }
        CloseLink(null);
    }

    private void WorkerLoop()
    {
        var readBuf = new byte[4096];
        while (!_stopRequested)
        {
            SerialLink? link = _link;
            if (link is not null)
            {
                ReadOnce(link, readBuf);
                continue;
            }

            if (!_wantsConnection)
            {
                SetState(UplinkState.Disconnected);
                Thread.Sleep(100);
                continue;
            }

            SetState(UplinkState.Scanning);
            string? port = PickPort();
            if (port is null)
            {
                LogThrottled($"waiting for device with VID {UplinkProtocol.UpLinkVid:X4}/PID {UplinkProtocol.UpLinkPid:X4} ...");
                Thread.Sleep(TimeSpan.FromSeconds(ReconnectPollSeconds));
                continue;
            }

            SetState(UplinkState.Connecting);
            if (TryOpenPort(port))
                SendSession();
        }
        SetState(UplinkState.Disconnected);
    }

    // The VID/PID match wins (it is the real device); otherwise fall back
    // to the remembered/selected port (also how macOS works, where there
    // is no VID/PID detection).
    private string? PickPort()
    {
        try
        {
            string? match = PortDiscovery.FindUplinkPort();
            if (match is not null)
                return match;
        }
        catch
        {
            // fall through to the remembered port
        }
        return _preferredPort;
    }

    private bool TryOpenPort(string port)
    {
        Thread.Sleep(TimeSpan.FromSeconds(OpenSettleSeconds));
        try
        {
            var link = new SerialLink(port, UplinkProtocol.DefaultBaud);
            lock (_linkLock)
            {
                _link = link;
            }
            _processor.Reset();
            _lastRxUtc = DateTime.MinValue;
            _preferredPort = port; // remember the port that worked
            SetState(UplinkState.Connected);
            LogMessage($"connected on {port}");
            return true;
        }
        catch (Exception e)
        {
            LogMessage($"cannot open {port}: {e.Message}");
            return false;
        }
    }

    private void SendSession()
    {
        SerialLink? link = _link;
        if (link is null)
            return;
        try
        {
            // A reboot resets device state (sampling stops), so START is
            // re-issued on every (re)connect. PING is a one-shot probe:
            // first connect only.
            link.Write(UplinkProtocol.StartCommand);
            if (!_pingSent)
            {
                link.Write(UplinkProtocol.PingCommand);
                _pingSent = true;
                LogMessage("sent START + PING");
            }
            else
            {
                LogMessage("sent START (reconnect)");
            }
        }
        catch (Exception e)
        {
            LogMessage($"command send failed: {e.Message}");
        }
    }

    private void ReadOnce(SerialLink link, byte[] readBuf)
    {
        int n;
        try
        {
            n = link.ReadAvailable(readBuf);
        }
        catch (Exception e)
        {
            if (_link is null)
                return; // closed intentionally from another thread
            Interlocked.Increment(ref _reconnects);
            CloseLink(e.Message);
            return;
        }

        if (n > 0)
        {
            _lastRxUtc = DateTime.UtcNow;
            Interlocked.Add(ref _bytes, n);
            _processor.Feed(readBuf.AsSpan(0, n), OnFrame, OnLine);
            return;
        }

        DateTime last = _lastRxUtc;
        if (last != DateTime.MinValue &&
            (DateTime.UtcNow - last).TotalSeconds >= SilenceWatchdogSeconds)
        {
            if (_link is null)
                return; // closed intentionally from another thread
            Interlocked.Increment(ref _reconnects);
            CloseLink($"no data for {(DateTime.UtcNow - last).TotalSeconds:0.0} s while sampling");
            return;
        }
        Thread.Sleep(10);
    }

    private void OnFrame(byte[] frame)
    {
        var samples = new uint[UplinkProtocol.SampleCount];
        if (!UplinkProtocol.TryParseFrame(frame, out uint seq, out uint gameFrame, samples))
            return; // the processor already validated; belt and braces

        UplinkMemoryMarker.Update(frame); // publish for external memory scanners
        var sample = new UplinkSample
        {
            Seq = seq,
            GameFrame = gameFrame,
            RawValues = samples,
        };
        _latest = sample;
        Interlocked.Increment(ref _frames);
        NoteSeq(seq);
        FrameReceived?.Invoke(this, sample);
    }

    private void NoteSeq(uint seq)
    {
        // Forward jumps are gaps; a reboot restarts seq at a low value,
        // which is not counted (same semantics as pc_reader.py).
        if (_lastSeq is { } last && seq > last + 1)
        {
            Interlocked.Increment(ref _seqGaps);
            Interlocked.Add(ref _gapFrames, (long)(seq - last - 1));
        }
        _lastSeq = seq;
    }

    private void OnLine(byte[] line)
    {
        Interlocked.Increment(ref _deviceLines);
        DeviceLine?.Invoke(this, Encoding.ASCII.GetString(line));
    }

    private void CloseLink(string? reason)
    {
        SerialLink? link;
        lock (_linkLock)
        {
            link = _link;
            _link = null;
        }
        _processor.Reset();
        _lastRxUtc = DateTime.MinValue;
        if (link is not null)
        {
            try
            {
                link.Dispose();
            }
            catch
            {
                // the port is gone anyway
            }
            if (reason is not null)
                LogMessage($"device lost ({reason})");
            SetState(UplinkState.Disconnected);
        }
    }

    private void SetState(UplinkState next)
    {
        if (_state == next)
            return;
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private void LogMessage(string message) => Log?.Invoke(this, message);

    private void LogThrottled(string message)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastScanLogUtc).TotalSeconds >= 5.0)
        {
            _lastScanLogUtc = now;
            LogMessage(message);
        }
    }
}

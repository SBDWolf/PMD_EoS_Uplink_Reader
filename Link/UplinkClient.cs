using System.Text;
using UplinkReader.Config;
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
/// Config upload progress (surfaced in the UI status line).
/// </summary>
public enum UploadPhase
{
    NotStarted,
    /// <summary>Connected; PING sent to check whether the mod's table already matches (reconnect skip).</summary>
    AwaitingConnectPing,
    AwaitingConfigAck,
    SendingEntries,
    AwaitingCommitAck,
    Streaming,
    Failed,
}

/// <summary>
/// Owns the uplink session: port scanning, connection, the config upload
/// (protocol v2: the reader uploads the address table on EVERY connect —
/// the mod's RAM resets on soft reset, so its table never survives), and
/// streaming with automatic reconnect on device loss (soft reset, DS
/// sleep, unplug). Mirrors docs/uplink-protocol.md.
///
/// Thread-safe surface: Start/Stop/SetWantsConnection can be
/// called from any thread; consume Latest, the counters, and the events
/// (which fire on the worker thread — marshal before touching UI state).
/// </summary>
public sealed class UplinkClient : IDisposable
{
    private const double ReconnectPollSeconds = 1.0;
    private const double OpenSettleSeconds = 0.5;
    // The game can be busy (saves, loading screens) and unable to service
    // USB for a while; silence alone is not device loss. 8 s covers the
    // observed busy stretches; a real unplug is detected by the port
    // throwing, not by silence.
    private const double SilenceWatchdogSeconds = 8.0;
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(2);
    // Per-step confirmation window: the mod polls RX every ~250 ms, so a
    // command read + reply normally lands within ~500 ms. On timeout the
    // SAME command is re-sent (every step is idempotent on the mod side),
    // indefinitely — the game may be busy for a long time and the upload
    // must simply wait it out. The pace relaxes with backoff to limit
    // firmware RX-ring pressure during long busy periods.
    private static readonly TimeSpan BatchAckTimeout = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan BatchAckBackoff = TimeSpan.FromSeconds(2);
    private const int ResendsBeforeBackoff = 3;
    // No OK/ERR line despite resends for this long -> one attempt restart
    // (ABORT + drain + BEGIN). Recovers lost BEGINs, index gaps and
    // transport loss. Restarts are uncapped while connected.
    private static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(20);
    // After CONFIG_ABORT, wait long enough for the mod to poll RX once
    // (~250 ms) and drain stale bytes left in its ring by a previous
    // failed attempt (stale entries would otherwise be applied to the
    // fresh table with mismatched indexes -> ERR IDX -> every retry
    // fails the same way).
    private const int DrainSettleMilliseconds = 400;
    // Consecutive ERR replies tolerated before giving up (transient ERRs
    // recover via an attempt restart; a permanent cause is pre-validated
    // before the upload, so 5 in a row means something is truly wrong).
    private const int MaxConsecutiveErrs = 5;

    private readonly StreamProcessor _processor;
    private readonly object _linkLock = new();
    private Thread? _worker;
    private volatile bool _stopRequested;
    private volatile bool _wantsConnection;
    private volatile string? _preferredPort;
    private SerialLink? _link;
    private UplinkState _state = UplinkState.Disconnected;

    // Config upload state (worker thread only)
    private UploadPhase _phase = UploadPhase.NotStarted;
    private AddressConfig? _config;
    private string? _configError;
    private List<byte[]> _batches = new();
    private int _nextBatch;
    private int _expectedEntries; // cumulative entries expected after the pending entry
    private int _batchSends;      // sends of the current step (1 = first)
    private DateTime _ackDeadline = DateTime.MaxValue;
    private DateTime _noProgressDeadline = DateTime.MaxValue;
    private int _consecutiveErrs;
    private int _modMaxEntries = UplinkProtocol.MaxSamples;
    // True for reconnects within the same reader session: the config
    // cannot have changed without an explicit Reload (which restarts the
    // upload anyway), so a matching cfg=A/N verdict may skip the upload.
    // The FIRST connect of a session always uploads (the exe-adjacent
    // addresses.json may have been edited while the reader was closed).
    private bool _reconnectSkipAllowed;

    private UplinkSample? _latest;
    // Memory-map publish state: the table structure is (re)published when
    // the mod's epoch (CONFIG_COMMIT counter) or the entry count changes.
    private int _publishedEpoch = -1;
    private int _publishedCount = -1;
    private DateTime _lastRxUtc = DateTime.MinValue;
    private DateTime _lastScanLogUtc = DateTime.MinValue;
    private DateTime _lastStatsLogUtc = DateTime.MinValue;
    private int _badFrameLogs;
    private uint? _lastSeq;
    private long _frames;
    private long _bytes;
    private long _seqGaps;
    private long _gapFrames;
    private long _deviceLines;
    private long _reconnects;

    public UplinkState State => _state;

    public UplinkClient()
    {
        _processor = new StreamProcessor(OnBadFrame);
    }

    /// <summary>Most recently decoded frame (null until the first frame).</summary>
    public UplinkSample? Latest => _latest;

    /// <summary>The address table currently in use (null when none loaded yet).</summary>
    public AddressConfig? ActiveConfig => _config;

    /// <summary>Config load/parse error (null when the config is fine).</summary>
    public string? ConfigError => _configError;

    public UploadPhase Phase => _phase;

    /// <summary>Max entries the connected mod reported (from OK CONFIG).</summary>
    public int ModMaxEntries => _modMaxEntries;

    /// <summary>
    /// When true the client scans for the device, connects, uploads the
    /// address table and keeps reconnecting for as long as it is true
    /// (soft resets included).
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
    public long Resyncs => _processor.Resyncs;
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
        // The address table is loaded ONCE per reader session: the file
        // location is hardcoded (addresses.json next to the executable)
        // and there is no hot reload — edit the file and restart the
        // reader to apply changes.
        LoadConfig();
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
                StartUpload();
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

    //------------------------------------------------------------------
    // Config upload (protocol v2)
    //------------------------------------------------------------------

    private void LoadConfig()
    {
        _configError = null;
        // A (re)loaded table changes the name -> value mapping: force a
        // republish on the next frame.
        _publishedEpoch = -1;
        _publishedCount = -1;
        try
        {
            string path = AddressConfig.EnsureAddressFile();
            _config = AddressConfig.Load(path);
            LogMessage($"address table: {_config.Count} entries from {path}");
        }
        catch (Exception e)
        {
            _config = null;
            _configError = e.Message;
            LogMessage($"address table error: {e.Message}");
        }
    }

    private void StartUpload()
    {
        if (_config is null)
            LoadConfig();
        if (_config is null)
        {
            _phase = UploadPhase.Failed;
            return; // config error already logged; user fixes the file + Reload
        }

        _batchSends = 0;
        _consecutiveErrs = 0;
        // Reconnect PING-skip: on a reconnect within the same reader
        // session, ask the mod for its table state first — if cfg=A/N
        // already matches, the upload is skipped entirely (a busy game
        // during streaming no longer causes reconnect churn). The first
        // connect of a session always uploads (the exe-adjacent
        // addresses.json may have been edited while the reader was
        // closed).
        if (_reconnectSkipAllowed)
        {
            _phase = UploadPhase.AwaitingConnectPing;
            _ackDeadline = DateTime.UtcNow + AckTimeout;
            _noProgressDeadline = DateTime.UtcNow + NoProgressTimeout;
            SendPing();
            LogMessage("reconnect: checking the mod's table state (PING)");
            return;
        }
        _reconnectSkipAllowed = true; // later connects in this session may skip
        BeginUploadAttempt();
    }

    /// <summary>
    /// Starts one upload attempt (ABORT + drain + BEGIN). Uncapped: every
    /// step is idempotent and the game may be busy for a long time, so
    /// restarts continue while connected. Called from the no-progress
    /// timer, ERR recovery and the connect-PING timeout.
    /// </summary>
    private void BeginUploadAttempt()
    {
        SendConfigBegin();
    }

    private void SendPing()
    {
        try
        {
            _link?.Write(UplinkProtocol.PingCommand);
        }
        catch
        {
            // port may be gone; the reconnect path handles it
        }
    }

    private void SendConfigBegin()
    {
        var link = _link;
        if (link is null || _config is null)
            return;
        try
        {
            _batches = _config.BuildBatches();
            _nextBatch = 0;
            _expectedEntries = 0;
            _batchSends = 0;
            _consecutiveErrs = 0;
            _phase = UploadPhase.AwaitingConfigAck;
            _ackDeadline = DateTime.UtcNow + AckTimeout;
            _noProgressDeadline = DateTime.UtcNow + NoProgressTimeout;
            // CONFIG_ABORT first: silently discards any config state left
            // by a failed attempt, and (with the drain wait below) gives
            // the mod one RX poll to flush stale ring bytes before the
            // fresh BEGIN.
            link.Write(UplinkProtocol.ConfigAbortCommand);
            Thread.Sleep(DrainSettleMilliseconds);
            link.Write(UplinkProtocol.ConfigBeginCommand(_config.Count));
            _batchSends = 1;
            LogMessage($"uploading config: {_config.Count} entries, one packet each");
        }
        catch (Exception e)
        {
            FailUpload($"cannot send CONFIG_BEGIN: {e.Message}");
        }
    }

    private void SendNextBatch()
    {
        var link = _link;
        if (link is null)
            return;
        if (_nextBatch >= _batches.Count)
        {
            // All entries applied: commit. The deadline is the batch one
            // (the mod polls RX every ~250 ms); on timeout the COMMIT is
            // re-sent — the mod re-acks idempotently once its table is
            // complete, so a lost OK STREAMING recovers without
            // discarding the table.
            _phase = UploadPhase.AwaitingCommitAck;
            _ackDeadline = DateTime.UtcNow + BatchAckTimeout;
            _batchSends = 1;
            link.Write(UplinkProtocol.ConfigCommitCommand);
            return;
        }
        _phase = UploadPhase.SendingEntries;
        _ackDeadline = DateTime.UtcNow + BatchAckTimeout;
        byte[] batch = _batches[_nextBatch];
        _expectedEntries += CountEntriesIn(batch);
        _nextBatch++;
        _batchSends = 1;
        link.Write(batch);
    }

    // Re-send the current step's command (idempotent on the mod side).
    // Indefinite: the game may be busy for a long time; the pace relaxes
    // with backoff after a few resends. Logging is throttled.
    private void ResendCurrentBatch()
    {
        var link = _link;
        if (link is null)
            return;
        _batchSends++;
        bool backoff = _batchSends > ResendsBeforeBackoff;
        _ackDeadline = DateTime.UtcNow + (backoff ? BatchAckBackoff : BatchAckTimeout);
        // _nextBatch was already advanced past the pending entry; re-send
        // the one before it.
        byte[] batch = _batches[_nextBatch - 1];
        link.Write(batch);
        if (_batchSends == 2)
        {
            LogMessage($"entry {_nextBatch - 1} not confirmed; re-sending (game may be busy)");
        }
        else if (_batchSends % 20 == 0)
        {
            LogMessage($"entry {_nextBatch - 1} still unconfirmed after {_batchSends} sends - game busy?");
        }
    }

    private static int CountEntriesIn(byte[] batch)
    {
        // CONFIG_ENTRY commands are 8 + 3*S bytes; walk the batch.
        int count = 0;
        int i = 0;
        while (i < batch.Length)
        {
            if (batch[i] != UplinkProtocol.CmdConfigEntry)
                break; // should not happen; the packer only emits entries
            int s = (batch[i + 7] >> 2) & 7;
            i += 8 + 3 * s;
            count++;
        }
        return count;
    }

    /// <summary>
    /// COMMIT went unacknowledged. Do NOT restart (CONFIG_ABORT would
    /// discard a table that is very likely fully applied and already
    /// streaming — seen on device: cfg=14/14 with seq climbing while the
    /// reader kept restarting). COMMIT is re-sent indefinitely instead;
    /// the mod re-acks idempotently once its table is complete.
    /// </summary>
    private void FailUpload(string reason)
    {
        _phase = UploadPhase.Failed;
        _ackDeadline = DateTime.MaxValue;
        _configError = reason; // surfaced in the UI status line
        LogMessage($"config upload failed: {reason}");
        // Diagnostic: ask the mod for its counters; the reply (with
        // rx=<total host bytes seen> and cfg=<applied>/<declared>) lands
        // in the console via DeviceLine and shows whether the mod ever
        // received the entry bytes and how far its table got.
        SendPing();
    }

    private void CheckUploadTimeout()
    {
        if (_phase is UploadPhase.NotStarted or UploadPhase.Streaming or UploadPhase.Failed)
            return;

        // No-progress watchdog: no OK/ERR line at all for NoProgressTimeout
        // despite resends -> the attempt is going nowhere (lost BEGIN,
        // index gap, transport loss). One destructive restart (ABORT +
        // drain + BEGIN) recovers it; uncapped while connected.
        if (DateTime.UtcNow >= _noProgressDeadline)
        {
            LogMessage($"no progress for {NoProgressTimeout.TotalSeconds:0} s (phase {_phase}); restarting the attempt");
            BeginUploadAttempt();
            return;
        }

        if (DateTime.UtcNow <= _ackDeadline)
            return;

        switch (_phase)
        {
            case UploadPhase.AwaitingConnectPing:
                // The mod did not answer the connect PING (busy, or an old
                // mod build without cfg=). Fall through to a normal
                // upload — always safe.
                LogMessage("no answer to the connect PING; uploading the table");
                BeginUploadAttempt();
                break;

            case UploadPhase.AwaitingConfigAck:
                // Re-send BEGIN (idempotent: resets staging; the mod
                // re-replies OK CONFIG).
                _batchSends++;
                _ackDeadline = DateTime.UtcNow + ResendDeadline();
                _link?.Write(UplinkProtocol.ConfigBeginCommand(_config!.Count));
                if (_batchSends == 2)
                    LogMessage("CONFIG_BEGIN not acknowledged; re-sending (game may be busy)");
                else if (_batchSends % 20 == 0)
                    LogMessage($"CONFIG_BEGIN still unacknowledged after {_batchSends} sends - game busy?");
                break;

            case UploadPhase.SendingEntries:
                // The mod has not confirmed this entry: re-send it
                // (idempotent on the mod side).
                ResendCurrentBatch();
                break;

            case UploadPhase.AwaitingCommitAck:
                // The table is fully applied on the mod side; only the OK
                // STREAMING reply is missing (likely skipped because the
                // card lock was busy with frame writes). Re-send COMMIT —
                // the mod re-acks idempotently — instead of discarding
                // the table.
                _batchSends++;
                _ackDeadline = DateTime.UtcNow + ResendDeadline();
                _link?.Write(UplinkProtocol.ConfigCommitCommand);
                if (_batchSends == 2)
                    LogMessage("COMMIT not acknowledged; re-sending (game may be busy)");
                else if (_batchSends % 20 == 0)
                    LogMessage($"COMMIT still unacknowledged after {_batchSends} sends - game busy?");
                break;
        }
    }

    /// <summary>Deadline for the next resend of the current step (backoff after a few).</summary>
    private TimeSpan ResendDeadline() =>
        _batchSends > ResendsBeforeBackoff ? BatchAckBackoff : BatchAckTimeout;

    private void HandleLine(string line)
    {
        Interlocked.Increment(ref _deviceLines);
        DeviceLine?.Invoke(this, line);
        // Any device reply is progress: reset the no-progress watchdog.
        _noProgressDeadline = DateTime.UtcNow + NoProgressTimeout;

        if (line.StartsWith("OK CONFIG", StringComparison.Ordinal))
        {
            int max = ParseIntAfter(line, "max=");
            if (max > 0)
                _modMaxEntries = max;
            if (_config is not null && _config.Count > _modMaxEntries)
            {
                FailUpload($"config has {_config.Count} entries but the mod supports {_modMaxEntries} - trim addresses.json");
                return;
            }
            if (_phase == UploadPhase.AwaitingConfigAck)
                SendNextBatch();
            return;
        }

        if (line.StartsWith("OK ENTRIES", StringComparison.Ordinal))
        {
            int n = ParseIntAfter(line, "n=");
            if (_phase == UploadPhase.SendingEntries)
            {
                if (n >= _expectedEntries)
                {
                    _batchSends = 0; // entry confirmed
                    SendNextBatch();
                }
                else
                {
                    // Partial application (the batch was split across RX
                    // reads and the tail is still in flight): re-send; the
                    // mod applies entries idempotently.
                    ResendCurrentBatch();
                }
            }
            return;
        }

        if (line.StartsWith("OK STREAMING", StringComparison.Ordinal))
        {
            // The mod may process the last entry batch and the COMMIT in
            // the same RX read, skipping the final OK ENTRIES — accept the
            // commit ack from any waiting phase.
            if (_phase is UploadPhase.SendingEntries or UploadPhase.AwaitingCommitAck)
            {
                _phase = UploadPhase.Streaming;
                _ackDeadline = DateTime.MaxValue;
                _noProgressDeadline = DateTime.MaxValue;
                LogMessage("mod is streaming the uploaded table");
            }
            return;
        }

        if (line.StartsWith("OK ABORTED", StringComparison.Ordinal))
        {
            LogMessage("mod discarded the table (ABORT)");
            return;
        }

        if (line.StartsWith("ERR", StringComparison.Ordinal))
        {
            // Transient ERRs (e.g. an index gap after RX-ring overflow)
            // recover via an attempt restart; a permanent cause is
            // pre-validated before the upload, so several ERRs in a row
            // mean something is truly wrong.
            _consecutiveErrs++;
            if (_consecutiveErrs >= MaxConsecutiveErrs)
            {
                FailUpload($"mod rejected the config {_consecutiveErrs} times in a row: {line}");
                return;
            }
            LogMessage($"mod rejected a command ({_consecutiveErrs}/{MaxConsecutiveErrs}): {line}; restarting the attempt");
            BeginUploadAttempt();
            return;
        }

        if (line.StartsWith("UPLINK", StringComparison.Ordinal))
        {
            int applied = ParseIntAfter(line, "cfg=");
            int declared = ParseIntAfterSlash(line, "cfg=");

            if (_phase == UploadPhase.AwaitingConnectPing)
            {
                // Reconnect PING-skip: cfg=A/N matching the config means
                // the mod still has the table applied and is streaming —
                // skip the upload entirely.
                if (applied > 0 && applied == declared && _config is not null && applied == _config.Count)
                {
                    _phase = UploadPhase.Streaming;
                    _ackDeadline = DateTime.MaxValue;
                    _noProgressDeadline = DateTime.MaxValue;
                    LogMessage($"table already applied (cfg={applied}/{declared}); skipping the upload");
                }
                else
                {
                    LogMessage($"mod table not applied (cfg={applied}/{declared}); uploading");
                    BeginUploadAttempt();
                }
                return;
            }

            // Boot greeting (unsolicited UPLINK line, sent by the mod
            // right after its uplink came up — e.g. after a soft reset
            // while this reader stayed connected): the mod's table state
            // changed under us. If it no longer matches the config,
            // re-upload immediately instead of waiting out the silence
            // watchdog. While Streaming a MATCHING cfg is a no-op (the
            // greeting's counters are just diagnostics).
            if (_config is not null &&
                !(applied > 0 && applied == declared && applied == _config.Count))
            {
                LogMessage($"mod rebooted (greeting cfg={applied}/{declared}); re-uploading the table");
                BeginUploadAttempt();
            }
            return;
        }

        // Anything else: logged via DeviceLine only.
    }

    /// <summary>Integer after "cfg=A/" — the declared count.</summary>
    private static int ParseIntAfterSlash(string line, string marker)
    {
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return -1;
        int slash = line.IndexOf('/', idx);
        if (slash < 0)
            return -1;
        int end = slash + 1;
        while (end < line.Length && char.IsDigit(line[end]))
            end++;
        return int.TryParse(line[(slash + 1)..end], out int v) ? v : -1;
    }

    private static int ParseIntAfter(string line, string marker)
    {
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return -1;
        int start = idx + marker.Length;
        int end = start;
        while (end < line.Length && char.IsDigit(line[end]))
            end++;
        return int.TryParse(line[start..end], out int v) ? v : -1;
    }

    //------------------------------------------------------------------
    // Receive path
    //------------------------------------------------------------------

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
            _processor.Feed(readBuf.AsSpan(0, n), OnFrame, HandleLine);
            CheckUploadTimeout();
            LogStatsThrottled();
            return;
        }

        CheckUploadTimeout();

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

    private void OnFrame(UplinkSample sample)
    {
        // A valid frame with the expected sample count is itself proof
        // that the mod committed the table (the mod streams only with a
        // committed, non-empty table, and streams nothing while a config
        // is in progress) — accept it as the commit ACK even if the OK
        // STREAMING reply was lost.
        if (_phase == UploadPhase.AwaitingCommitAck &&
            _config is not null && sample.SampleCount == _config.Count)
        {
            _phase = UploadPhase.Streaming;
            _ackDeadline = DateTime.MaxValue;
            LogMessage("mod is streaming the uploaded table (confirmed by frames)");
        }
        PublishToMemoryMap(sample);
        _latest = sample;
        Interlocked.Increment(ref _frames);
        NoteSeq(sample.Seq);
        FrameReceived?.Invoke(this, sample);
    }

    /// <summary>
    /// Publishes the frame to the memory map (format v3, a name -> value
    /// table). Only frames from a committed table that matches the loaded
    /// address table are publishable: the mod streams nothing while a
    /// config upload is in progress, but between a connect and the first
    /// CONFIG_BEGIN it may still stream a table left over from a previous
    /// reader session, whose sample order need not match the current
    /// config — labeling those frames would mislabel values.
    /// </summary>
    private void PublishToMemoryMap(UplinkSample sample)
    {
        if (_phase != UploadPhase.Streaming)
            return;
        AddressConfig? config = _config;
        if (config is null || sample.SampleCount != config.Count)
            return;

        // (Re)publish the table structure when the mod re-commits its
        // config (epoch bump — e.g. the re-upload after a soft reset) or
        // the entry count changes; otherwise just refresh the values.
        if (sample.Epoch != _publishedEpoch || sample.SampleCount != _publishedCount)
        {
            UplinkMemoryMarker.PublishTable(
                config.Names, config.Addresses.Select(a => a.Width).ToArray(), sample.Epoch);
            _publishedEpoch = sample.Epoch;
            _publishedCount = sample.SampleCount;
            LogMessage($"memory map: published {sample.SampleCount} named entries (epoch {sample.Epoch})");
        }
        UplinkMemoryMarker.UpdateValues(sample.RawValues, sample.Seq, sample.GameFrame, sample.Epoch);
    }

    /// <summary>
    /// Diagnostics for frames the parser rejected (the mod streams and
    /// replies over one byte stream, so a reply can also be lost inside
    /// rejected frame ranges). Logged for the first few occurrences only.
    /// </summary>
    private void OnBadFrame(string message)
    {
        if (Interlocked.Increment(ref _badFrameLogs) <= 3)
        {
            LogMessage($"bad frame ({_badFrameLogs}/3 logged): {message}");
        }
    }

    private void NoteSeq(uint seq)
    {
        // Forward jumps are gaps; a reboot restarts seq at a low value,
        // which is not counted (same semantics as the v1 reader).
        if (_lastSeq is { } last && seq > last + 1)
        {
            Interlocked.Increment(ref _seqGaps);
            Interlocked.Add(ref _gapFrames, (long)(seq - last - 1));
        }
        _lastSeq = seq;
    }

    private void CloseLink(string? reason)
    {
        SerialLink? link;
        lock (_linkLock)
        {
            link = _link;
            _link = null;
        }
        if (link is null)
            return;
        _phase = UploadPhase.NotStarted;
        _ackDeadline = DateTime.MaxValue;
        _lastSeq = null;
        if (reason is not null)
            LogMessage($"disconnected ({reason}); reconnecting ...");
        try
        {
            link.Dispose();
        }
        catch
        {
            // the port may already be gone (device soft reset)
        }
    }

    private void SetState(UplinkState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void LogMessage(string message) => Log?.Invoke(this, message);

    private void LogThrottled(string message)
    {
        if ((DateTime.UtcNow - _lastScanLogUtc).TotalSeconds < 5)
            return;
        _lastScanLogUtc = DateTime.UtcNow;
        LogMessage(message);
    }

    /// <summary>
    /// Periodic receive-path stats (every 5 s): frame/checksum/resync
    /// counters in the console make a silent frame-decode failure visible
    /// without the UI.
    /// </summary>
    private void LogStatsThrottled()
    {
        if ((DateTime.UtcNow - _lastStatsLogUtc).TotalSeconds < 5)
            return;
        _lastStatsLogUtc = DateTime.UtcNow;
        LogMessage($"stats: frames={_frames} checksum-errors={_processor.BadChecksums} " +
                   $"resyncs={_processor.Resyncs} bytes={_bytes} phase={_phase}");
    }
}

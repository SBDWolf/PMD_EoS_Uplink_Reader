using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UplinkReader;
using UplinkReader.Config;
using UplinkReader.ConsoleMode;
using UplinkReader.Link;
using UplinkReader.Protocol;

namespace UplinkReader.Ui;

/// <summary>
/// Main window: DSPico detection, port selection (auto-refreshed every
/// 3 s), auto-reconnect checkbox (not persisted), connect/disconnect, and
/// connection status. Sample values are deliberately not displayed
/// here — Debug builds log every frame to the console (ConsoleLog),
/// and the UplinkClient.FrameReceived event is available for other use.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly UplinkClient _client;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _portTimer;
    private readonly Stopwatch _session = Stopwatch.StartNew();
    private bool _suppressPortEvents;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        _client = new UplinkClient();
        if (!string.IsNullOrEmpty(_config.LastPort))
            _client.PreferredPort = _config.LastPort;

        _client.StateChanged += OnStateChanged;
        _client.Start();

        // Console log to standard I/O (Debug builds only; no-op in
        // Release — see ConsoleLog).
        ConsoleLog.Banner();
        _client.FrameReceived += ConsoleLog.Frame;
        _client.Log += ConsoleLog.Log;
        _client.DeviceLine += ConsoleLog.DeviceLine;

        // Refresh the counters a few times per second (values are not
        // shown in the GUI).
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statsTimer.Tick += (_, _) => UpdateStatsText();
        _statsTimer.Start();

        // Auto-refresh the port list every few seconds (new ports,
        // VID/PID changes, unplug/replug).
        _portTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _portTimer.Tick += (_, _) => RefreshPorts();
        _portTimer.Start();

        RefreshPorts();
        Closing += OnClosing;
    }

    private sealed class PortItem
    {
        public PortItem(PortDiscovery.PortInfo info) => Info = info;
        public PortDiscovery.PortInfo Info { get; }
        public override string ToString() =>
            Info.IsUplink ? $"{Info.Display}   <== uplink" : Info.Display;
    }

    private void RefreshPorts()
    {
        var items = PortDiscovery.ListPorts().Select(p => new PortItem(p)).ToList();

        _suppressPortEvents = true;
        PortCombo.ItemsSource = items;
        string? preferred = _client.PreferredPort ?? _config.LastPort;
        PortCombo.SelectedItem =
            items.FirstOrDefault(i => i.Info.Name == preferred)
            ?? items.FirstOrDefault(i => i.Info.IsUplink);
        _suppressPortEvents = false;

        if (!PortDiscovery.VidPidDetectionSupported)
        {
            DetectionText.Text =
                "DSPico detected: unknown (VID/PID detection not supported here - pick the port above)";
        }
        else
        {
            var uplink = items.FirstOrDefault(i => i.Info.IsUplink);
            DetectionText.Text = uplink is null
                ? "DSPico detected: no (no port with VID 2020 / PID D801)"
                : $"DSPico detected: yes ({uplink.Info.Name})";
        }
        UpdateStatsText();
    }

    private void OnPortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPortEvents || PortCombo.SelectedItem is not PortItem item)
            return;
        _client.PreferredPort = item.Info.Name;
        _config.LastPort = item.Info.Name;
    }

    private void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is PortItem item)
        {
            _client.PreferredPort = item.Info.Name;
            _config.LastPort = item.Info.Name;
        }
        _client.WantsConnection = true;
        AutoReconnectCheck.IsChecked = true;
        SaveConfig();
    }

    private void OnDisconnect(object? sender, RoutedEventArgs e)
    {
        _client.WantsConnection = false;
        AutoReconnectCheck.IsChecked = false;
        SaveConfig();
    }

    private void OnAutoReconnectToggled(object? sender, RoutedEventArgs e) =>
        _client.WantsConnection = AutoReconnectCheck.IsChecked == true;

    private void OnStateChanged(object? sender, UplinkState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = state switch
            {
                UplinkState.Connected => $"Connected - {_client.ActivePort}",
                UplinkState.Connecting => $"Connecting to {_client.PreferredPort ?? "..."} ...",
                UplinkState.Scanning => "Scanning for DSPico ...",
                _ => "Disconnected",
            };
            ConnectButton.IsEnabled = state == UplinkState.Disconnected;
            DisconnectButton.IsEnabled = state != UplinkState.Disconnected;
            UpdateStatsText();
        });
    }

    private void UpdateStatsText()
    {
        StatsText.Text =
            $"frames: {_client.FramesReceived}    seq gaps: {_client.SeqGaps} ({_client.GapFramesLost} lost)" +
            $"    checksum errors: {_client.ChecksumErrors}    reconnects: {_client.Reconnects}" +
            $"    bytes: {_client.BytesReceived}";
    }

    private void SaveConfig()
    {
        if (PortCombo.SelectedItem is PortItem item)
            _config.LastPort = item.Info.Name;
        _config.Save();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveConfig();
        _statsTimer.Stop();
        _portTimer.Stop();
        ConsoleLog.Summary(_client, _session.Elapsed.TotalSeconds);
        _client.Dispose();
    }
}

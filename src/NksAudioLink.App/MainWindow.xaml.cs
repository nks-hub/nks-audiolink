using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Microsoft.Win32;
using NksAudioLink.Client;
using NksAudioLink.Core;
using Forms = System.Windows.Forms;

namespace NksAudioLink.App;

public partial class MainWindow : Window
{
    private sealed record Settings
    {
        public string Server { get; init; } = "127.0.0.1";
        public int Port { get; init; } = 7355;
        public int HttpPort { get; init; } = 7356;
        public string? DeviceId { get; init; }
        public bool Loopback { get; init; } = true;
        public bool VirtualCableMode { get; init; }
        public int GainPercent { get; init; } = 100;
        public int TargetLatencyMs { get; init; } = 30;
    }

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NksAudioLink", "settings.json");
    private const string StartupName = "NKS AudioLink";
    private CancellationTokenSource? _streamStop;
    private Task? _streamTask;
    private AudioStreamClient? _activeClient;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayConnect;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private readonly Queue<string> _activity = new();
    private TaskCompletionSource? _streamFinished;
    private bool _quitInProgress;
    private bool _closing;
    private bool _initializing = true;
    private string? _preferredDeviceId;
    private DateTime _lastStatsUtc;
    private DateTime _connectionStartedUtc;
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _meterTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };

    public MainWindow()
    {
        InitializeComponent();
        try { VirtualCableRouting.RestorePending(); }
        catch (Exception ex) { DetailText.Text = "Could not restore the previous sound device. Check the Windows default output. " + ex.Message; }
        LoadSettings();
        RefreshDevices();
        InitTray();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            StartWithWindowsCheck.IsChecked = key?.GetValue(StartupName) is not null;
        }
        catch (Exception ex) { DetailText.Text = "Could not read the Windows startup setting. " + ex.Message; }
        _initializing = false;
        GainSlider.ToolTip = "100% keeps the original level. Higher gain may clip.";
        _meterTimer.Tick += (_, _) =>
        {
            if (IsVisible && WindowState != WindowState.Minimized && _activeClient is { } client)
                ShowSentLevels(client.Levels);
        };
        IsVisibleChanged += (_, _) => UpdateMeterTimer();
        AddActivity("AudioLink started. Ready to connect.");
        _healthTimer.Tick += (_, _) =>
        {
            if (_streamStop is not null && !_streamStop.IsCancellationRequested &&
                DateTime.UtcNow - (_lastStatsUtc == DateTime.MinValue ? _connectionStartedUtc : _lastStatsUtc) > TimeSpan.FromSeconds(4) &&
                (StatusText.Text == "Connected" || StatusText.Text == "Connecting"))
                SetStatus("Waiting for server", "The server is not responding. The app is trying to reconnect.");
        };
        _healthTimer.Start();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (saved is null) return;
            ServerBox.Text = saved.Server;
            PortBox.Text = saved.Port.ToString();
            HttpPortBox.Text = (saved.HttpPort is >= 1 and <= 65535 ? saved.HttpPort : 7356).ToString();
            GainSlider.Value = Math.Clamp(saved.GainPercent, 0, 200);
            LatencySlider.Value = Math.Clamp(saved.TargetLatencyMs, 10, 200);
            VirtualRadio.IsChecked = saved.VirtualCableMode;
            CaptureRadio.IsChecked = !saved.Loopback && !saved.VirtualCableMode;
            LoopbackRadio.IsChecked = saved.Loopback && !saved.VirtualCableMode;
            _preferredDeviceId = saved.DeviceId;
        }
        catch (Exception ex) { DetailText.Text = "Could not load saved settings. Check settings.json. " + ex.Message; }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new Settings
            {
                Server = ServerBox.Text.Trim(),
                Port = int.TryParse(PortBox.Text, out int port) ? port : 7355,
                HttpPort = int.TryParse(HttpPortBox.Text, out int httpPort) && httpPort is >= 1 and <= 65535 ? httpPort : 7356,
                DeviceId = DeviceCombo.SelectedValue as string,
                Loopback = LoopbackRadio.IsChecked == true,
                VirtualCableMode = VirtualRadio.IsChecked == true,
                GainPercent = (int)GainSlider.Value,
                TargetLatencyMs = (int)LatencySlider.Value
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { DetailText.Text = "Could not save settings. Check access to your user folder. " + ex.Message; }
    }

    private void RefreshDevices()
    {
        try
        {
            string? selected = _preferredDeviceId ?? DeviceCombo.SelectedValue as string;
            bool loopback = LoopbackRadio.IsChecked == true;
            bool virtualMode = VirtualRadio.IsChecked == true;
            var devices = WasapiSource.ListDevices().Where(d => d.IsRender == loopback).ToArray();
            DeviceCombo.ItemsSource = devices;
            var cable = virtualMode ? VirtualCableRouting.FindInstalledPair() : null;
            DeviceCombo.SelectedItem = virtualMode ? devices.FirstOrDefault(d => d.Id == cable?.Capture.Id)
                : devices.FirstOrDefault(d => d.Id == selected);
            if (!virtualMode && DeviceCombo.SelectedIndex < 0 && devices.Length > 0) DeviceCombo.SelectedIndex = 0;
            DeviceCombo.IsEnabled = !virtualMode;
            CableInfoButton.Visibility = virtualMode && cable is null ? Visibility.Visible : Visibility.Collapsed;
            _preferredDeviceId = null;
            DeviceHint.Text = virtualMode && cable is null
                ? "VB-CABLE is not ready. Enable CABLE Input and CABLE Output in Windows."
                : virtualMode ? "Connecting sets CABLE Input as the Windows default output. Disconnecting restores the previous device."
                : devices.Length == 0 ? "No audio device is available. Check Windows sound settings." : "";
        }
        catch (Exception ex)
        {
            DeviceHint.Text = "Could not list audio devices. Try Refresh. " + ex.Message;
            DeviceCombo.ItemsSource = null;
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo is null || ModeDescription is null) return;
        ModeDescription.Text = LoopbackRadio.IsChecked == true
            ? "Sends audio from the selected output. It still plays on this PC."
            : VirtualRadio.IsChecked == true
                ? "Routes Windows audio to CABLE Input and captures it from CABLE Output."
                : "Sends audio from the selected input. The Windows default output stays the same.";
        RefreshDevices();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void CableInfo_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Could not open website", "Open the vendor website in a browser. " + ex.Message); }
    }
    private void GainSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainValue is not null) GainValue.Text = $"{(int)GainSlider.Value}%";
        if (_activeClient is not null) _activeClient.Gain = GainSlider.Value / 100;
    }
    private void LatencySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LatencyValue is not null) LatencyValue.Text = $"{(int)LatencySlider.Value} ms";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_quitInProgress) return;
        if (_streamStop is not null)
        {
            _streamStop.Cancel();
            ConnectButton.IsEnabled = false;
            SetStatus("Disconnecting", "Stopping capture and restoring the previous sound device.");
            return;
        }
        bool virtualMode = VirtualRadio.IsChecked == true;
        (AudioDevice Playback, AudioDevice Capture)? cablePair;
        try { cablePair = virtualMode ? VirtualCableRouting.FindInstalledPair() : null; }
        catch (Exception ex) { SetStatus("Could not list devices", ex.Message); return; }
        if (virtualMode && cablePair is null)
        {
            SetStatus("Virtual mode unavailable", "Enable CABLE Input and CABLE Output in Windows sound settings.");
            return;
        }
        if (DeviceCombo.SelectedItem is not AudioDevice device)
        {
            SetStatus("No audio source", "Select an available audio device.");
            return;
        }
        if (!int.TryParse(PortBox.Text, out int port))
        {
            SetStatus("Invalid settings", "Enter a UDP port from 1 to 65535.");
            return;
        }
        var config = new ClientConfig
        {
            Server = ServerBox.Text.Trim(), Port = port, Gain = GainSlider.Value / 100,
            TargetLatencyMs = (int)LatencySlider.Value,
            PskBase64 = string.IsNullOrWhiteSpace(PskBox.Password) ? null : PskBox.Password.Trim()
        };
        try { config.Validate(); }
        catch (Exception ex) { SetStatus("Invalid settings", ex.Message); return; }
        if (virtualMode)
        {
            try
            {
                var cable = cablePair!.Value;
                if (device.Id != cable.Capture.Id)
                    throw new InvalidOperationException("The selected input is not CABLE Output. Refresh the device list.");
                VirtualCableRouting.Activate(cable.Playback.Id);
            }
            catch (Exception ex) { SetStatus("Virtual mode unavailable", ex.Message); return; }
        }
        SaveSettings();
        _streamStop = new CancellationTokenSource();
        _streamFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastStatsUtc = DateTime.MinValue;
        _connectionStartedUtc = DateTime.UtcNow;
        ConnectButton.Content = "Disconnect";
        ConnectButton.Background = new SolidColorBrush(Color.FromRgb(45, 62, 79));
        if (_trayConnect is not null) _trayConnect.Text = "Disconnect";
        SetStatus("Connecting", $"Server {config.Server}:{config.Port} · {device.Name}");
        var client = new AudioStreamClient(config);
        _activeClient = client;
        ServerCard.IsEnabled = SourceCard.IsEnabled = LatencySlider.IsEnabled = false;
        client.StatsReceived += (stats, rtt) => Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_activeClient, client) && _streamStop is { IsCancellationRequested: false })
                ShowStats(stats, rtt, client.CaptureLatencyMs);
        });
        client.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_activeClient, client) || _streamStop is null || _streamStop.IsCancellationRequested) return;
            if (state == "Disconnected") return;
            if (state == "Rejected")
                SetStatus("Rejected", state);
            else if (state == "Recovering audio device")
                SetStatus(state, "The selected device is unavailable. Check its connection.");
            else if (state == "Waiting for server")
                SetStatus(state, "The server is not responding. The app is trying to reconnect.");
        });
        _streamTask = client.RunAsync(device.Id, LoopbackRadio.IsChecked == true, _streamStop.Token);
        bool serverRejected = false;
        try { await _streamTask; }
        catch (ServerRejectedException ex)
        {
            serverRejected = true;
            SetStatus("Rejected", ex.Message);
        }
        catch (OperationCanceledException) when (_streamStop.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closing) SetStatus("Stream error", ex.Message); }
        finally
        {
            if (virtualMode)
            {
                try { VirtualCableRouting.RestorePending(); }
                catch (Exception ex) { SetStatus("Device restore failed", ex.Message); }
            }
            _streamStop.Dispose();
            _streamStop = null;
            _streamTask = null;
            _activeClient = null;
            ResetStats();
            ServerCard.IsEnabled = SourceCard.IsEnabled = LatencySlider.IsEnabled = true;
            ConnectButton.IsEnabled = !_quitInProgress;
            ConnectButton.Content = "Connect";
            ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 127, 117));
            if (!serverRejected && StatusText.Text is not ("Stream error" or "Device restore failed"))
                SetStatus("Disconnected", "Streaming has stopped.");
            if (_trayConnect is not null) _trayConnect.Text = "Connect";
            _streamFinished.TrySetResult();
        }
    }

    private void ShowStats(StatsMessage stats, double rtt, double captureLatencyMs)
    {
        _lastStatsUtc = DateTime.UtcNow;
        SetStatus(stats.Accepted ? "Connected" : "Rejected",
            stats.Accepted ? "Sending audio: 48 kHz, 16-bit stereo." : "The server rejected this connection. Check its address, shared key, and whether another client is connected.");
        if (!stats.Accepted) return;
        BufferText.Text = $"{stats.BufferMs} ms";
        SinkDelayText.Text = $"{stats.SinkDelayMs} ms";
        RttText.Text = $"{rtt:0.0} ms";
        // Capture period is an estimate; WASAPI does not report actual capture-to-wire delay here.
        EndToEndText.Text = $"≈ {captureLatencyMs + AudioFrameQueue.TargetBufferMs + Protocol.FrameMs + rtt / 2 + stats.BufferMs + stats.SinkDelayMs:0} ms";
        LostText.Text = stats.Lost.ToString("N0");
        LateText.Text = stats.Late.ToString("N0");
        UnderrunText.Text = stats.Underruns.ToString("N0");
        OverrunText.Text = stats.Overruns.ToString("N0");
        if (_trayConnect is not null) _trayConnect.Text = "Disconnect";
    }

    private void SetStatus(string status, string detail)
    {
        bool changed = StatusText.Text != status || DetailText.Text != detail;
        StatusText.Text = status;
        DetailText.Text = detail;
        var colors = status switch
        {
            "Connected" => ("#193E36", "#81E5BE"),
            "Disconnected" => ("#263749", "#B6C6D7"),
            "Connecting" or "Disconnecting" or "Waiting for server" or "Recovering audio device" => ("#453921", "#F6CE87"),
            _ => ("#492B37", "#FFA7B8")
        };
        StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Item1));
        StatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors.Item2));
        if (status == "Connected") UpdateMeterTimer();
        else
        {
            _meterTimer.Stop();
            ResetStats();
            ShowSentLevels(new AudioLevelSnapshot(-90, -90, -90, -90, false));
        }
        if (changed) AddActivity($"{status}: {detail}");
        if (_tray is not null) _tray.Text = ("NKS AudioLink: " + status)[..Math.Min(63, ("NKS AudioLink: " + status).Length)];
    }

    private void ResetStats()
    {
        foreach (var field in new[] { BufferText, SinkDelayText, RttText, EndToEndText, LostText, LateText, UnderrunText, OverrunText })
            field.Text = "\u2014";
    }

    private void ShowSentLevels(AudioLevelSnapshot level)
    {
        SetChannelLevel(LeftMeterTrack, LeftRmsBar, LeftPeakMarker, LeftLevelText, level.LeftRmsDbfs, level.LeftPeakDbfs);
        SetChannelLevel(RightMeterTrack, RightRmsBar, RightPeakMarker, RightLevelText, level.RightRmsDbfs, level.RightPeakDbfs);
        SentClipText.Visibility = level.Clipping ? Visibility.Visible : Visibility.Hidden;
    }

    private void UpdateMeterTimer()
    {
        if (StatusText?.Text == "Connected" && IsVisible && WindowState != WindowState.Minimized)
            _meterTimer.Start();
        else _meterTimer.Stop();
    }

    private static void SetChannelLevel(Grid track, Border rmsBar, Border peakMarker, TextBlock value, double rmsDbfs, double peakDbfs)
    {
        // dB scale keeps low levels readable; the numeric readout covers the full -90 dBFS floor.
        double width = track.ActualWidth;
        rmsBar.Width = Math.Clamp((rmsDbfs + 60) / 60, 0, 1) * width;
        peakMarker.Margin = new Thickness(Math.Clamp((peakDbfs + 60) / 60, 0, 1) * Math.Max(0, width - 2), 0, 0, 0);
        peakMarker.Visibility = peakDbfs > -60 ? Visibility.Visible : Visibility.Hidden;
        string text = rmsDbfs <= -90 ? "−∞" : rmsDbfs.ToString("0.0");
        if (value.Text != text) value.Text = text;
    }

    private void AddActivity(string message)
    {
        // Avoid storing credentials even when an exception includes input text.
        string secret = PskBox.Password.Trim();
        if (secret.Length > 0) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        if (message.Length > 1000) message = message[..1000] + "…";
        _activity.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_activity.Count > 200) _activity.Dequeue();
        ActivityLog.Text = string.Join(Environment.NewLine, _activity);
        ActivityLog.ScrollToEnd();
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(ActivityLog.Text); }
        catch (System.Runtime.InteropServices.COMException) { AddActivity("Could not copy: the clipboard is busy. Try again."); }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) { _activity.Clear(); ActivityLog.Clear(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Hide_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        WindowFrame.Margin = maximized ? new Thickness(8) : new Thickness(0);
        UpdateMeterTimer();
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        string host = ServerBox.Text.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown ||
            !int.TryParse(HttpPortBox.Text, out int port) || port is < 1 or > 65535)
        {
            AddActivity("Dashboard: enter a valid server host name and HTTP port (1–65535).");
            System.Windows.MessageBox.Show(this, "Enter a valid server host name and HTTP port from 1 to 65535.", "Server dashboard", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            string url = new UriBuilder(Uri.UriSchemeHttp, host, port, "/").Uri.AbsoluteUri;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            SaveSettings();
            AddActivity($"Opened server dashboard at {url}");
        }
        catch (Exception ex) { AddActivity($"Could not open the server dashboard: {ex.Message}"); }
    }
    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();

    private async Task QuitAsync()
    {
        if (_quitInProgress) return;
        _quitInProgress = true;
        ConnectButton.IsEnabled = false;
        if (_trayConnect is not null) _trayConnect.Enabled = false;
        SaveSettings();
        _streamStop?.Cancel();
        if (_streamFinished is { Task.IsCompleted: false })
        {
            SetStatus("Disconnecting", "Finishing audio cleanup before quitting…");
            await _streamFinished.Task;
        }
        try { VirtualCableRouting.RestorePending(); }
        catch (Exception ex)
        {
            _quitInProgress = false;
            ConnectButton.IsEnabled = true;
            if (_trayConnect is not null) _trayConnect.Enabled = true;
            Show();
            Activate();
            SetStatus("Device restore failed", ex.Message);
            return;
        }
        _closing = true;
        Close();
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
        {
            DiscoveryText.Text = "Enter a valid UDP port.";
            return;
        }
        byte[] key;
        try { key = string.IsNullOrWhiteSpace(PskBox.Password) ? [] : Convert.FromBase64String(PskBox.Password.Trim()); }
        catch (FormatException) { DiscoveryText.Text = "The shared key must be Base64. Check the entered value."; return; }
        DiscoverButton.IsEnabled = false;
        DiscoveryText.Text = "Searching the local network…";
        try
        {
            byte[] request = new byte[Protocol.MaxDatagramSize];
            int length = Protocol.Write(request, new PacketHeader(PacketType.Discover, PacketFlags.None, 0, 0), [], key);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var addresses = GetDiscoveryAddresses().ToArray();
            if (addresses.Length == 0)
                addresses = [(IPAddress.Any, IPAddress.Broadcast)];
            var probes = addresses.Select(address =>
                ProbeServerAsync(address.Local, address.Broadcast, port, request.AsMemory(0, length), key, timeout.Token)).ToList();
            DiscoveryResult? found = null;
            while (probes.Count > 0)
            {
                Task<DiscoveryResult?> finished = await Task.WhenAny(probes);
                probes.Remove(finished);
                found = await finished;
                if (found is not null) break;
            }
            if (found is not null)
            {
                timeout.Cancel();
                ServerBox.Text = found.Address.ToString();
                DiscoveryText.Text = $"Found: {found.Message.Name} ({ServerBox.Text}, output {found.Message.Sink})";
                SaveSettings();
            }
            else DiscoveryText.Text = "No server replied. Enter its address or check the network and shared key.";
        }
        catch (OperationCanceledException) { DiscoveryText.Text = "No server replied. Enter its address or check the network and shared key."; }
        catch (Exception ex) { DiscoveryText.Text = "Could not search for a server. Check the network connection. " + ex.Message; }
        finally { DiscoverButton.IsEnabled = true; }
    }

    private sealed record DiscoveryResult(IPAddress Address, DiscoverReplyMessage Message);

    private static IEnumerable<(IPAddress Local, IPAddress Broadcast)> GetDiscoveryAddresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;
                byte[] address = unicast.Address.GetAddressBytes();
                byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                byte[] broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(address[i] | ~mask[i]);
                yield return (unicast.Address, new IPAddress(broadcast));
            }
        }
    }

    private static async Task<DiscoveryResult?> ProbeServerAsync(
        IPAddress local, IPAddress directedBroadcast, int port, ReadOnlyMemory<byte> request,
        byte[] key, CancellationToken cancellationToken)
    {
        try
        {
            // Bind before sending: a machine with multiple adapters must use the matching source address.
            using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
            await udp.SendAsync(request, new IPEndPoint(directedBroadcast, port));
            if (!directedBroadcast.Equals(IPAddress.Broadcast))
            {
                try { await udp.SendAsync(request, new IPEndPoint(IPAddress.Broadcast, port)); }
                catch (SocketException) { /* Directed broadcast may still reach the server. */ }
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                var reply = await udp.ReceiveAsync(cancellationToken);
                if (TryReadDiscoveryReply(reply.Buffer, key, out var message))
                    return new DiscoveryResult(reply.RemoteEndPoint.Address, message);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
        return null;
    }

    private static bool TryReadDiscoveryReply(byte[] packet, byte[] key, out DiscoverReplyMessage message)
    {
        message = default;
        return Protocol.TryRead(packet, key, out var header, out var body) &&
               header.Type == PacketType.DiscoverReply && DiscoverReplyMessage.TryRead(body, out message);
    }

    private void InitTray()
    {
        var menu = _trayMenu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Open");
        open.Click += (_, _) => Dispatcher.BeginInvoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
        _trayConnect = new Forms.ToolStripMenuItem("Connect");
        _trayConnect.Click += (_, _) => Dispatcher.BeginInvoke(() => Connect_Click(this, new RoutedEventArgs()));
        var quit = new Forms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Dispatcher.BeginInvoke(() => Quit_Click(this, new RoutedEventArgs()));
        menu.Items.AddRange([open, _trayConnect, new Forms.ToolStripSeparator(), quit]);
        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AudioLink.ico")).Stream;
        _trayIcon = new System.Drawing.Icon(iconStream, 32, 32);
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "NKS AudioLink: Disconnected", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (StartWithWindowsCheck.IsChecked == true)
                key.SetValue(StartupName, "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue(StartupName, false);
        }
        catch (Exception ex) { DetailText.Text = "Could not change the Windows startup setting. Check your account permissions. " + ex.Message; }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
            SaveSettings();
            return;
        }
        SaveSettings();
        _healthTimer.Stop();
        _meterTimer.Stop();
        _tray?.Dispose();
        _trayMenu?.Dispose();
        _trayIcon?.Dispose();
    }
}

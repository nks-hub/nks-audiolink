using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
    private bool _closing;
    private bool _initializing = true;
    private string? _preferredDeviceId;
    private DateTime _lastStatsUtc;
    private DateTime _connectionStartedUtc;
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        try { VirtualCableRouting.RestorePending(); }
        catch (Exception ex) { DetailText.Text = "Původní zvukovku se nepodařilo obnovit: " + ex.Message; }
        LoadSettings();
        RefreshDevices();
        InitTray();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            StartWithWindowsCheck.IsChecked = key?.GetValue(StartupName) is not null;
        }
        catch (Exception ex) { DetailText.Text = "Automatické spuštění nelze přečíst: " + ex.Message; }
        _initializing = false;
        _healthTimer.Tick += (_, _) =>
        {
            if (_streamStop is not null && !_streamStop.IsCancellationRequested &&
                DateTime.UtcNow - (_lastStatsUtc == DateTime.MinValue ? _connectionStartedUtc : _lastStatsUtc) > TimeSpan.FromSeconds(4) &&
                (StatusText.Text == "Připojeno" || StatusText.Text == "Připojování"))
                SetStatus("Čeká na server", "Statistiky se přestaly aktualizovat. Klient se pokouší obnovit spojení.");
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
            GainSlider.Value = Math.Clamp(saved.GainPercent, 0, 200);
            LatencySlider.Value = Math.Clamp(saved.TargetLatencyMs, 10, 200);
            VirtualRadio.IsChecked = saved.VirtualCableMode;
            CaptureRadio.IsChecked = !saved.Loopback && !saved.VirtualCableMode;
            LoopbackRadio.IsChecked = saved.Loopback && !saved.VirtualCableMode;
            _preferredDeviceId = saved.DeviceId;
        }
        catch (Exception ex) { DetailText.Text = "Nastavení se nepodařilo načíst: " + ex.Message; }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new Settings
            {
                Server = ServerBox.Text.Trim(),
                Port = int.TryParse(PortBox.Text, out int port) ? port : 7355,
                DeviceId = DeviceCombo.SelectedValue as string,
                Loopback = LoopbackRadio.IsChecked == true,
                VirtualCableMode = VirtualRadio.IsChecked == true,
                GainPercent = (int)GainSlider.Value,
                TargetLatencyMs = (int)LatencySlider.Value
            };
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { DetailText.Text = "Nastavení se nepodařilo uložit: " + ex.Message; }
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
                ? "VB-CABLE není připraven: potřebujete aktivní CABLE Input (výstup) i CABLE Output (záznam). Ovladač aplikace neinstaluje."
                : virtualMode ? "Po připojení se výchozí výstup Windows přepne na CABLE Input. Po odpojení se obnoví."
                : devices.Length == 0 ? "V tomto režimu není dostupné žádné aktivní zvukové zařízení." : "";
        }
        catch (Exception ex)
        {
            DeviceHint.Text = "Zařízení nelze načíst: " + ex.Message;
            DeviceCombo.ItemsSource = null;
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo is null || ModeDescription is null) return;
        ModeDescription.Text = LoopbackRadio.IsChecked == true
            ? "Odesílá zvuk vybraného výstupu. Současně hraje i na PC."
            : VirtualRadio.IsChecked == true
                ? "Směruje zvuk Windows do CABLE Input a snímá jej z CABLE Output."
                : "Odesílá vybraný záznamový vstup bez změny výchozího výstupu.";
        RefreshDevices();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void CableInfo_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Web nelze otevřít", ex.Message); }
    }
    private void GainSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainValue is not null) GainValue.Text = $"{(int)GainSlider.Value} %";
        if (_activeClient is not null) _activeClient.Gain = GainSlider.Value / 100;
    }
    private void LatencySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LatencyValue is not null) LatencyValue.Text = $"{(int)LatencySlider.Value} ms";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_streamStop is not null) { _streamStop.Cancel(); return; }
        bool virtualMode = VirtualRadio.IsChecked == true;
        (AudioDevice Playback, AudioDevice Capture)? cablePair;
        try { cablePair = virtualMode ? VirtualCableRouting.FindInstalledPair() : null; }
        catch (Exception ex) { SetStatus("Zvukové zařízení nelze načíst", ex.Message); return; }
        if (virtualMode && cablePair is null)
        {
            SetStatus("Virtuální režim není připraven", "Aktivní CABLE Input a CABLE Output nebyly nalezeny. Ovladač aplikace neinstaluje.");
            return;
        }
        if (DeviceCombo.SelectedItem is not AudioDevice device)
        {
            SetStatus("Chybí zdroj", "Vyberte dostupné zvukové zařízení.");
            return;
        }
        if (!int.TryParse(PortBox.Text, out int port))
        {
            SetStatus("Chyba nastavení", "UDP port musí být číslo od 1 do 65535.");
            return;
        }
        var config = new ClientConfig
        {
            Server = ServerBox.Text.Trim(), Port = port, Gain = GainSlider.Value / 100,
            TargetLatencyMs = (int)LatencySlider.Value,
            PskBase64 = string.IsNullOrWhiteSpace(PskBox.Password) ? null : PskBox.Password.Trim()
        };
        try { config.Validate(); }
        catch (Exception ex) { SetStatus("Chyba nastavení", ex.Message); return; }
        if (virtualMode)
        {
            try
            {
                var cable = cablePair!.Value;
                if (device.Id != cable.Capture.Id)
                    throw new InvalidOperationException("Vybraný vstup není CABLE Output.");
                VirtualCableRouting.Activate(cable.Playback.Id);
            }
            catch (Exception ex) { SetStatus("Virtuální režim není připraven", ex.Message); return; }
        }
        SaveSettings();
        _streamStop = new CancellationTokenSource();
        _lastStatsUtc = DateTime.MinValue;
        _connectionStartedUtc = DateTime.UtcNow;
        ConnectButton.Content = "Odpojit";
        ConnectButton.Background = System.Windows.Media.Brushes.IndianRed;
        if (_trayConnect is not null) _trayConnect.Text = "Odpojit";
        SetStatus("Připojování", $"Server {config.Server}:{config.Port} · {device.Name}");
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
            if (state == "Odpojeno") return;
            if (state.StartsWith("Odm", StringComparison.OrdinalIgnoreCase))
                SetStatus("Odmítnuto", state);
            else if (state == "Obnovuje zvukové zařízení")
                SetStatus(state, "Čeká na dostupnost vybraného zdroje a znovu naváže přenos.");
            else if (state == "Čeká na server")
                SetStatus(state, "Pokouší se znovu navázat spojení. Přenos lze odpojit.");
        });
        _streamTask = client.RunAsync(device.Id, LoopbackRadio.IsChecked == true, _streamStop.Token);
        try { await _streamTask; }
        catch (Exception ex) { if (!_closing) SetStatus("Chyba přenosu", ex.Message); }
        finally
        {
            if (virtualMode)
            {
                try { VirtualCableRouting.RestorePending(); }
                catch (Exception ex) { SetStatus("Obnova zvukovky selhala", ex.Message); }
            }
            _streamStop.Dispose();
            _streamStop = null;
            _streamTask = null;
            _activeClient = null;
            ServerCard.IsEnabled = SourceCard.IsEnabled = LatencySlider.IsEnabled = true;
            ConnectButton.Content = "Připojit";
            ConnectButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 127, 117));
            if (StatusText.Text is not ("Chyba přenosu" or "Obnova zvukovky selhala"))
                SetStatus("Odpojeno", "Přenos je zastaven.");
            if (_trayConnect is not null) _trayConnect.Text = "Připojit";
        }
    }

    private void ShowStats(StatsMessage stats, double rtt, double captureLatencyMs)
    {
        _lastStatsUtc = DateTime.UtcNow;
        SetStatus(stats.Accepted ? "Připojeno" : "Odmítnuto",
            stats.Accepted ? "Zvuk se odesílá · 48 kHz / 16 bit / stereo" : "Server relaci odmítl. Zkontrolujte adresu, klíč a obsazenost serveru.");
        BufferText.Text = $"{stats.BufferMs} ms";
        SinkDelayText.Text = $"{stats.SinkDelayMs} ms";
        RttText.Text = $"{rtt:0.0} ms";
        // Capture period is an estimate; WASAPI does not report actual capture-to-wire delay here.
        EndToEndText.Text = $"≈ {captureLatencyMs + AudioFrameQueue.TargetBufferMs + Protocol.FrameMs + rtt / 2 + stats.BufferMs + stats.SinkDelayMs:0} ms";
        LostText.Text = stats.Lost.ToString("N0");
        LateText.Text = stats.Late.ToString("N0");
        UnderrunText.Text = stats.Underruns.ToString("N0");
        OverrunText.Text = stats.Overruns.ToString("N0");
        if (_trayConnect is not null) _trayConnect.Text = "Odpojit";
    }

    private void SetStatus(string status, string detail)
    {
        StatusText.Text = status;
        DetailText.Text = detail;
        if (_tray is not null) _tray.Text = ("NKS AudioLink — " + status)[..Math.Min(63, ("NKS AudioLink — " + status).Length)];
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port) || port is < 1 or > 65535)
        {
            DiscoveryText.Text = "Zadejte platný UDP port.";
            return;
        }
        byte[] key;
        try { key = string.IsNullOrWhiteSpace(PskBox.Password) ? [] : Convert.FromBase64String(PskBox.Password.Trim()); }
        catch (FormatException) { DiscoveryText.Text = "Sdílený klíč musí být Base64."; return; }
        DiscoverButton.IsEnabled = false;
        DiscoveryText.Text = "Hledám server v místní síti…";
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
                DiscoveryText.Text = $"Nalezeno: {found.Message.Name} ({ServerBox.Text}, výstup {found.Message.Sink})";
                SaveSettings();
            }
            else DiscoveryText.Text = "Server neodpověděl. Zadejte adresu ručně nebo zkontrolujte síť a sdílený klíč.";
        }
        catch (OperationCanceledException) { DiscoveryText.Text = "Server neodpověděl. Zadejte adresu ručně nebo zkontrolujte síť a sdílený klíč."; }
        catch (Exception ex) { DiscoveryText.Text = "Hledání selhalo: " + ex.Message; }
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
        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Otevřít");
        open.Click += (_, _) => Dispatcher.BeginInvoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
        _trayConnect = new Forms.ToolStripMenuItem("Připojit");
        _trayConnect.Click += (_, _) => Dispatcher.BeginInvoke(() => Connect_Click(this, new RoutedEventArgs()));
        var quit = new Forms.ToolStripMenuItem("Ukončit");
        quit.Click += (_, _) => Dispatcher.BeginInvoke(() => { _closing = true; Close(); });
        menu.Items.AddRange([open, _trayConnect, new Forms.ToolStripSeparator(), quit]);
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "NKS AudioLink — Odpojeno", ContextMenuStrip = menu, Visible = true };
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
        catch (Exception ex) { DetailText.Text = "Automatické spuštění nelze změnit: " + ex.Message; }
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
        _streamStop?.Cancel();
        try { VirtualCableRouting.RestorePending(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            _closing = false;
            Show();
            SetStatus("Obnova zvukovky selhala", ex.Message);
            return;
        }
        _healthTimer.Stop();
        _tray?.Dispose();
    }
}

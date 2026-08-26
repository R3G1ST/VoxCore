using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed partial class SettingsView : UserControl
{
    private AppSettings? _settings;
    private VoiceClient? _voice;
    private WebRTCVoiceClient? _webrtc;
    private readonly MicLevelMeter _meter = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _testing;
    private bool _inited;
    private UpdateInfo? _update;

    public event System.Action? CloseRequested;

    public SettingsView()
    {
        InitializeComponent();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateVoiceStatus();
    }

    public void Init(AppSettings settings, VoiceClient voice, WebRTCVoiceClient? webrtc)
    {
        if (_inited) return;
        _inited = true;
        _settings = settings;
        _voice = voice;
        _webrtc = webrtc;

        var devices = MicLevelMeter.GetDevices();
        foreach (var d in devices) MicCombo.Items.Add(d);
        if (MicCombo.Items.Count == 0) MicCombo.Items.Add("микрофон по умолчанию");
        MicCombo.SelectedIndex = settings.MicDevice < MicCombo.Items.Count ? settings.MicDevice : 0;

        GainSlider.Value = settings.MicGain;
        GainText.Text = $"{settings.MicGain:0}%";
        NsToggle.IsOn = settings.NoiseSuppression;
        AgcToggle.IsOn = settings.AgcEnabled;
        DfAttSlider.Value = settings.DfAttLim;
        DfAttText.Text = $"{settings.DfAttLim:0} dB";
        EqLowSlider.Value = settings.EqLow;
        EqMidSlider.Value = settings.EqMid;
        EqHighSlider.Value = settings.EqHigh;
        EqLowText.Text = $"{settings.EqLow:0} dB";
        EqMidText.Text = $"{settings.EqMid:0} dB";
        EqHighText.Text = $"{settings.EqHigh:0} dB";
        LoopbackCheck.IsChecked = false;

        // Server selection
        var servers = new List<(string Label, string Address, int VoicePort)>
        {
            ("🇳🇱 NL сервер (Европа)", "194.31.204.5:9988", 9987),
            ("🇷🇺 RU сервер (Россия)", "89.169.4.132:9990", 9989),
        };
        foreach (var s in servers) ServerCombo.Items.Add(s.Label);
        var currentServer = settings.Server;
        ServerCombo.SelectedIndex = servers.FindIndex(s => s.Address == currentServer);
        if (ServerCombo.SelectedIndex < 0) ServerCombo.SelectedIndex = 0;
        // Set voice port based on current server
        var currentIdx = ServerCombo.SelectedIndex;
        if (currentIdx >= 0 && currentIdx < servers.Count)
            _settings.VoicePort = servers[currentIdx].VoicePort;
        ForceUdpToggle.IsOn = settings.ForceUdp;
        UpdateServerInfo();

        VolumeSlider.Value = 80;
        VolumeText.Text = "80%";
        VolumeSlider.ValueChanged += (_, e) =>
        {
            VolumeText.Text = $"{e.NewValue:0}%";
            _voice.Volume = (int)e.NewValue;
            if (_webrtc is not null) _webrtc.Volume = (int)e.NewValue;
        };

        DfAttSlider.ValueChanged += (_, e) => DfAttText.Text = $"{e.NewValue:0} dB";
        EqLowSlider.ValueChanged += (_, e) => { EqLowText.Text = $"{e.NewValue:0} dB"; _webrtc?.ApplyEq(EqLowSlider.Value, EqMidSlider.Value, EqHighSlider.Value); };
        EqMidSlider.ValueChanged += (_, e) => { EqMidText.Text = $"{e.NewValue:0} dB"; _webrtc?.ApplyEq(EqLowSlider.Value, EqMidSlider.Value, EqHighSlider.Value); };
        EqHighSlider.ValueChanged += (_, e) => { EqHighText.Text = $"{e.NewValue:0} dB"; _webrtc?.ApplyEq(EqLowSlider.Value, EqMidSlider.Value, EqHighSlider.Value); };

        GainSlider.ValueChanged += (_, e) => GainText.Text = $"{e.NewValue:0}%";
        _meter.LevelChanged += level =>
            DispatcherQueue.TryEnqueue(() =>
                MeterFill.Width = MeterTrack.ActualWidth * Math.Clamp(level, 0f, 1f));

        VerText.Text = $"VoxCore {UpdateService.CurrentVersion}";

        _statusTimer.Start();
        UpdateVoiceStatus();
    }

    public void Shutdown()
    {
        StopTest();
        _statusTimer.Stop();
    }

    private static SolidColorBrush Green() => new(Windows.UI.Color.FromArgb(255, 46, 234, 139));
    private static SolidColorBrush Red() => new(Windows.UI.Color.FromArgb(255, 255, 59, 92));
    private static SolidColorBrush Yellow() => new(Windows.UI.Color.FromArgb(255, 250, 166, 26));
    private static SolidColorBrush Gray() => new(Windows.UI.Color.FromArgb(255, 90, 97, 122));

    private void UpdateVoiceStatus()
    {
        if (_voice is null) return;

        var webrtcConnected = _webrtc?.IsConnected == true;
        var udpConnected = !webrtcConnected && _voice.IsConnected;

        HpfDot.Fill = Green();
        HpfStatus.Text = "активен";
        HpfStatus.Foreground = Green();

        var aecOn = _webrtc?.IsAecActive == true;
        AecDot.Fill = aecOn ? Green() : Gray();
        AecStatus.Text = aecOn ? "активен" : "нет DLL";
        AecStatus.Foreground = aecOn ? Green() : Gray();

        var bitrate = _webrtc?.BitrateKbps ?? 0;
        OpusStatus.Text = bitrate > 0 ? $"{bitrate} kbps" : "64 kbps";

        var nsOn = NsToggle.IsOn;
        RnnoiseDot.Fill = nsOn ? Green() : Red();
        RnnoiseStatus.Text = nsOn ? "активно" : "выключено";
        RnnoiseStatus.Foreground = nsOn ? Green() : Red();

        var dfLoaded = _webrtc?.IsDeepFilterLoaded == true;
        DfDot.Fill = dfLoaded ? Green() : Gray();
        DfStatus.Text = dfLoaded ? "загружен" : "нет DLL";
        DfStatus.Foreground = dfLoaded ? Green() : Gray();

        AgcDot.Fill = AgcToggle.IsOn ? Green() : Gray();
        AgcStatus.Text = AgcToggle.IsOn ? "активен" : "выключено";
        AgcStatus.Foreground = AgcToggle.IsOn ? Green() : Gray();

        bool dredOn = _webrtc?.IsDredActive == true;
        bool fecOn = dredOn || _webrtc?.IsFec == true;
        FecDot.Fill = fecOn ? Green() : Gray();
        FecStatus.Text = dredOn ? "DRED 40ms" : (fecOn ? "FEC" : "выключено");
        FecStatus.Foreground = fecOn ? Green() : Gray();

        // VAD
        if (webrtcConnected && _webrtc?.IsVadLoaded == true)
        {
            double p = _webrtc!.VadProb;
            double t = _webrtc.VadThreshold;
            bool speaking = p >= t;
            VadDot.Fill = speaking ? Green() : Yellow();
            VadStatus.Text = $"{p:0.00} / {t:0.00} {(speaking ? "речь" : "тишина")}";
            VadStatus.Foreground = speaking ? Green() : Yellow();
        }
        else if (udpConnected)
        {
            double p = _voice.VadProb;
            double t = 0.1;
            bool speaking = p >= t;
            VadDot.Fill = speaking ? Green() : Yellow();
            VadStatus.Text = $"{p:0.00} / {t:0.00} {(speaking ? "речь" : "тишина")}";
            VadStatus.Foreground = speaking ? Green() : Yellow();
        }
        else
        {
            VadDot.Fill = Gray();
            VadStatus.Text = "—";
            VadStatus.Foreground = Gray();
        }

        bool gateOpen = _webrtc?.IsGateOpen == true || udpConnected;
        GateDot.Fill = gateOpen ? Green() : Gray();
        GateStatus.Text = gateOpen ? "открыт" : "зажат (-40dB)";
        GateStatus.Foreground = gateOpen ? Green() : Gray();
        if (webrtcConnected)
        {
            ConnDot.Fill = Green();
            ConnProtocol.Text = "WebRTC подключен";
            ConnProtocol.Foreground = Green();
            IceDot.Fill = Green();
            IceStatus.Text = "connected";
            IceStatus.Foreground = Green();
            TurnDot.Fill = Green();
            TurnStatus.Text = "relay активен";
            TurnStatus.Foreground = Green();
        }
        else if (udpConnected)
        {
            ConnDot.Fill = Yellow();
            ConnProtocol.Text = "UDP подключен";
            ConnProtocol.Foreground = Yellow();
            IceDot.Fill = Gray();
            IceStatus.Text = "не используется";
            TurnDot.Fill = Gray();
            TurnStatus.Text = "не используется";
        }
        else
        {
            ConnDot.Fill = Gray();
            ConnProtocol.Text = "не подключен";
            ConnProtocol.Foreground = Gray();
            IceDot.Fill = Gray();
            IceStatus.Text = "—";
            TurnDot.Fill = Gray();
            TurnStatus.Text = $"{_settings?.Server?.Split(':')[0] ?? "?"}:3478";
        }

        int ping = _webrtc?.IsConnected == true ? _webrtc.LastPingMs : (_voice?.LastPingMs ?? -1);
        if (ping < 0)
        {
            PingDot.Fill = Gray();
            PingStatus.Text = "—";
            PingStatus.Foreground = Gray();
        }
        else if (ping < 50)
        {
            PingDot.Fill = Green();
            PingStatus.Text = $"{ping} ms";
            PingStatus.Foreground = Green();
        }
        else if (ping < 100)
        {
            PingDot.Fill = Yellow();
            PingStatus.Text = $"{ping} ms";
            PingStatus.Foreground = Yellow();
        }
        else
        {
            PingDot.Fill = Red();
            PingStatus.Text = $"{ping} ms";
            PingStatus.Foreground = Red();
        }

        if (_webrtc?.IsConnected == true)
        {
            var (t, b, l) = _webrtc!.JitterStats;
            JitDot.Fill = b <= t + 40 ? Green() : Yellow();
            JitStatus.Text = $"{t}ms / {b}ms";
            JitStatus.Foreground = Green();
            LossDot.Fill = l == 0 ? Green() : (l < 10 ? Yellow() : Red());
            LossStatus.Text = $"{l} кадров";
            LossStatus.Foreground = l == 0 ? Green() : (l < 10 ? Yellow() : Red());
        }
        else
        {
            JitDot.Fill = Gray();
            JitStatus.Text = "—";
            LossDot.Fill = Gray();
            LossStatus.Text = "—";
        }
    }

    private async void CheckUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatus.Text = "проверяю...";
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info is null)
            {
                UpdateStatus.Text = $"у вас последняя версия {UpdateService.CurrentVersion}";
                InstallUpdateBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                _update = info;
                UpdateStatus.Text = $"доступно обновление до v{info.Version}";
                InstallUpdateBtn.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            UpdateStatus.Text = "не удалось проверить обновления (нет интернета?)";
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private async void InstallUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        CheckUpdateBtn.IsEnabled = false;
        InstallUpdateBtn.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateStatus.Text = "скачиваю обновление...";
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"VoxCore-Setup-{_update.Version}.exe");
            var progress = new Progress<double>(p => UpdateProgress.Value = p);
            await UpdateService.DownloadAsync(_update.DownloadUrl, path, progress);
            UpdateStatus.Text = "обновление скачано, запускаю установку...";
            await Task.Delay(500);
            UpdateService.LaunchInstaller(path);
            Application.Current.Exit();
        }
        catch
        {
            UpdateStatus.Text = "не удалось скачать обновление";
            InstallUpdateBtn.IsEnabled = true;
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_testing)
        {
            StopTest();
            return;
        }
        _meter.DeviceIndex = MicCombo.SelectedIndex;
        _meter.Loopback = LoopbackCheck.IsChecked == true;
        _meter.Start();
        _testing = true;
        TestBtn.Content = "СТОП";
        TestInfo.Text = "говори в микрофон...";
    }

    private void StopTest()
    {
        _meter.Stop();
        _testing = false;
        TestBtn.Content = "НАЧАТЬ ТЕСТ";
        TestInfo.Text = "";
        MeterFill.Width = 0;
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _voice is null) return;
        StopTest();
        _settings.MicDevice = MicCombo.SelectedIndex;
        _settings.MicGain = GainSlider.Value;
        _settings.NoiseSuppression = NsToggle.IsOn;
        _settings.AgcEnabled = AgcToggle.IsOn;
        _settings.DfAttLim = DfAttSlider.Value;
        _settings.EqLow = EqLowSlider.Value;
        _settings.EqMid = EqMidSlider.Value;
        _settings.EqHigh = EqHighSlider.Value;
        // Save server selection
        var servers = new List<(string Address, int VoicePort)> { ("194.31.204.5:9988", 9987), ("89.169.4.132:9990", 9989) };
        if (ServerCombo.SelectedIndex >= 0 && ServerCombo.SelectedIndex < servers.Count)
        {
            _settings.Server = servers[ServerCombo.SelectedIndex].Address;
            _settings.VoicePort = servers[ServerCombo.SelectedIndex].VoicePort;
        }
        _settings.ForceUdp = ForceUdpToggle.IsOn;
        _voice.MicGain = GainSlider.Value / 100.0;
        _voice.InputDevice = MicCombo.SelectedIndex;
        _voice.NoiseSuppression = NsToggle.IsOn;
        if (_webrtc is not null)
        {
            _webrtc.NoiseSuppression = NsToggle.IsOn;
            _webrtc.AgcEnabled = AgcToggle.IsOn;
            _webrtc.ApplyEq(EqLowSlider.Value, EqMidSlider.Value, EqHighSlider.Value);
        }
        _settings.Save();
        UpdateVoiceStatus();
        CloseRequested?.Invoke();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        StopTest();
        CloseRequested?.Invoke();
    }

    private void ServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateServerInfo();
    }

    private void UpdateServerInfo()
    {
        var servers = new List<(string Label, string Address, string Ping)>
        {
            ("🇳🇱 NL", "194.31.204.5:9988", "50ms"),
            ("🇷🇺 RU", "89.169.4.132:9990", "20-40ms"),
        };
        var idx = ServerCombo.SelectedIndex;
        if (idx >= 0 && idx < servers.Count)
        {
            ServerInfo.Text = $"API: {servers[idx].Address} • UDP: {(idx == 0 ? "9987" : "9989")} • Пинг: ~{servers[idx].Ping}";
            ServerInfo.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 94, 100, 126));
        }
        else
        {
            ServerInfo.Text = "";
        }
    }
}

using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly VoiceClient _voice;
    private readonly WebRTCVoiceClient? _webrtc;
    private readonly MicLevelMeter _meter = new();
    private readonly DispatcherTimer _statusTimer;
    private bool _testing;
    private UpdateInfo? _update;

    public SettingsWindow(AppSettings settings, VoiceClient voice, WebRTCVoiceClient? webrtc = null)
    {
        _settings = settings;
        _voice = voice;
        _webrtc = webrtc;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 780));
        AppWindow.Title = "VoxCore — настройки";

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

        VolumeSlider.Value = 80;
        VolumeText.Text = "80%";
        VolumeSlider.ValueChanged += (_, e) =>
        {
            VolumeText.Text = $"{e.NewValue:0}%";
            _voice.Volume = (int)e.NewValue;
            _webrtc.Volume = (int)e.NewValue;
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

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateVoiceStatus();
        _statusTimer.Start();
        UpdateVoiceStatus();
    }

    private static SolidColorBrush Green() => new(Windows.UI.Color.FromArgb(255, 59, 165, 93));
    private static SolidColorBrush Red() => new(Windows.UI.Color.FromArgb(255, 237, 66, 69));
    private static SolidColorBrush Yellow() => new(Windows.UI.Color.FromArgb(255, 250, 166, 26));
    private static SolidColorBrush Gray() => new(Windows.UI.Color.FromArgb(255, 90, 90, 106));

    private void UpdateVoiceStatus()
    {
        // --- Pipeline ---
        var bitrate = _webrtc?.BitrateKbps ?? 0;
        OpusStatus.Text = bitrate > 0 ? $"{bitrate} kbps" : "48 kHz моно";

        var nsOn = NsToggle.IsOn;
        RnnoiseDot.Fill = nsOn ? Green() : Red();
        RnnoiseStatus.Text = nsOn ? "активно" : "выключено";
        RnnoiseStatus.Foreground = nsOn ? Green() : Red();

        var dfLoaded = _webrtc?.IsDeepFilterLoaded == true;
        DfDot.Fill = dfLoaded ? Green() : Gray();
        DfStatus.Text = dfLoaded ? "загружен" : "нет DLL в %LOCALAPPDATA%\\VoxCore\\native";
        DfStatus.Foreground = dfLoaded ? Green() : Gray();

        var agcOn = AgcToggle.IsOn && _webrtc != null;
        if (agcOn)
        {
            AgcDot.Fill = Green();
            double gdb = _webrtc!.AgcGainDb;
            AgcStatus.Text = $"активен {gdb:+0;-0;0}dB";
            AgcStatus.Foreground = Green();
        }
        else
        {
            AgcDot.Fill = Gray();
            AgcStatus.Text = "выключено";
            AgcStatus.Foreground = Gray();
        }

        var fecOn = _webrtc?.IsFec == true;
        FecDot.Fill = fecOn ? Green() : Gray();
        FecStatus.Text = fecOn ? "активно" : "выключено";
        FecStatus.Foreground = fecOn ? Green() : Gray();

        var dtxOn = _webrtc?.IsDtx == true;
        DtxDot.Fill = dtxOn ? Yellow() : Gray();
        DtxStatus.Text = dtxOn ? "WebRTC" : "выключено";
        DtxStatus.Foreground = dtxOn ? Yellow() : Gray();

        var vadLoaded = _webrtc?.IsVadLoaded == true;
        if (!vadLoaded)
        {
            VadDot.Fill = Gray();
            VadStatus.Text = "нет модели";
            VadStatus.Foreground = Gray();
        }
        else
        {
            double p = _webrtc!.VadProb;
            bool speaking = p >= 0.5;
            VadDot.Fill = speaking ? Green() : Yellow();
            VadStatus.Text = $"{p:0.00} / 0.50 {(speaking ? "речь" : "тишина")}";
            VadStatus.Foreground = speaking ? Green() : Yellow();
        }

        bool gateOpen = _webrtc?.IsGateOpen ?? false;
        GateDot.Fill = gateOpen ? Green() : Gray();
        GateStatus.Text = gateOpen ? "открыт" : "зажат (-40dB)";
        GateStatus.Foreground = gateOpen ? Green() : Gray();

        // --- Connection ---
        var webrtcConnected = _webrtc?.IsConnected == true;
        var udpConnected = !webrtcConnected && _voice.IsConnected;
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
            TurnStatus.Text = "194.31.204.5:3478";
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
        StopTest();
        _settings.MicDevice = MicCombo.SelectedIndex;
        _settings.MicGain = GainSlider.Value;
        _settings.NoiseSuppression = NsToggle.IsOn;
        _settings.AgcEnabled = AgcToggle.IsOn;
        _settings.DfAttLim = DfAttSlider.Value;
        _settings.EqLow = EqLowSlider.Value;
        _settings.EqMid = EqMidSlider.Value;
        _settings.EqHigh = EqHighSlider.Value;
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
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        StopTest();
        _statusTimer.Stop();
        Close();
    }
}

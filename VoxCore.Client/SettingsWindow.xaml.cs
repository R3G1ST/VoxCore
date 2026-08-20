using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly VoiceClient _voice;
    private readonly MicLevelMeter _meter = new();
    private bool _testing;

    public SettingsWindow(AppSettings settings, VoiceClient voice)
    {
        _settings = settings;
        _voice = voice;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 640));
        AppWindow.Title = "VoxCore — настройки";

        var devices = MicLevelMeter.GetDevices();
        foreach (var d in devices) MicCombo.Items.Add(d);
        if (MicCombo.Items.Count == 0) MicCombo.Items.Add("микрофон по умолчанию");
        MicCombo.SelectedIndex = settings.MicDevice < MicCombo.Items.Count ? settings.MicDevice : 0;

        GainSlider.Value = settings.MicGain;
        GainText.Text = $"{settings.MicGain:0}%";
        NsToggle.IsOn = settings.NoiseSuppression;
        LoopbackCheck.IsChecked = false;

        GainSlider.ValueChanged += (_, e) => GainText.Text = $"{e.NewValue:0}%";
        _meter.LevelChanged += level =>
            DispatcherQueue.TryEnqueue(() =>
                MeterFill.Width = MeterTrack.ActualWidth * Math.Clamp(level, 0f, 1f));
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
        _voice.MicGain = GainSlider.Value / 100.0;
        _voice.InputDevice = MicCombo.SelectedIndex;
        _voice.NoiseSuppression = NsToggle.IsOn;
        _settings.Save();
        Close();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        StopTest();
        Close();
    }
}
using System.Threading.Tasks;
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
    private UpdateInfo? _update;

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

        VerText.Text = $"VoxCore {UpdateService.CurrentVersion}";
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
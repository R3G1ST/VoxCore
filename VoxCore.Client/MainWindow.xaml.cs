using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VoxCore.Client;

public sealed partial class MainWindow : Window
{
    private readonly VoiceClient _voice = new();
    private readonly MicLevelMeter _micMeter = new();
    private readonly ObservableCollection<MemberItem> _members = [];
    private readonly AppSettings _settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(860, 640));
        AppWindow.Title = "VoxCore";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragArea);
        MembersList.ItemsSource = _members;
        var devices = MicLevelMeter.GetDevices();
        MicDevicesCombo.ItemsSource = devices;
        if (devices.Length > 0)
            MicDevicesCombo.SelectedIndex = Math.Clamp(_settings.MicDevice, 0, devices.Length - 1);
        ServerBox.Text = _settings.Server;
        RoomBox.Text = _settings.Room;
        NameBox.Text = _settings.Nickname;
        OpenMicBtn.IsChecked = _settings.OpenMic;
        NoiseSuppressBtn.IsChecked = _settings.NoiseSuppression;
        _voice.NoiseSuppression = _settings.NoiseSuppression;
        RoomPasswordBox.Password = _settings.RoomPassword;
        _micMeter.LevelChanged += OnMicLevel;
        _voice.MembersChanged += OnMembersChanged;
        _voice.TalkingChanged += OnTalkingChanged;
        _voice.StatusChanged += OnStatusChanged;
        _voice.SpeakerStarted += OnSpeakerStarted;
        _voice.SpeakerStopped += OnSpeakerStopped;
        Closed += (_, _) => { SaveSettings(); _voice.Dispose(); _micMeter.Dispose(); };
        MicGainSlider.ValueChanged += MicGainSlider_ValueChanged;
        MicGainSlider.Value = _settings.MicGain;
        MicGainText.Text = $"{(int)_settings.MicGain}%";
    }

    private void SaveSettings()
    {
        _settings.Server = ServerBox.Text;
        _settings.Room = RoomBox.Text;
        _settings.Nickname = NameBox.Text;
        _settings.MicDevice = Math.Max(0, MicDevicesCombo.SelectedIndex);
        _settings.MicGain = MicGainSlider.Value;
        _settings.OpenMic = OpenMicBtn.IsChecked == true;
        _settings.RoomPassword = RoomPasswordBox.Password;
        _settings.Save();
    }

    private void MicDevicesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.MicDevice = Math.Max(0, MicDevicesCombo.SelectedIndex);
        _settings.Save();
    }

    private void OnMicLevel(float level)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MicLevelBar.Width = MicLevelTrack.ActualWidth * level;
            MicLevelText.Text = $"{(int)(level * 100)}%";
        });
    }

    private void RoomPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _settings.RoomPassword = RoomPasswordBox.Password;
        _settings.Save();
    }

    private void NoiseSuppressBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.NoiseSuppression = true;
        _settings.NoiseSuppression = true;
        _settings.Save();
    }

    private void NoiseSuppressBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.NoiseSuppression = false;
        _settings.NoiseSuppression = false;
        _settings.Save();
    }

    private void RoomTabBtn_Click(object sender, RoutedEventArgs e)
    {
        RoomPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        RoomTabBtn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 255, 136));
        RoomTabBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 3, 3, 6));
        SettingsTabBtn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0, 0, 0, 0));
        SettingsTabBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 240));
    }

    private void SettingsTabBtn_Click(object sender, RoutedEventArgs e)
    {
        RoomPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsTabBtn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 255, 136));
        SettingsTabBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 3, 3, 6));
        RoomTabBtn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0, 0, 0, 0));
        RoomTabBtn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 240));
    }

    private void MicTestBtn_Checked(object sender, RoutedEventArgs e)
    {
        _micMeter.DeviceIndex = MicDevicesCombo.SelectedIndex;
        _micMeter.Loopback = LoopbackBtn.IsChecked == true;
        _micMeter.Start();
        MicTestBtn.Content = "СТОП";
        MicLevelText.Text = "0%";
    }

    private void MicTestBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _micMeter.Stop();
        MicTestBtn.Content = "ТЕСТ МИКРОФОНА";
        MicLevelBar.Width = 0;
        MicLevelText.Text = "0%";
    }

    private void LoopbackBtn_Checked(object sender, RoutedEventArgs e) => RestartTestIfRunning();

    private void LoopbackBtn_Unchecked(object sender, RoutedEventArgs e) => RestartTestIfRunning();

    private void RestartTestIfRunning()
    {
        if (MicTestBtn.IsChecked == true)
        {
            _micMeter.Stop();
            _micMeter.Loopback = LoopbackBtn.IsChecked == true;
            _micMeter.Start();
        }
    }

    private void MicGainSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _voice.MicGain = e.NewValue / 100.0;
        MicGainText.Text = $"{(int)e.NewValue}%";
        _settings.MicGain = e.NewValue;
        _settings.Save();
    }

    private void OnMembersChanged(IReadOnlyList<string> names)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _members.Clear();
            foreach (var n in names)
                _members.Add(new MemberItem(n));
        });
    }

    private void OnSpeakerStarted(string name)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var m in _members)
                if (m.Name == name) m.IsSpeaking = true;
        });
    }

    private void OnSpeakerStopped(string name)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var m in _members)
                if (m.Name == name) m.IsSpeaking = false;
        });
    }

    private void OnTalkingChanged(bool talking)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TalkBadge.Visibility = talking ? Visibility.Visible : Visibility.Collapsed;
            TalkBadge.Foreground = talking
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        });
    }

    private void OnStatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() => StatusText.Text = status);
    }

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_voice.IsConnected)
        {
            _voice.Disconnect();
            ConnectBtn.Content = "ПОДКЛЮЧИТЬСЯ";
            return;
        }
        if (!int.TryParse(ServerPort, out var port)) port = 9987;
        var server = ServerHost;
        var room = RoomBox.Text.Trim();
        var name = NameBox.Text.Trim();
        if (server.Length == 0 || room.Length == 0 || name.Length == 0) return;
        if (MicTestBtn.IsChecked == true)
        {
            _micMeter.Stop();
            MicTestBtn.IsChecked = false;
        }
        _voice.InputDevice = MicDevicesCombo.SelectedIndex;
        _voice.Connect(server, port, room, name, RoomPasswordBox.Password);
        ConnectBtn.Content = "ОТКЛЮЧИТЬСЯ";
        SaveSettings();
    }

    private string ServerPort
    {
        get
        {
            var parts = ServerBox.Text.Split(':');
            return parts.Length > 1 ? parts[1] : "9987";
        }
    }

    private string ServerHost
    {
        get
        {
            var parts = ServerBox.Text.Split(':');
            return parts[0];
        }
    }

    private void OpenMicBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.OpenMic = true;
        _settings.OpenMic = true;
        _settings.Save();
    }

    private void OpenMicBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.OpenMic = false;
        _settings.OpenMic = false;
        _settings.Save();
    }
}
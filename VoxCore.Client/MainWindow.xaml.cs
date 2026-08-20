using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed partial class MainWindow : Window
{
    private readonly ApiClient _api;
    private readonly AppSettings _settings;
    private readonly VoiceClient _voice = new();
    private readonly ObservableCollection<MemberItem> _members = [];
    private readonly UserInfo _user;
    private readonly DispatcherTimer _refreshTimer;
    private List<ChannelInfo> _channels = [];
    private ChannelInfo? _currentChannel;

    public MainWindow(ApiClient api, AppSettings settings, UserInfo user)
    {
        _api = api;
        _settings = settings;
        _user = user;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 700));
        AppWindow.Title = "VoxCore";

        AvatarBorder.Background = BrushFromHex(user.Color);
        AvatarLetter.Text = user.Name.Length > 0 ? user.Name[..1].ToUpperInvariant() : "?";
        UserNameText.Text = user.Name;

        MembersList.ItemsSource = _members;
        _voice.MembersChanged += OnMembersChanged;
        _voice.TalkingChanged += OnTalkingChanged;
        _voice.StatusChanged += OnStatusChanged;
        _voice.SpeakerStarted += OnSpeakerStarted;
        _voice.SpeakerStopped += OnSpeakerStopped;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += async (_, _) => await RefreshChannelsAsync();
        _refreshTimer.Start();

        Closed += OnWindowClosed;
        _ = RefreshChannelsAsync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _voice.Dispose();
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.Save();
    }

    // ---------- РљР°РЅР°Р»С‹ ----------

    private async Task RefreshChannelsAsync()
    {
        try
        {
            var channels = await _api.GetChannelsAsync();
            _channels = channels;
            RenderChannels();
        }
        catch
        {
            // СЃРµСЂРІРµСЂ РЅРµРґРѕСЃС‚СѓРїРµРЅ вЂ” РїСЂРѕСЃС‚Рѕ РЅРµ РѕР±РЅРѕРІР»СЏРµРј
        }
    }

    private void RenderChannels()
    {
        ChannelsPanel.Children.Clear();
        foreach (var ch in _channels)
        {
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 9, 10, 9),
                CornerRadius = new CornerRadius(6),
                Background = BrushFromHex(_currentChannel?.Id == ch.Id ? "#3f4248" : "#00000000"),
                BorderThickness = new Thickness(0)
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "рџ”Љ",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            var nameTb = new TextBlock
            {
                Text = ch.Name + (ch.HasPassword ? " рџ”’" : ""),
                Foreground = BrushFromHex("#dbdee1"),
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(nameTb);
            if (ch.Users > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = ch.Users.ToString(),
                    Foreground = BrushFromHex("#949ba4"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            if (ch.OwnerId == _user.Id)
            {
                var delBtn = new Button
                {
                    Content = "рџ—‘",
                    Padding = new Thickness(4, 2, 4, 2),
                    Background = BrushFromHex("#00000000"),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                delBtn.Click += async (_, _) => await DeleteChannelAsync(ch);
                panel.Children.Add(delBtn);
            }
            btn.Content = panel;
            btn.Click += async (_, _) => await JoinChannelAsync(ch);
            ChannelsPanel.Children.Add(btn);
        }
    }

    private async Task CreateChannelAsync()
    {
        var nameBox = new TextBox { PlaceholderText = "РЅР°Р·РІР°РЅРёРµ РєР°РЅР°Р»Р°" };
        var passBox = new PasswordBox { PlaceholderText = "РїР°СЂРѕР»СЊ (РЅРµРѕР±СЏР·Р°С‚РµР»СЊРЅРѕ)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(nameBox);
        panel.Children.Add(passBox);
        var dialog = new ContentDialog
        {
            Title = "РЎРѕР·РґР°С‚СЊ РєР°РЅР°Р»",
            Content = panel,
            PrimaryButtonText = "РЎРћР—Р”РђРўР¬",
            CloseButtonText = "РћРўРњР•РќРђ",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        if (name.Length == 0) return;
        try
        {
            await _api.CreateChannelAsync(name, passBox.Password);
            await RefreshChannelsAsync();
        }
        catch (ApiException ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        catch
        {
            await ShowErrorAsync("РЅРµС‚ СЃРІСЏР·Рё СЃ СЃРµСЂРІРµСЂРѕРј");
        }
    }

    private async Task DeleteChannelAsync(ChannelInfo ch)
    {
        var dialog = new ContentDialog
        {
            Title = $"РЈРґР°Р»РёС‚СЊ РєР°РЅР°Р» В«{ch.Name}В»?",
            Content = "РґРµР№СЃС‚РІРёРµ РЅРµРѕР±СЂР°С‚РёРјРѕ",
            PrimaryButtonText = "РЈР”РђР›РРўР¬",
            CloseButtonText = "РћРўРњР•РќРђ",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _api.DeleteChannelAsync(ch.Id);
            if (_currentChannel?.Id == ch.Id) LeaveChannel();
            await RefreshChannelsAsync();
        }
        catch (ApiException ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        catch
        {
            await ShowErrorAsync("РЅРµС‚ СЃРІСЏР·Рё СЃ СЃРµСЂРІРµСЂРѕРј");
        }
    }

    private async Task JoinChannelAsync(ChannelInfo ch)
    {
        if (_currentChannel?.Id == ch.Id) return;
        string password = "";
        if (ch.HasPassword)
        {
            var passBox = new PasswordBox { PlaceholderText = "РїР°СЂРѕР»СЊ РєР°РЅР°Р»Р°" };
            var dialog = new ContentDialog
            {
                Title = $"Р’С…РѕРґ РІ В«{ch.Name}В»",
                Content = passBox,
                PrimaryButtonText = "Р’РћР™РўР",
                CloseButtonText = "РћРўРњР•РќРђ",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            password = passBox.Password;
            try
            {
                await _api.VerifyChannelPasswordAsync(ch.Id, password);
            }
            catch (ApiException ex)
            {
                await ShowErrorAsync(ex.Message);
                return;
            }
            catch
            {
                await ShowErrorAsync("РЅРµС‚ СЃРІСЏР·Рё СЃ СЃРµСЂРІРµСЂРѕРј");
                return;
            }
        }

        LeaveChannel();
        var host = _settings.Server.Split(':')[0];
        _voice.Connect(host, 9987, ch.Id.ToString(), _user.Name, password);
        _currentChannel = ch;
        ChannelNameText.Text = ch.Name;
        ChannelStatusText.Text = "РІ РіРѕР»РѕСЃРѕРІРѕРј РєР°РЅР°Р»Рµ";
        LeaveChannelBtn.Visibility = Visibility.Visible;
        StatusText.Text = $"РїРѕРґРєР»СЋС‡РµРЅРѕ Рє {ch.Name}";
        RenderChannels();
    }

    private void LeaveChannel()
    {
        _voice.Disconnect();
        _currentChannel = null;
        ChannelNameText.Text = "РЅРµ РІ РєР°РЅР°Р»Рµ";
        ChannelStatusText.Text = "РІС‹Р±РµСЂРё РєР°РЅР°Р» СЃР»РµРІР°";
        LeaveChannelBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "РѕС‚РєР»СЋС‡РµРЅРѕ";
        _members.Clear();
        RenderChannels();
    }

    private async Task ShowErrorAsync(string msg)
    {
        var dialog = new ContentDialog
        {
            Title = "РћС€РёР±РєР°",
            Content = msg,
            CloseButtonText = "РћРљ",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ---------- Р“РѕР»РѕСЃ ----------

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
            PttButton.Background = BrushFromHex(talking ? "#3ba55d" : "#5865f2");
            PttButtonText.Text = talking ? "Р“РћР’РћР РРЁР¬..." : "[SPACE] PTT";
        });
    }

    private void OnStatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() => StatusText.Text = status);
    }

    // ---------- PTT ----------

    private void PttButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _voice.OpenMic = true;
    }

    private void PttButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _voice.OpenMic = false;
    }

    // ---------- РљРЅРѕРїРєРё ----------

    private async void AddChannelBtn_Click(object sender, RoutedEventArgs e) => await CreateChannelAsync();

    private void LeaveChannelBtn_Click(object sender, RoutedEventArgs e) => LeaveChannel();

    private void MicMuteBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = true;
        MicMuteBtn.Content = "рџЋ™пёЏрџљ«";
    }

    private void MicMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = false;
        MicMuteBtn.Content = "рџЋ™пёЏ";
    }

    private void HeadMuteBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = true;
        HeadMuteBtn.Content = "рџ”‡";
    }

    private void HeadMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = false;
        HeadMuteBtn.Content = "рџ”Љ";
    }

    private void DeafenBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = true;
        _voice.PlaybackMuted = true;
        MicMuteBtn.IsChecked = true;
        HeadMuteBtn.IsChecked = true;
        DeafenBtn.Content = "рџ”‡вњ…";
    }

    private void DeafenBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = false;
        _voice.PlaybackMuted = false;
        MicMuteBtn.IsChecked = false;
        HeadMuteBtn.IsChecked = false;
        DeafenBtn.Content = "рџ”‡";
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var settingsWin = new SettingsWindow(_settings, _voice);
        settingsWin.Activate();
    }

    private void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        _voice.Dispose();
        _settings.Token = null;
        _settings.Save();
        var authWin = new AuthWindow(_settings, (api, settings, user) =>
        {
            var win = new MainWindow(api, settings, user);
            win.Activate();
        });
        authWin.Activate();
        Close();
    }

    // ---------- РЈС‚РёР»РёС‚С‹ ----------

    internal static SolidColorBrush BrushFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16)));
    }
}

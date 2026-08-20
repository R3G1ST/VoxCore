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

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        var tb = AppWindow.TitleBar;
        tb.BackgroundColor = ColorFromArgb(30, 31, 34);
        tb.ForegroundColor = ColorFromArgb(148, 155, 164);
        tb.ButtonBackgroundColor = ColorFromArgb(30, 31, 34);
        tb.ButtonForegroundColor = ColorFromArgb(148, 155, 164);
        tb.ButtonHoverBackgroundColor = ColorFromArgb(57, 60, 67);
        tb.ButtonHoverForegroundColor = ColorFromArgb(255, 255, 255);
        tb.ButtonPressedBackgroundColor = ColorFromArgb(35, 37, 43);
        tb.InactiveBackgroundColor = ColorFromArgb(30, 31, 34);
        tb.InactiveForegroundColor = ColorFromArgb(90, 94, 102);
        tb.ButtonInactiveBackgroundColor = ColorFromArgb(30, 31, 34);
        tb.ButtonInactiveForegroundColor = ColorFromArgb(90, 94, 102);

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
        _refreshTimer.Tick += async (_, _) =>
        {
            await RefreshChannelsAsync();
            await RefreshFriendsAsync();
        };
        _refreshTimer.Start();

        Closed += OnWindowClosed;
        _voice.OpenMic = true;
        _ = RefreshChannelsAsync();
        _ = RefreshFriendsAsync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _voice.Dispose();
        _settings.Save();
    }

    // ---------- Каналы ----------

    private async Task RefreshChannelsAsync()
    {
        try
        {
            var channels = await _api.GetChannelsAsync();
            _channels = channels;
            RenderChannels();
        }
        catch (ApiException ex) when (ex.Message == "unauthorized")
        {
            DispatcherQueue.TryEnqueue(ShowAuthAndClose);
        }
        catch
        {
            // сервер недоступен — просто не обновляем
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
                Text = "🔊",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            var nameTb = new TextBlock
            {
                Text = ch.Name + (ch.HasPassword ? " 🔒" : ""),
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
                    Content = "🗑",
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
        var nameBox = new TextBox { PlaceholderText = "название канала" };
        var passBox = new PasswordBox { PlaceholderText = "пароль (необязательно)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(nameBox);
        panel.Children.Add(passBox);
        var dialog = new ContentDialog
        {
            Title = "Создать канал",
            Content = panel,
            PrimaryButtonText = "СОЗДАТЬ",
            CloseButtonText = "ОТМЕНА",
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
            await ShowErrorAsync("нет связи с сервером");
        }
    }

    private async Task DeleteChannelAsync(ChannelInfo ch)
    {
        var dialog = new ContentDialog
        {
            Title = $"Удалить канал «{ch.Name}»?",
            Content = "действие необратимо",
            PrimaryButtonText = "УДАЛИТЬ",
            CloseButtonText = "ОТМЕНА",
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
            await ShowErrorAsync("нет связи с сервером");
        }
    }

    private async Task JoinChannelAsync(ChannelInfo ch)
    {
        if (_currentChannel?.Id == ch.Id) return;
        string password = "";
        if (ch.HasPassword)
        {
            var passBox = new PasswordBox { PlaceholderText = "пароль канала" };
            var dialog = new ContentDialog
            {
                Title = $"Вход в «{ch.Name}»",
                Content = passBox,
                PrimaryButtonText = "ВОЙТИ",
                CloseButtonText = "ОТМЕНА",
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
                await ShowErrorAsync("нет связи с сервером");
                return;
            }
        }

        LeaveChannel();
        var host = _settings.Server.Split(':')[0];
        _voice.Connect(host, 9987, ch.Id.ToString(), _user.Name, password);
        _currentChannel = ch;
        ChannelNameText.Text = ch.Name;
        ChannelStatusText.Text = "в голосовом канале";
        LeaveChannelBtn.Visibility = Visibility.Visible;
        StatusText.Text = $"подключено к {ch.Name}";
        RenderChannels();
    }

    private void LeaveChannel()
    {
        _voice.Disconnect();
        _currentChannel = null;
        ChannelNameText.Text = "не в канале";
        ChannelStatusText.Text = "выбери канал слева";
        LeaveChannelBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "отключено";
        _members.Clear();
        RenderChannels();
    }

    private async Task ShowErrorAsync(string msg)
    {
        var dialog = new ContentDialog
        {
            Title = "Ошибка",
            Content = msg,
            CloseButtonText = "ОК",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ---------- Друзья ----------

    private async Task RefreshFriendsAsync()
    {
        try
        {
            var friends = await _api.GetFriendsAsync();
            FriendsList.ItemsSource = friends.Select(FriendItem.FromUser).ToList();

            var requests = await _api.GetFriendRequestsAsync();
            var reqItems = requests.Select(FriendItem.FromUser).ToList();
            RequestsList.ItemsSource = reqItems;
            RequestsSection.Visibility = reqItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FriendsTabBtn.Content = reqItems.Count > 0 ? $"ДРУЗЬЯ ({reqItems.Count})" : "ДРУЗЬЯ";
        }
        catch (ApiException ex) when (ex.Message == "unauthorized")
        {
            DispatcherQueue.TryEnqueue(ShowAuthAndClose);
        }
        catch
        {
            // сервер недоступен — не обновляем
        }
    }

    private void ShowAuthAndClose()
    {
        _refreshTimer.Stop();
        _voice.Dispose();
        var authWin = new AuthWindow(_settings, (api, settings, user) =>
        {
            var win = new MainWindow(api, settings, user);
            win.Activate();
        });
        authWin.Activate();
        Close();
    }

    private async Task DoSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchResultsList.Visibility = Visibility.Collapsed;
            SearchInfo.Text = "";
            return;
        }
        SearchBtn.IsEnabled = false;
        SearchInfo.Text = "поиск...";
        try
        {
            var users = await _api.SearchUsersAsync(query);
            if (users.Count == 0)
            {
                SearchInfo.Text = "никого не найдено";
                SearchResultsList.Visibility = Visibility.Collapsed;
            }
            else
            {
                SearchInfo.Text = "";
                SearchResultsList.ItemsSource = users.Select(FriendItem.FromUser).ToList();
                SearchResultsList.Visibility = Visibility.Visible;
            }
        }
        catch (ApiException ex)
        {
            if (ex.Message == "unauthorized")
            {
                DispatcherQueue.TryEnqueue(ShowAuthAndClose);
                return;
            }
            SearchInfo.Text = ex.Message;
        }
        catch
        {
            SearchInfo.Text = "нет связи с сервером";
        }
        finally
        {
            SearchBtn.IsEnabled = true;
        }
    }

    private async void SearchBtn_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = DoSearchAsync();
    }

    private async void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FriendItem item) return;
        SearchInfo.Text = "отправляю запрос...";
        try
        {
            await _api.AddFriendAsync(item.Name);
            SearchInfo.Text = $"запрос отправлен {item.Name}";
            SearchResultsList.Visibility = Visibility.Collapsed;
        }
        catch (ApiException ex)
        {
            SearchInfo.Text = ex.Message;
        }
        catch
        {
            SearchInfo.Text = "нет связи с сервером";
        }
    }

    private async void RequestAccept_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        try
        {
            await _api.AcceptFriendRequestAsync(id);
            await RefreshFriendsAsync();
        }
        catch
        {
            // игнорируем
        }
    }

    private async void RequestDecline_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        try
        {
            await _api.DeclineFriendRequestAsync(id);
            await RefreshFriendsAsync();
        }
        catch
        {
            // игнорируем
        }
    }

    private async void FriendRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        try
        {
            await _api.RemoveFriendAsync(id);
            await RefreshFriendsAsync();
        }
        catch
        {
            // игнорируем
        }
    }

    private void ChannelsTabBtn_Click(object sender, RoutedEventArgs e)
    {
        ChannelsTabBtn.Background = MainWindow.BrushFromHex("#5865f2");
        FriendsTabBtn.Background = MainWindow.BrushFromHex("#3f4248");
        FriendsPanel.Visibility = Visibility.Collapsed;
        ChannelsHeader.Visibility = Visibility.Visible;
        ChannelsScroll.Visibility = Visibility.Visible;
    }

    private void FriendsTabBtn_Click(object sender, RoutedEventArgs e)
    {
        FriendsTabBtn.Background = MainWindow.BrushFromHex("#5865f2");
        ChannelsTabBtn.Background = MainWindow.BrushFromHex("#3f4248");
        FriendsPanel.Visibility = Visibility.Visible;
        ChannelsHeader.Visibility = Visibility.Collapsed;
        ChannelsScroll.Visibility = Visibility.Collapsed;
        _ = RefreshFriendsAsync();
    }

    // ---------- Голос ----------

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
            if (talking)
            {
                StatusText.Text = "говоришь...";
                StatusText.Foreground = BrushFromHex("#3ba55d");
            }
            else if (ModeToggle.IsChecked == true)
            {
                StatusText.Text = "микрофон активен — просто говори";
                StatusText.Foreground = BrushFromHex("#b5bac1");
            }
            else
            {
                StatusText.Text = "PTT — зажми пробел";
                StatusText.Foreground = BrushFromHex("#b5bac1");
            }
        });
    }

    private void OnStatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() => StatusText.Text = status);
    }

    // ---------- Режим микрофона ----------

    private void ModeToggle_Checked(object sender, RoutedEventArgs e)
    {
        ModeToggle.Content = "🎤 АКТИВНЫЙ";
        _voice.OpenMic = true;
        ModeHintText.Text = "микрофон активен — просто говори";
        if (!_voice.MicMuted)
            StatusText.Text = "микрофон активен — просто говори";
    }

    private void ModeToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        ModeToggle.Content = "⌨️ PTT";
        _voice.OpenMic = false;
        ModeHintText.Text = "PTT — зажми пробел";
        if (!_voice.MicMuted)
            StatusText.Text = "PTT — зажми пробел";
    }

    // ---------- Кнопки ----------

    private async void AddChannelBtn_Click(object sender, RoutedEventArgs e) => await CreateChannelAsync();

    private void LeaveChannelBtn_Click(object sender, RoutedEventArgs e) => LeaveChannel();

    private void MicMuteBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = true;
        MicMuteBtn.Content = "🎙️🚫";
    }

    private void MicMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = false;
        MicMuteBtn.Content = "🎙️";
    }

    private void HeadMuteBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = true;
        HeadMuteBtn.Content = "🔇";
    }

    private void HeadMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = false;
        HeadMuteBtn.Content = "🔊";
    }

    private void DeafenBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = true;
        _voice.PlaybackMuted = true;
        MicMuteBtn.IsChecked = true;
        HeadMuteBtn.IsChecked = true;
        DeafenBtn.Content = "🔇✅";
    }

    private void DeafenBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = false;
        _voice.PlaybackMuted = false;
        MicMuteBtn.IsChecked = false;
        HeadMuteBtn.IsChecked = false;
        DeafenBtn.Content = "🔇";
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

    // ---------- Утилиты ----------

    internal static SolidColorBrush BrushFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16)));
    }

    private static Windows.UI.Color ColorFromArgb(byte r, byte g, byte b)
        => Windows.UI.Color.FromArgb(255, r, g, b);
}
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed class ChatMessage
{
    public string SenderName { get; set; } = "";
    public string SenderColor { get; set; } = "#5865f2";
    public string Letter => SenderName.Length > 0 ? SenderName[..1].ToUpperInvariant() : "?";
    public SolidColorBrush ColorBrush => MainWindow.BrushFromHex(SenderColor);
    public string TimeText { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed partial class MainWindow : Window
{
    private readonly ApiClient _api;
    private readonly AppSettings _settings;
    private readonly VoiceClient _voice = new();
    private readonly WebRTCVoiceClient? _webrtc;
    private readonly ObservableCollection<MemberItem> _members = [];
    private readonly ObservableCollection<ChatMessage> _channelMessages = [];
    private readonly ObservableCollection<ChatMessage> _dmMessages = [];
    private readonly UserInfo _user;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chatTimer;
    private List<ChannelInfo> _channels = [];
    private ChannelInfo? _currentChannel;
    private UserInfo? _currentDmFriend;
    private int _lastChannelMsgId;
    private int _lastDmMsgId;
    private bool _useWebRtc = true;
    private string _lastWebRtcError = "";

    public MainWindow(ApiClient api, AppSettings settings, UserInfo user)
    {
        _api = api;
        _settings = settings;
        _user = user;

        // Try WebRTC first, fallback to UDP
        try
        {
            var host = _settings.Server.Split(':')[0];
            _webrtc = new WebRTCVoiceClient(api, host, _settings);
            _webrtc.AgcEnabled = _settings.AgcEnabled;
            _webrtc.StatusChanged += (msg) => DispatcherQueue.TryEnqueue(() => StatusText.Text = msg);
            _webrtc.MembersChanged += (names) => DispatcherQueue.TryEnqueue(() =>
            {
                var oldNames = _members.Select(m => m.Name).ToList();
                var newNames = names.ToList();
                if (oldNames.SequenceEqual(newNames)) return;
                _members.Clear();
                foreach (var n in names) _members.Add(new MemberItem(n));
            });
        }
        catch { _useWebRtc = false; }

        InitializeComponent();
        HubView.Init(_members, _user.Name, _voice, _webrtc, _settings, LeaveChannel);
        HubView.HomeRequested += () => ShowUi(false);
        HubView.SettingsRequested += () => ShowMode("settings");
        SettingsView.Init(_settings, _voice, _webrtc);
        SettingsView.CloseRequested += () => ShowUi(true);
        HomeView.Init(_api, _user, () => _currentChannel, _settings);
        HomeView.JoinRequested += async ch => { await JoinChannelAsync(ch); ShowUi(true); };
        HomeView.Unauthorized += () => DispatcherQueue.TryEnqueue(ShowAuthAndClose);
        HomeView.HubRequested += () => ShowUi(true);
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
        ChannelChatList.ItemsSource = _channelMessages;
        DmChatList.ItemsSource = _dmMessages;
        _voice.MembersChanged += OnMembersChanged;
        _voice.TalkingChanged += OnTalkingChanged;
        _voice.StatusChanged += OnStatusChanged;
        _voice.SpeakerStarted += OnSpeakerStarted;
        _voice.SpeakerStopped += OnSpeakerStopped;
        if (_webrtc != null)
        {
            _webrtc.TalkingChanged += OnTalkingChanged;
        }

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += async (_, _) =>
        {
            await RefreshChannelsAsync();
            await RefreshFriendsAsync();
        };
        _refreshTimer.Start();

        _chatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _chatTimer.Tick += async (_, _) => await RefreshChatAsync();

        _shareStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _shareStatusTimer.Tick += (_, _) =>
        {
            var activeSharers = ScreenShareReceiver.GetActiveSharers();
            foreach (var m in _members)
            {
                if (m.Name == _user.Name) continue;
                m.IsScreenSharing = activeSharers.Contains(m.Name);
                if (m.IsScreenSharing)
                    m.RefreshShareTime();
            }
        };
        _shareStatusTimer.Start();

        _screenPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _screenPollTimer.Tick += async (_, _) => await PollScreenSharersAsync();
        _screenPollTimer.Start();

        Closed += OnWindowClosed;
        _voice.OpenMic = true;
        _ = RefreshChannelsAsync();
        _ = RefreshFriendsAsync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _refreshTimer.Stop();
        _chatTimer.Stop();
        _shareStatusTimer.Stop();
        _screenPollTimer.Stop();
        _activeScreenShare?.StopCapture();
        _activeScreenShare?.Dispose();
        _screenShareViewer?.Close();
        HubView.Shutdown();
        HomeView.Shutdown();
        SettingsView.Shutdown();
        foreach (var v in _memberViewers.Values) v.Close();
        _memberViewers.Clear();
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
                Background = BrushFromHex(_currentChannel?.Id == ch.Id ? "#0d3a54" : "#00000000"),
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

        ShowConnectingOverlay(ch.Name, 0);
        void OverlayHandler(string msg) => DispatcherQueue.TryEnqueue(() => UpdateConnectingOverlay(msg));
        if (_webrtc != null) _webrtc.StatusChanged += OverlayHandler;

        LeaveChannel();
        bool connected = false;

        // Try WebRTC first (with 30s timeout)
        if (_useWebRtc && _webrtc != null)
        {
            try
            {
                UpdateConnectingOverlay("подключение к WebRTC...");
                var connectTask = _webrtc.ConnectAsync(ch.Id);
                var completed = await Task.WhenAny(connectTask, Task.Delay(30000));
                if (completed == connectTask && !connectTask.IsFaulted)
                {
                    await connectTask;
                    connected = true;
                    UpdateConnectingOverlay("WebRTC подключен");
                }
                else
                {
                    string errorDetail = connectTask.IsFaulted
                        ? connectTask.Exception?.GetBaseException().Message ?? "unknown"
                        : "таймаут ICE (30с)";
                    WebRTCVoiceClient.Log($"WebRTC error: {errorDetail}, falling back to UDP");
                    _webrtc.Disconnect();
                }
            }
            catch (Exception ex)
            {
                WebRTCVoiceClient.Log($"WebRTC failed: {ex.Message}, falling back to UDP");
                _webrtc.Disconnect();
            }
        }

        // Fallback to UDP (VoiceClient with full DSP pipeline)
        if (!connected && _voice is not null)
        {
            try
            {
                UpdateConnectingOverlay("WebRTC недоступен, подключаю UDP...");
                var serverHost = _settings.Server.Split(':')[0];
                var serverPort = _settings.VoicePort;
                _voice.Connect(serverHost, serverPort, ch.Id.ToString(), _user.Name, ch.HasPassword ? "" : "", _settings);
                connected = true;
                UpdateConnectingOverlay("UDP подключен (DSP активен)");
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"UDP failed: {ex.Message}";
                UpdateConnectingOverlay($"ошибка: {ex.Message}");
                await ShowErrorAsync($"Не удалось подключиться:\n{ex.Message}\n\nЛог: %TEMP%\\voxcore-client.log");
                HideConnectingOverlay();
                return;
            }
        }

        if (!connected)
        {
            HideConnectingOverlay();
            await ShowErrorAsync("Не удалось подключиться к голосовому каналу");
            return;
        }

        if (_webrtc != null) _webrtc.StatusChanged -= OverlayHandler;
        HideConnectingOverlay();

        _currentChannel = ch;
        ChannelNameText.Text = ch.Name;
        ChannelStatusText.Text = connected ? (_useWebRtc && _webrtc?.IsConnected == true ? "WebRTC подключен" : "UDP подключен (DSP)") : "не подключен";
        LeaveChannelBtn.Visibility = Visibility.Visible;
        VoiceChannelName.Text = ch.Name;
        VoiceServerName.Text = "VoxCore";
        VoiceConnectedPanel.Visibility = Visibility.Visible;
        VoiceStatusPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = connected ? $"WebRTC: {ch.Name}" : "ошибка";
        ChannelChatPanel.Visibility = Visibility.Visible;
        _lastChannelMsgId = 0;
        _channelMessages.Clear();
        CloseDmPanel();
        _chatTimer.Start();
        _ = LoadChannelChatAsync();
        RenderChannels();
    }

    private void LeaveChannel()
    {
        _voice.Disconnect();
        _webrtc?.Disconnect();
        _activeScreenShare?.StopCapture();
        _activeScreenShare?.Dispose();
        _activeScreenShare = null;
        _currentChannel = null;
        ChannelNameText.Text = "не в канале";
        ChannelStatusText.Text = "выбери канал слева";
        LeaveChannelBtn.Visibility = Visibility.Collapsed;
        VoiceConnectedPanel.Visibility = Visibility.Collapsed;
        VoiceStatusPanel.Visibility = Visibility.Visible;
        ScreenShareBtn.IsChecked = false;
        ScreenShareDot.Visibility = Visibility.Collapsed;
        StatusText.Text = "отключено";
        ChannelChatPanel.Visibility = Visibility.Collapsed;
        _chatTimer.Stop();
        _channelMessages.Clear();
        _lastChannelMsgId = 0;
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
        switch (item.State)
        {
            case "Friend":
                SearchInfo.Text = $"{item.Name} уже у тебя в друзьях";
                return;
            case "Requested":
                SearchInfo.Text = $"запрос {item.Name} уже отправлен, ждёт принятия";
                return;
            case "Incoming":
                SearchInfo.Text = $"{item.Name} уже отправил тебе запрос — прими его во вкладке Друзья";
                return;
        }
        SearchInfo.Text = "отправляю запрос...";
        try
        {
            await _api.AddFriendAsync(item.Name);
            SearchInfo.Text = $"запрос отправлен {item.Name}";
            SearchResultsList.Visibility = Visibility.Collapsed;
            _ = RefreshFriendsAsync();
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
        ChannelsTabBtn.Background = MainWindow.BrushFromHex("#0d3a54");
        ChannelsTabBtn.Foreground = MainWindow.BrushFromHex("#7fe3ff");
        FriendsTabBtn.Background = MainWindow.BrushFromHex("#0d0f1e");
        FriendsTabBtn.Foreground = MainWindow.BrushFromHex("#9fefff");
        FriendsPanel.Visibility = Visibility.Collapsed;
        ChannelsHeader.Visibility = Visibility.Visible;
        ChannelsScroll.Visibility = Visibility.Visible;
        CloseDmPanel();
    }

    private void FriendsTabBtn_Click(object sender, RoutedEventArgs e)
    {
        FriendsTabBtn.Background = MainWindow.BrushFromHex("#0d3a54");
        FriendsTabBtn.Foreground = MainWindow.BrushFromHex("#7fe3ff");
        ChannelsTabBtn.Background = MainWindow.BrushFromHex("#0d0f1e");
        ChannelsTabBtn.Foreground = MainWindow.BrushFromHex("#9fefff");
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
            foreach (var m in _members)
            {
                if (m.Name == _user.Name)
                    m.IsSpeaking = talking;
            }
            if (talking)
            {
                StatusText.Text = "говоришь...";
                StatusText.Foreground = BrushFromHex("#3ba55d");
            }
            else
            {
                StatusText.Text = "микрофон активен — просто говори";
                StatusText.Foreground = BrushFromHex("#b5bac1");
            }
        });
    }

    private void OnStatusChanged(string status)
    {
        DispatcherQueue.TryEnqueue(() => StatusText.Text = status);
    }

    // ---------- Кнопки ----------

    private async void AddChannelBtn_Click(object sender, RoutedEventArgs e) => await CreateChannelAsync();

    private void LeaveChannelBtn_Click(object sender, RoutedEventArgs e) => LeaveChannel();

    private void MicMuteBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = true;
        _webrtc.MicMuted = true;
        MicMuteBtn.Content = "🎙️🚫";
    }

    private void MicMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = false;
        _webrtc.MicMuted = false;
        MicMuteBtn.Content = "🎙️";
    }

    private void HeadMuteBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = true;
        _webrtc.PlaybackMuted = true;
        HeadMuteBtn.Content = "🔇";
    }

    private void HeadMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = false;
        _webrtc.PlaybackMuted = false;
        HeadMuteBtn.Content = "🔊";
    }

    private ScreenShareService? _activeScreenShare;
    private ScreenShareViewerWindow? _screenShareViewer;
    private bool _hubVisible = true;

    private void AudioHubBtn_Click(object sender, RoutedEventArgs e) => ShowUi(true);

    private void ToggleUiBtn_Click(object sender, RoutedEventArgs e) => ShowUi(!_hubVisible);

    private void ShowUi(bool hub) => ShowMode(hub ? "hub" : "home");

    private void ShowMode(string mode)
    {
        _hubVisible = mode == "hub";
        HubView.Visibility = _hubVisible ? Visibility.Visible : Visibility.Collapsed;
        HubView.SetActive(_hubVisible);
        HomeView.Visibility = mode == "home" ? Visibility.Visible : Visibility.Collapsed;
        HomeView.SetActive(mode == "home");
        SettingsView.Visibility = mode == "settings" ? Visibility.Visible : Visibility.Collapsed;
        OldUi.Visibility = Visibility.Collapsed;
        TitleBarArea.Background = BrushFromHex("#070810");
        var tb = AppWindow.TitleBar;
        var bg = ColorFromArgb(7, 8, 16);
        var fg = ColorFromArgb(159, 239, 255);
        tb.BackgroundColor = bg;
        tb.ForegroundColor = fg;
        tb.ButtonBackgroundColor = bg;
        tb.ButtonForegroundColor = fg;
        tb.ButtonHoverBackgroundColor = ColorFromArgb(20, 23, 42);
        tb.ButtonHoverForegroundColor = ColorFromArgb(255, 255, 255);
        tb.InactiveBackgroundColor = bg;
        tb.InactiveForegroundColor = fg;
        tb.ButtonInactiveBackgroundColor = bg;
    }

    private async void ScreenShareBtn_Checked(object sender, RoutedEventArgs e)
    {
        ScreenShareStatusText.Text = "открываю пикер...";

        if (_currentChannel == null)
        {
            ScreenShareStatusText.Text = "не в канале";
            ScreenShareBtn.IsChecked = false;
            return;
        }

        try
        {
            ScreenSharePickerResult.Reset();
            var picker = new ScreenSharePickerWindow();
            picker.Activate();

            ScreenShareStatusText.Text = "ждём выбор в пикере...";
            bool confirmed = await ScreenSharePickerWindow.PickerTcs!.Task;

            if (!confirmed)
            {
                ScreenShareStatusText.Text = "";
                ScreenShareBtn.IsChecked = false;
                return;
            }

            var roomId = _webrtc?.RoomId ?? _currentChannel.Id.ToString();
            _activeScreenShare = new ScreenShareService(_api, roomId);
            _activeScreenShare.StatusChanged += (msg) => DispatcherQueue.TryEnqueue(() => ScreenShareStatusText.Text = msg);

            if (ScreenSharePickerResult.DisplayIndex >= 0)
                _activeScreenShare.SetTargetDisplay(ScreenSharePickerResult.DisplayIndex);
            else if (ScreenSharePickerResult.WindowHandle != IntPtr.Zero)
                _activeScreenShare.SetTargetWindow(ScreenSharePickerResult.WindowHandle);

            ScreenShareStatusText.Text = "запускаю захват...";
            _activeScreenShare.FrameSent += (frame) =>
            {
                ScreenShareReceiver.UpdateFrame(_user.Name, frame);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_screenShareViewer == null)
                    {
                        _screenShareViewer = new ScreenShareViewerWindow(_user.Name);
                        _screenShareViewer.Closed += (_, _) => _screenShareViewer = null;
                        _screenShareViewer.Activate();
                    }
                });
            };
            _activeScreenShare.StartCapture();
            ScreenShareDot.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ScreenShareStatusText.Text = $"ОШИБКА: {ex.Message}";
            ScreenShareBtn.IsChecked = false;
        }
    }

    private void ScreenShareBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _activeScreenShare?.StopCapture();
        _activeScreenShare?.Dispose();
        _activeScreenShare = null;
        _screenShareViewer?.Close();
        _screenShareViewer = null;
        _ = _api.ScreenShareStopAsync();
        ScreenShareReceiver.Remove(_user.Name);
        ScreenShareDot.Visibility = Visibility.Collapsed;
        ScreenShareStatusText.Text = "";
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => ShowMode("settings");

    private readonly Dictionary<string, ScreenShareViewerWindow> _memberViewers = new();
    private readonly DispatcherTimer _shareStatusTimer;
    private readonly DispatcherTimer _screenPollTimer;

    private void MembersList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MemberItem member) return;
        if (!member.IsScreenSharing)
        {
            ScreenShareStatusText.Text = $"{member.Name} не демонстрирует экран";
            return;
        }
        if (_memberViewers.TryGetValue(member.Name, out var existing) && existing != null)
        {
            existing.Activate();
            return;
        }
        var viewer = new ScreenShareViewerWindow(member.Name);
        _memberViewers[member.Name] = viewer;
        viewer.Closed += (_, _) => _memberViewers.Remove(member.Name);
        viewer.Activate();
    }

    private async Task PollScreenSharersAsync()
    {
        try
        {
            var sharers = await _api.ScreenListAsync();
            foreach (var m in _members)
            {
                if (m.Name == _user.Name) continue;
                m.IsScreenSharing = sharers.Contains(m.Name);
            }
            foreach (var name in sharers)
            {
                if (name == _user.Name) continue;
                var frame = await _api.ScreenGetFrameAsync(name);
                if (frame != null && frame.Length > 0)
                    ScreenShareReceiver.UpdateFrame(name, frame);
            }
        }
        catch { }
    }

    // ---------- Чат в канале ----------

    private async Task LoadChannelChatAsync()
    {
        if (_currentChannel is null) return;
        try
        {
            var msgs = await _api.GetChannelMessagesAsync(_currentChannel.Id, 100);
            foreach (var m in msgs.Where(m => m.Id > _lastChannelMsgId))
            {
                _channelMessages.Add(new ChatMessage
                {
                    SenderName = m.SenderName,
                    SenderColor = m.SenderColor,
                    TimeText = m.SentAt.ToLocalTime().ToString("HH:mm"),
                    Text = m.Text
                });
                _lastChannelMsgId = m.Id;
            }
            if (_channelMessages.Count > 0)
                ChannelChatList.ScrollIntoView(_channelMessages[^1]);
        }
        catch { }
    }

    private async Task RefreshChatAsync()
    {
        if (_currentChannel is null && _currentDmFriend is null) return;
        if (_currentChannel is not null) await LoadChannelChatAsync();
        if (_currentDmFriend is not null) await LoadDmChatAsync();
    }

    private async Task SendChannelChatAsync()
    {
        if (_currentChannel is null) return;
        var text = ChannelChatInput.Text.Trim();
        if (text.Length == 0) return;
        ChannelChatInput.Text = "";
        try
        {
            await _api.SendChannelMessageAsync(_currentChannel.Id, text);
            await LoadChannelChatAsync();
        }
        catch (ApiException ex)
        {
            if (ex.Message == "unauthorized") { DispatcherQueue.TryEnqueue(ShowAuthAndClose); return; }
            ChannelChatInput.Text = text;
        }
        catch
        {
            ChannelChatInput.Text = text;
        }
    }

    private async void ChannelChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            await SendChannelChatAsync();
    }

    private async void ChannelChatSend_Click(object sender, RoutedEventArgs e) => await SendChannelChatAsync();

    // ---------- Личные сообщения ----------

    private void OpenDmPanel(UserInfo friend)
    {
        _currentDmFriend = friend;
        _lastDmMsgId = 0;
        _dmMessages.Clear();
        DmFriendAvatar.Background = BrushFromHex(friend.Color);
        DmFriendLetter.Text = friend.Name.Length > 0 ? friend.Name[..1].ToUpperInvariant() : "?";
        DmFriendName.Text = friend.Name;
        ChannelChatPanel.Visibility = Visibility.Collapsed;
        VoiceStatusPanel.Visibility = Visibility.Collapsed;
        DmPanel.Visibility = Visibility.Visible;
        _chatTimer.Stop();
        _chatTimer.Start();
        _ = LoadDmChatAsync();
    }

    private void CloseDmPanel()
    {
        _currentDmFriend = null;
        _dmMessages.Clear();
        _lastDmMsgId = 0;
        DmPanel.Visibility = Visibility.Collapsed;
        if (_currentChannel is not null)
        {
            ChannelChatPanel.Visibility = Visibility.Visible;
            VoiceStatusPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            VoiceStatusPanel.Visibility = Visibility.Visible;
            ChannelChatPanel.Visibility = Visibility.Collapsed;
        }
        if (_currentChannel is null && _currentDmFriend is null)
            _chatTimer.Stop();
    }

    private void DmBack_Click(object sender, RoutedEventArgs e)
    {
        CloseDmPanel();
        _chatTimer.Stop();
    }

    private async Task LoadDmChatAsync()
    {
        if (_currentDmFriend is null) return;
        try
        {
            var msgs = await _api.GetMessagesAsync(_currentDmFriend.Id, 100);
            foreach (var m in msgs.Where(m => m.Id > _lastDmMsgId))
            {
                var isMe = m.FromUserId == _user.Id;
                _dmMessages.Add(new ChatMessage
                {
                    SenderName = isMe ? _user.Name : _currentDmFriend.Name,
                    SenderColor = isMe ? _user.Color : _currentDmFriend.Color,
                    TimeText = m.SentAt.ToLocalTime().ToString("HH:mm"),
                    Text = m.Text
                });
                _lastDmMsgId = m.Id;
            }
            if (_dmMessages.Count > 0)
                DmChatList.ScrollIntoView(_dmMessages[^1]);
            await _api.MarkAsReadAsync(_currentDmFriend.Id);
        }
        catch { }
    }

    private async Task SendDmAsync()
    {
        if (_currentDmFriend is null) return;
        var text = DmChatInput.Text.Trim();
        if (text.Length == 0) return;
        DmChatInput.Text = "";
        try
        {
            await _api.SendMessageAsync(_currentDmFriend.Id, text);
            await LoadDmChatAsync();
        }
        catch (ApiException ex)
        {
            if (ex.Message == "unauthorized") { DispatcherQueue.TryEnqueue(ShowAuthAndClose); return; }
            DmChatInput.Text = text;
        }
        catch
        {
            DmChatInput.Text = text;
        }
    }

    private async void DmChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            await SendDmAsync();
    }

    private async void DmChatSend_Click(object sender, RoutedEventArgs e) => await SendDmAsync();

    private void FriendDm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        var friends = FriendsList.ItemsSource as List<FriendItem>;
        var friend = friends?.FirstOrDefault(f => f.Id == id);
        if (friend is null) return;
        OpenDmPanel(new UserInfo { Id = friend.Id, Name = friend.Name, Color = friend.HexColor });
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

    // ---------- Connecting overlay ----------
    private void ShowConnectingOverlay(string channel, int step)
    {
        ConnectingTitle.Text = $"Подключение к {channel}";
        ConnectingStep.Text = "Подготовка...";
        Step1.Text = "● Подготовка"; Step1.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 234, 139));
        Step2.Text = "○ Сбор ICE кандидатов"; Step2.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 97, 122));
        Step3.Text = "○ Обмен SDP"; Step3.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 97, 122));
        Step4.Text = "○ Установка соединения"; Step4.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 97, 122));
        ConnectingOverlay.Visibility = Visibility.Visible;
    }

    private void HideConnectingOverlay() => ConnectingOverlay.Visibility = Visibility.Collapsed;

    private void UpdateConnectingOverlay(string msg)
    {
        ConnectingStep.Text = msg;
        var green = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 234, 139));
        var gray = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 97, 122));
        if (msg.Contains("подключение к WebRTC") || msg.Contains("Подготовка"))
        {
            Step1.Text = "● Подготовка"; Step1.Foreground = green;
        }
        else if (msg.Contains("ICE") || msg.Contains("кандидатов"))
        {
            Step1.Text = "✓ Подготовка"; Step1.Foreground = green;
            Step2.Text = "● Сбор ICE кандидатов"; Step2.Foreground = green;
        }
        else if (msg.Contains("offer") || msg.Contains("answer") || msg.Contains("SDP"))
        {
            Step2.Text = "✓ Сбор ICE кандидатов"; Step2.Foreground = green;
            Step3.Text = "● Обмен SDP"; Step3.Foreground = green;
        }
        else if (msg.Contains("WebRTC:") || msg.Contains("Установка") || msg.Contains("setRemote"))
        {
            Step3.Text = "✓ Обмен SDP"; Step3.Foreground = green;
            Step4.Text = "● Установка соединения"; Step4.Foreground = green;
        }
        else if (msg.Contains("подключен"))
        {
            Step4.Text = "✓ Установка соединения"; Step4.Foreground = green;
        }
        else if (msg.Contains("UDP"))
        {
            Step1.Text = "✓ Подготовка"; Step1.Foreground = green;
            Step2.Text = "○ Сбор ICE кандидатов"; Step2.Foreground = gray;
            Step3.Text = "○ Обмен SDP"; Step3.Foreground = gray;
            Step4.Text = "● Подключение по UDP"; Step4.Foreground = green;
        }
    }
}
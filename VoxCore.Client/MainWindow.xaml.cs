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
            _webrtc.MembersChanged += (names) => DispatcherQueue.TryEnqueue(() => { _members.Clear(); foreach (var n in names) _members.Add(new MemberItem(n)); });
        }
        catch { _useWebRtc = false; }

        InitializeComponent();
        BootView.BootCompleted += (_, _) =>
        {
            BootView.Visibility = Visibility.Collapsed;
        };
        HubView.Init(_members, _user.Name, _voice, _webrtc, _settings, LeaveChannel);
        HubView.HomeRequested += () => ShowUi(false);
        HubView.SettingsRequested += () => ShowMode("settings");
        SettingsView.Init(_settings, _voice, _webrtc);
        SettingsView.CloseRequested += () => ShowUi(true);
        HomeView.Init(_api, _user, () => _currentChannel, _settings);
        HomeView.JoinRequested += async ch => { await JoinChannelAsync(ch); ShowUi(true); };
        HomeView.Unauthorized += () => DispatcherQueue.TryEnqueue(ShowAuthAndClose);
        HomeView.HubRequested += () => ShowUi(true);

        // New layout components
        PilotProfile.SetUser(_user.Name, _user.Color);
        CrewManifest.BindMembers(_members);
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

        ChannelChatList.ItemsSource = _channelMessages;
        DmChatList.ItemsSource = _dmMessages;
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
        SectorNav.SetChannels(_channels);
        ConstellationBar.SetServers(_channels);
    }

    private async Task CreateChannelAsync()
    {
        var nameBox = new TextBox { PlaceholderText = "название сектора" };
        var passBox = new PasswordBox { PlaceholderText = "ключ доступа (необязательно)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(nameBox);
        panel.Children.Add(passBox);
        var dialog = new ContentDialog
        {
            Title = "Создать сектор",
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
            Title = $"Удалить сектор «{ch.Name}»?",
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
            var passBox = new PasswordBox { PlaceholderText = "ключ доступа сектора" };
            var dialog = new ContentDialog
            {
                Title = $"Войти в «{ch.Name}»",
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

        // Try WebRTC first (with 15s timeout)
        if (_useWebRtc && _webrtc != null)
        {
            try
            {
                UpdateConnectingOverlay("установка квантовой связи...");
                var connectTask = _webrtc.ConnectAsync(ch.Id);
                var completed = await Task.WhenAny(connectTask, Task.Delay(15000));
                if (completed == connectTask && !connectTask.IsFaulted)
                {
                    await connectTask;
                    connected = true;
                    UpdateConnectingOverlay("квантовая связь установлена");
                }
                else
                {
                    StatusText.Text = "WebRTC timeout, fallback to UDP";
                    WebRTCVoiceClient.Log("timeout 15s, fallback to UDP");
                    UpdateConnectingOverlay("WebRTC timeout → UDP");
                    _webrtc.Disconnect();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"WebRTC failed: {ex.Message}, fallback to UDP";
                WebRTCVoiceClient.Log($"failed: {ex.Message}, fallback to UDP");
                UpdateConnectingOverlay($"ошибка связи: {ex.Message}");
                _webrtc.Disconnect();
            }
        }

        // Fallback to UDP if WebRTC didn't connect
        if (!connected)
        {
            try
            {
                var host = _settings.Server.Split(':')[0];
                UpdateConnectingOverlay("прямая квантовая связь...");
                _voice.Connect(host, 9987, ch.Id.ToString(), _user.Name, password);
                connected = true;
                UpdateConnectingOverlay("прямая связь установлена");
            }
            catch (Exception ex)
            {
                StatusText.Text = $"UDP failed: {ex.Message}";
                UpdateConnectingOverlay($"UDP failed: {ex.Message}");
                if (_webrtc != null) _webrtc.StatusChanged -= OverlayHandler;
                HideConnectingOverlay();
                await ShowErrorAsync("не удалось установить голографическую связь");
                return;
            }
        }

        if (_webrtc != null) _webrtc.StatusChanged -= OverlayHandler;
        HideConnectingOverlay();

        _currentChannel = ch;
        ChannelNameText.Text = ch.Name;
        ChannelStatusText.Text = connected ? (connected && _webrtc?.IsConnected == true ? "квантовая связь" : "прямая связь") : "нет связи";
        LeaveChannelBtn.Visibility = Visibility.Visible;
        VoiceChannelName.Text = ch.Name;
        VoiceServerName.Text = "VoxCore";
        VoiceConnectedPanel.Visibility = Visibility.Visible;
        VoiceStatusPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = connected ? $"{(_webrtc?.IsConnected == true ? "квантовая" : "прямая")} связь: {ch.Name}" : "ошибка";
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
        ChannelNameText.Text = "нет связи";
        ChannelStatusText.Text = "выбери сектор";
        LeaveChannelBtn.Visibility = Visibility.Collapsed;
        VoiceConnectedPanel.Visibility = Visibility.Collapsed;
        VoiceStatusPanel.Visibility = Visibility.Visible;
        ScreenShareBtn.IsChecked = false;
        ScreenShareDot.Visibility = Visibility.Collapsed;
        StatusText.Text = "разорвано";
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

    private async Task SearchPilotsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        try
        {
            var results = await _api.SearchUsersAsync(query);
            SectorNav.SetAllies(results.Select(FriendItem.FromUser).ToList());
        }
        catch
        {
            // поиск недоступен
        }
    }

    private async Task RefreshFriendsAsync()
    {
        try
        {
            var friends = await _api.GetFriendsAsync();
            var allyItems = friends.Select(FriendItem.FromUser).ToList();
            SectorNav.SetAllies(allyItems);

            var requests = await _api.GetFriendRequestsAsync();
            var reqItems = requests.Select(FriendItem.FromUser).ToList();
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
        // Search is now handled by SectorNavigation
        await Task.CompletedTask;
    }

    private async void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FriendItem item) return;
        try
        {
            await _api.AddFriendAsync(item.Name);
            _ = RefreshFriendsAsync();
        }
        catch
        {
            // search handled by SectorNav
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
        // Handled by SectorNavigation
    }

    private void FriendsTabBtn_Click(object sender, RoutedEventArgs e)
    {
        // Handled by SectorNavigation
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
    }

    private void MicMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.MicMuted = false;
        _webrtc.MicMuted = false;
    }

    private void HeadMuteBtn_Checked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = true;
        _webrtc.PlaybackMuted = true;
    }

    private void HeadMuteBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        _voice.PlaybackMuted = false;
        _webrtc.PlaybackMuted = false;
    }

    // ---------- New layout handlers ----------
    private void ConstellationBar_SettingsRequested(object sender, EventArgs e) => ShowMode("settings");
    private void ConstellationBar_AddServerRequested(object sender, EventArgs e) { _ = CreateChannelAsync(); }
    private void SectorNav_ChannelSelected(object sender, ChannelInfo ch) { _ = JoinChannelAsync(ch); ShowUi(true); }
    private void SectorNav_AddChannelRequested(object sender, EventArgs e) { _ = CreateChannelAsync(); }
    private void SectorNav_AlliesTabRequested(object sender, EventArgs e) { _ = RefreshFriendsAsync(); }
    private void SectorNav_PilotSearched(object sender, string query) { _ = SearchPilotsAsync(query); }
    private void PilotProfile_SettingsRequested(object sender, EventArgs e) => ShowMode("settings");
    private void PilotProfile_MicToggled(object sender, bool muted)
    {
        _voice.MicMuted = muted;
        _webrtc.MicMuted = muted;
    }
    private void PilotProfile_SpeakerToggled(object sender, bool muted)
    {
        _voice.PlaybackMuted = muted;
        _webrtc.PlaybackMuted = muted;
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
        // MembersList is now in CrewManifest
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

    private async void FriendDm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        try
        {
            var friends = await _api.GetFriendsAsync();
            var friend = friends.FirstOrDefault(f => f.Id == id);
            if (friend is not null)
                OpenDmPanel(friend);
        }
        catch
        {
            // ignore
        }
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
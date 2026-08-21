using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed partial class ChatWindow : Window
{
    private readonly ApiClient _api;
    private readonly UserInfo _friend;
    private readonly AppSettings _settings;
    private readonly List<DmChatMessage> _messages = [];

    public ChatWindow(ApiClient api, UserInfo friend, AppSettings settings)
    {
        _api = api;
        _friend = friend;
        _settings = settings;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 600));
        AppWindow.Title = $"VoxCore — чат с {friend.Name}";
        SetupDarkTitleBar();

        ChatTitle.Text = friend.Name;
        ChatSubtitle.Text = friend.Online ? "в сети" : "не в сети";

        _ = LoadMessagesAsync();
    }

    private void SetupDarkTitleBar()
    {
        var tb = AppWindow.TitleBar;
        Windows.UI.Color C(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
        tb.BackgroundColor = C(30, 31, 34);
        tb.ForegroundColor = C(148, 155, 164);
        tb.ButtonBackgroundColor = C(30, 31, 34);
        tb.ButtonForegroundColor = C(148, 155, 164);
        tb.ButtonHoverBackgroundColor = C(57, 60, 67);
        tb.ButtonHoverForegroundColor = C(255, 255, 255);
        tb.ButtonPressedBackgroundColor = C(35, 37, 43);
        tb.InactiveBackgroundColor = C(30, 31, 34);
        tb.InactiveForegroundColor = C(90, 94, 102);
        tb.ButtonInactiveBackgroundColor = C(30, 31, 34);
        tb.ButtonInactiveForegroundColor = C(90, 94, 102);
    }

    private async Task LoadMessagesAsync()
    {
        try
        {
            var msgs = await _api.GetMessagesAsync(_friend.Id);
            _messages.Clear();
            foreach (var m in msgs)
            {
                _messages.Add(new DmChatMessage
                {
                    SenderName = m.FromUserId == _settings.UserId ? _settings.UserName : _friend.Name,
                    Text = m.Text,
                    TimeText = m.SentAt.ToLocalTime().ToString("HH:mm"),
                    ColorBrush = MainWindow.BrushFromHex(m.FromUserId == _settings.UserId ? _settings.UserColor : _friend.Color),
                    IsOwn = m.FromUserId == _settings.UserId
                });
            }
            MessagesList.ItemsSource = _messages;
            if (_messages.Count > 0)
                MessagesList.ScrollIntoView(_messages[^1]);
        }
        catch { }
    }

    private async void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync();
    }

    private async void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private async Task SendMessageAsync()
    {
        var text = MessageInput.Text.Trim();
        if (text.Length == 0) return;
        MessageInput.Text = "";
        try
        {
            await _api.SendMessageAsync(_friend.Id, text);
            _messages.Add(new DmChatMessage
            {
                SenderName = _settings.UserName,
                Text = text,
                TimeText = DateTime.Now.ToString("HH:mm"),
                ColorBrush = MainWindow.BrushFromHex(_settings.UserColor),
                IsOwn = true
            });
            MessagesList.ItemsSource = null;
            MessagesList.ItemsSource = _messages;
            MessagesList.ScrollIntoView(_messages[^1]);
        }
        catch (ApiException ex)
        {
            if (ex.Message == "unauthorized")
            {
                DispatcherQueue.TryEnqueue(() => { Close(); });
                return;
            }
            ChatSubtitle.Text = ex.Message;
        }
        catch
        {
            ChatSubtitle.Text = "нет связи с сервером";
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class DmChatMessage
{
    public string SenderName { get; set; } = "";
    public string Text { get; set; } = "";
    public string TimeText { get; set; } = "";
    public Brush ColorBrush { get; set; } = MainWindow.BrushFromHex("#5865f2");
    public bool IsOwn { get; set; }
}

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace VoxCore.Client;

public sealed partial class HomeView : UserControl
{
    private static readonly Color ColWhite = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color ColCyan = Color.FromArgb(255, 0, 229, 255);
    private static readonly Color ColMagenta = Color.FromArgb(255, 255, 46, 196);
    private static readonly Color ColRed = Color.FromArgb(255, 255, 92, 120);
    private static readonly Color ColText = Color.FromArgb(255, 219, 230, 255);
    private static readonly Color ColMuted = Color.FromArgb(255, 90, 97, 122);
    private static readonly Color ColGreen = Color.FromArgb(255, 59, 165, 93);
    private static readonly FontFamily Cascadia = new("Cascadia Code");
    private static readonly string[] Palette = ["#5865f2", "#eb459e", "#faa61a", "#3ba55d", "#ed4245", "#9b59b6", "#00b0f4", "#f0b232"];

    private ApiClient? _api;
    private UserInfo? _user;
    private AppSettings? _settings;
    private Func<ChannelInfo?>? _currentChannelGetter;
    private readonly Random _rnd = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _chatTimer;
    private bool _inited;
    private bool _active;

    private List<ChannelInfo> _channels = [];
    private ChannelInfo? _chatChannel;
    private UserInfo? _chatFriend;
    private int _lastMsgId;

    public event System.Action<ChannelInfo>? JoinRequested;
    public event System.Action? Unauthorized;
    public event System.Action? HubRequested;

    public HomeView()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        _chatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _chatTimer.Tick += async (_, _) => await LoadChatAsync();
    }

    public void Init(ApiClient api, UserInfo user, Func<ChannelInfo?> currentChannelGetter, AppSettings settings)
    {
        if (_inited) return;
        _inited = true;
        _api = api;
        _user = user;
        _settings = settings;
        _currentChannelGetter = currentChannelGetter;
    }

    public void SetActive(bool active)
    {
        if (!_inited) return;
        _active = active;
        if (active)
        {
            _ = RefreshAllAsync();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
            _chatTimer.Stop();
            CloseChat();
        }
    }

    public void Shutdown()
    {
        _refreshTimer.Stop();
        _chatTimer.Stop();
    }

    // ---------- данные ----------

    private async Task RefreshAllAsync()
    {
        if (_api is null || _user is null || !_active) return;
        try
        {
            _channels = await _api.GetChannelsAsync();
            RenderChannels();
        }
        catch (ApiException ex) when (ex.Message == "unauthorized") { Unauthorized?.Invoke(); return; }
        catch { }
        try
        {
            RenderFriends(await _api.GetFriendsAsync());
        }
        catch (ApiException ex) when (ex.Message == "unauthorized") { Unauthorized?.Invoke(); }
        catch { }
        try
        {
            RenderRequests(await _api.GetFriendRequestsAsync());
        }
        catch { }
    }

    // ---------- каналы ----------

    private void RenderChannels()
    {
        var current = _currentChannelGetter?.Invoke();
        if (current is not null)
        {
            ChannelChatEntry.Visibility = Visibility.Visible;
            ChannelChatEntryText.Text = $"💬 ЧАТ КАНАЛА — {current.Name}";
        }
        else
        {
            ChannelChatEntry.Visibility = Visibility.Collapsed;
        }

        ChannelsList.Children.Clear();
        foreach (var ch in _channels)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(60, 13, 15, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 229, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Tag = ch
            };
            row.PointerEntered += (s, _) => ((Border)s!).BorderBrush = new SolidColorBrush(ColCyan);
            row.PointerExited += (s, _) => ((Border)s!).BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 229, 255));
            row.Tapped += ChannelRow_Tapped;

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = ch.HasPassword ? "🔒" : "🔊",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var name = new TextBlock
            {
                Text = ch.Name,
                FontFamily = Cascadia,
                FontSize = 13,
                Foreground = new SolidColorBrush(ColText),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            if (ch.Users > 0)
            {
                var users = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                users.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(ColGreen) });
                users.Children.Add(new TextBlock
                {
                    Text = ch.Users.ToString(),
                    FontFamily = Cascadia,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColCyan),
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(users, 2);
                grid.Children.Add(users);
            }

            if (_user is not null && ch.OwnerId == _user.Id)
            {
                var del = new Button
                {
                    Content = new TextBlock { Text = "🗑", FontSize = 12 },
                    Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontFamily = Cascadia
                };
                del.Click += async (_, _) => await DeleteChannelAsync(ch);
                Grid.SetColumn(del, 3);
                grid.Children.Add(del);
            }

            row.Child = grid;
            ChannelsList.Children.Add(row);
        }
    }

    private async void ChannelRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: ChannelInfo ch } || _api is null) return;
        if (_currentChannelGetter?.Invoke()?.Id == ch.Id)
        {
            OpenChannelChat(ch);
            return;
        }
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
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                await _api.VerifyChannelPasswordAsync(ch.Id, passBox.Password);
            }
            catch (ApiException ex)
            {
                await ShowInfoAsync(ex.Message);
                return;
            }
            catch
            {
                await ShowInfoAsync("нет связи с сервером");
                return;
            }
        }
        JoinRequested?.Invoke(ch);
    }

    private async Task DeleteChannelAsync(ChannelInfo ch)
    {
        if (_api is null) return;
        var dialog = new ContentDialog
        {
            Title = $"Удалить «{ch.Name}»?",
            Content = "действие необратимо",
            PrimaryButtonText = "УДАЛИТЬ",
            CloseButtonText = "ОТМЕНА",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _api.DeleteChannelAsync(ch.Id);
            await RefreshAllAsync();
        }
        catch (ApiException ex) { await ShowInfoAsync(ex.Message); }
        catch { await ShowInfoAsync("нет связи с сервером"); }
    }

    private async void AddChannelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
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
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        if (name.Length == 0) return;
        try
        {
            await _api.CreateChannelAsync(name, passBox.Password);
            await RefreshAllAsync();
        }
        catch (ApiException ex) { await ShowInfoAsync(ex.Message); }
        catch { await ShowInfoAsync("нет связи с сервером"); }
    }

    private void ChannelChatEntry_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var current = _currentChannelGetter?.Invoke();
        if (current is not null) OpenChannelChat(current);
    }

    // ---------- друзья ----------

    private Brush ColorFor(string name) =>
        MainWindow.BrushFromHex(Palette[Math.Abs(name.GetHashCode()) % Palette.Length]);

    private void RenderFriends(List<UserInfo> friends)
    {
        FriendsListPanel.Children.Clear();
        foreach (var f in friends)
        {
            if (_settings is not null && (_settings.Blocked.Contains(f.Name) || _settings.Ignored.Contains(f.Name))) continue;
            FriendsListPanel.Children.Add(BuildPersonRow(f.Name, f.Color, f.Online, isFriend: true, userId: f.Id));
        }
        if (FriendsListPanel.Children.Count == 0)
            FriendsListPanel.Children.Add(new TextBlock
            {
                Text = "пока никого — найди друзей через поиск",
                FontFamily = Cascadia,
                FontSize = 11,
                Foreground = new SolidColorBrush(ColMuted)
            });
    }

    private void RenderRequests(List<UserInfo> requests)
    {
        RequestsList.Children.Clear();
        RequestsPanel.Visibility = requests.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in requests)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock
            {
                Text = r.Name,
                FontFamily = Cascadia,
                FontSize = 12,
                Foreground = new SolidColorBrush(ColText),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);
            var ok = MiniButton("✓", ColGreen, async (_, _) =>
            {
                if (_api is null) return;
                try { await _api.AcceptFriendRequestAsync(r.Id); await RefreshAllAsync(); } catch { }
            });
            Grid.SetColumn(ok, 1);
            row.Children.Add(ok);
            var no = MiniButton("✕", ColRed, async (_, _) =>
            {
                if (_api is null) return;
                try { await _api.DeclineFriendRequestAsync(r.Id); await RefreshAllAsync(); } catch { }
            });
            Grid.SetColumn(no, 2);
            row.Children.Add(no);
            RequestsList.Children.Add(row);
        }
    }

    private Button MiniButton(string glyph, Color color, EventHandler<RoutedEventArgs> onClick)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 12, Foreground = new SolidColorBrush(color), FontFamily = Cascadia },
            Background = new SolidColorBrush(Color.FromArgb(70, 13, 15, 30)),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Width = 30,
            Height = 30,
            Padding = new Thickness(0)
        };
        b.Click += new RoutedEventHandler(onClick);
        return b;
    }

    private Border BuildPersonRow(string name, string colorHex, bool online, bool isFriend, int userId)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(60, 13, 15, 30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 46, 196)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7)
        };
        row.PointerEntered += (s, _) => ((Border)s!).BorderBrush = new SolidColorBrush(ColMagenta);
        row.PointerExited += (s, _) => ((Border)s!).BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 46, 196));
        row.RightTapped += (s, e) =>
        {
            if (s is Border b) ShowUserMenu(b, e.GetPosition(b), name, userId, isFriend);
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var av = new Grid { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        av.Children.Add(new Ellipse { Fill = MainWindow.BrushFromHex(colorHex) });
        av.Children.Add(new TextBlock
        {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
            FontFamily = Cascadia,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(ColWhite),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(av, 0);
        grid.Children.Add(av);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = name,
            FontFamily = Cascadia,
            FontSize = 13,
            Foreground = new SolidColorBrush(ColText)
        });
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        status.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(online ? ColGreen : ColMuted) });
        status.Children.Add(new TextBlock
        {
            Text = online ? "в сети" : "не в сети",
            FontFamily = Cascadia,
            FontSize = 10,
            Foreground = new SolidColorBrush(ColMuted)
        });
        info.Children.Add(status);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var dm = MiniButton("💬", ColCyan, (_, _) => OpenDm(new UserInfo { Id = userId, Name = name, Color = colorHex }));
        Grid.SetColumn(dm, 2);
        grid.Children.Add(dm);

        if (isFriend)
        {
            var del = MiniButton("✕", ColRed, async (_, _) =>
            {
                if (_api is null) return;
                try { await _api.RemoveFriendAsync(userId); await RefreshAllAsync(); } catch { }
            });
            Grid.SetColumn(del, 3);
            grid.Children.Add(del);
        }

        row.Child = grid;
        return row;
    }

    private async void SearchBtn_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await DoSearchAsync();
    }

    private async Task DoSearchAsync()
    {
        if (_api is null) return;
        var query = SearchBox.Text.Trim();
        SearchResults.Children.Clear();
        if (query.Length == 0)
        {
            SearchResults.Visibility = Visibility.Collapsed;
            return;
        }
        SearchResults.Visibility = Visibility.Visible;
        try
        {
            var users = await _api.SearchUsersAsync(query);
            var visible = users.Where(u => _settings is null ||
                    (!_settings.Blocked.Contains(u.Name) && !_settings.Ignored.Contains(u.Name))).ToList();
            if (visible.Count == 0)
            {
                SearchResults.Children.Add(new TextBlock
                {
                    Text = "никого не найдено",
                    FontFamily = Cascadia,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(ColMuted)
                });
                return;
            }
            foreach (var u in visible)
            {
                var row = BuildPersonRow(u.Name, u.Color, u.Online, isFriend: u.State == "Friend", userId: u.Id);
                if (u.State == "None")
                {
                    var grid = (Grid)row.Child!;
                    var add = MiniButton("＋", ColGreen, async (_, _) =>
                    {
                        if (_api is null) return;
                        try
                        {
                            await _api.AddFriendAsync(u.Name);
                            await RefreshAllAsync();
                        }
                        catch (ApiException ex) { await ShowInfoAsync(ex.Message); }
                        catch { await ShowInfoAsync("нет связи с сервером"); }
                    });
                    Grid.SetColumn(add, 3);
                    grid.Children.Add(add);
                }
                else if (u.State == "Requested" || u.State == "Incoming")
                {
                    var grid = (Grid)row.Child!;
                    var stateText = new TextBlock
                    {
                        Text = u.State == "Requested" ? "запрос отправлен" : "ждёт твоего ответа",
                        FontFamily = Cascadia,
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 250, 166, 26)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(stateText, 3);
                    grid.Children.Add(stateText);
                }
                SearchResults.Children.Add(row);
            }
        }
        catch (ApiException ex) when (ex.Message == "unauthorized") { Unauthorized?.Invoke(); }
        catch (ApiException ex) { await ShowInfoAsync(ex.Message); }
        catch { await ShowInfoAsync("нет связи с сервером"); }
    }

    // ---------- контекстное меню аккаунта ----------

    private SolidColorBrush MenuHover() => new(Color.FromArgb(255, 30, 36, 74));

    private MenuFlyoutItem MakeMenuItem(string text, System.Action? onClick, bool danger = false, string? subtitle = null)
    {
        var label = subtitle is null ? text : $"{text}   ({subtitle})";
        var item = new MenuFlyoutItem
        {
            Text = label,
            FontFamily = Cascadia,
            FontSize = 12,
            Foreground = new SolidColorBrush(danger ? ColRed : ColText),
            Padding = new Thickness(14, 8, 14, 8),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
        };
        item.PointerEntered += (_, _) => item.Background = MenuHover();
        item.PointerExited += (_, _) => item.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        if (onClick is not null) item.Click += (_, _) => onClick();
        return item;
    }

    private MenuFlyoutSubItem MakeSubItem(string text)
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = text,
            FontFamily = Cascadia,
            FontSize = 12,
            Foreground = new SolidColorBrush(ColText),
            Padding = new Thickness(14, 8, 14, 8),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
        };
        sub.PointerEntered += (_, _) => sub.Background = MenuHover();
        sub.PointerExited += (_, _) => sub.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        return sub;
    }

    private void ShowUserMenu(FrameworkElement anchor, Windows.Foundation.Point pos, string name, int userId, bool isFriend)
    {
        if (_settings is null) return;
        var menu = new MenuFlyout();

        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(252, 13, 15, 30))));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(255, 42, 47, 85))));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(10)));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 5, 0, 5)));
        menu.MenuFlyoutPresenterStyle = presenterStyle;

        menu.Items.Add(MakeMenuItem("Досье пилота", () => ShowProfile(name, userId)));
        if (isFriend) menu.Items.Add(MakeMenuItem("Начать голографическую связь", () => StartCall(name, userId)));
        menu.Items.Add(MakeMenuItem("Добавить заметку", () => AddNote(name), subtitle: "Видна только вам"));
        if (isFriend) menu.Items.Add(MakeMenuItem("Закрыть прямую передачу", () => CloseDmFor(name)));

        menu.Items.Add(new MenuFlyoutSeparator());

        var apps = MakeSubItem("Инструменты");
        apps.Items.Add(MakeMenuItem("Скопировать позывной", () => CopyText(name)));
        apps.Items.Add(MakeMenuItem("Скопировать ID", () => CopyText(userId.ToString())));
        menu.Items.Add(apps);

        var invite = MakeSubItem("Пригласить в звёздную систему");
        if (_channels.Count == 0)
            invite.Items.Add(MakeMenuItem("нет секторов", null));
        else
            foreach (var ch in _channels)
            {
                var copy = ch;
                invite.Items.Add(MakeMenuItem(copy.Name, () => InviteToChannel(name, userId, copy)));
            }
        menu.Items.Add(invite);

        if (!isFriend)
            menu.Items.Add(MakeMenuItem("Позвать в экипаж", () => AddFriendFromMenu(name)));

        menu.Items.Add(MakeMenuItem("Изолировать", () => IgnoreUser(name)));
        menu.Items.Add(MakeMenuItem("Заблокировать", () => BlockUser(name), danger: true));

        menu.ShowAt(anchor, pos);
    }

    private async void ShowProfile(string name, int userId)
    {
        if (_settings is null) return;
        var ignored = _settings.Ignored.Contains(name);
        var blocked = _settings.Blocked.Contains(name);

        var panel = new StackPanel { Spacing = 8, MinWidth = 300 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var av = new Grid { Width = 40, Height = 40 };
        av.Children.Add(new Ellipse { Fill = ColorFor(name) });
        av.Children.Add(new TextBlock
        {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?",
            FontFamily = Cascadia,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(ColWhite),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(av);
        var nameCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        nameCol.Children.Add(new TextBlock
        {
            Text = name,
            FontFamily = Cascadia,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(ColWhite)
        });
        nameCol.Children.Add(new TextBlock
        {
            Text = $"ID: {userId}",
            FontFamily = Cascadia,
            FontSize = 11,
            Foreground = new SolidColorBrush(ColMuted)
        });
        head.Children.Add(nameCol);
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock
        {
            Text = "аккаунт VoxCore",
            FontFamily = Cascadia,
            FontSize = 11,
            Foreground = new SolidColorBrush(ColMuted)
        });
        if (_settings.Notes.TryGetValue(name, out var note) && note.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = $"Заметка: {note}",
                FontFamily = Cascadia,
                FontSize = 12,
                Foreground = new SolidColorBrush(ColText),
                TextWrapping = TextWrapping.Wrap
            });

        var dialog = new ContentDialog
        {
            Title = "Профиль",
            Content = panel,
            CloseButtonText = "ЗАКРЫТЬ",
            XamlRoot = XamlRoot
        };
        if (ignored || blocked)
        {
            dialog.PrimaryButtonText = blocked ? "РАЗБЛОКИРОВАТЬ" : "УБРАТЬ ИГНОР";
            dialog.PrimaryButtonClick += (_, _) =>
            {
                _settings.Ignored.Remove(name);
                _settings.Blocked.Remove(name);
                _settings.Save();
                _ = RefreshAllAsync();
            };
        }
        await dialog.ShowAsync();
    }

    private async void StartCall(string name, int userId)
    {
        if (_channels.Count == 0)
        {
            await ShowInfoAsync("нет голосовых каналов — создай канал сначала");
            return;
        }
        ChannelInfo? picked = null;
        var panel = new StackPanel { Spacing = 6, MinWidth = 300 };
        ContentDialog? dialog = null;
        foreach (var ch in _channels)
        {
            var copy = ch;
            var btn = new Button
            {
                Content = new TextBlock { Text = $"🔊 {copy.Name}", FontFamily = Cascadia, FontSize = 12, Foreground = new SolidColorBrush(ColText) },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(60, 13, 15, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 229, 255)),
                BorderThickness = new Thickness(1)
            };
            btn.Click += (_, _) => { picked = copy; dialog?.Hide(); };
            panel.Children.Add(btn);
        }
        dialog = new ContentDialog
        {
            Title = $"Звонок — {name}",
            Content = new ScrollViewer { MaxHeight = 300, Content = panel },
            CloseButtonText = "ОТМЕНА",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
        if (picked is null) return;
        JoinRequested?.Invoke(picked);
        if (_api is not null && _user is not null)
        {
            try { await _api.SendMessageAsync(userId, $"{_user.Name} приглашает тебя к звонку в канале «{picked.Name}»!"); }
            catch (ApiException ex) when (ex.Message == "unauthorized") { Unauthorized?.Invoke(); }
            catch { }
        }
    }

    private async void AddNote(string name)
    {
        if (_settings is null) return;
        _settings.Notes.TryGetValue(name, out var existing);
        var box = new TextBox { Text = existing ?? "", PlaceholderText = "заметка (видна только вам)" };
        var dialog = new ContentDialog
        {
            Title = $"Заметка — {name}",
            Content = box,
            PrimaryButtonText = "СОХРАНИТЬ",
            CloseButtonText = "ОТМЕНА",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (box.Text.Trim().Length == 0) _settings.Notes.Remove(name);
        else _settings.Notes[name] = box.Text.Trim();
        _settings.Save();
    }

    private void CloseDmFor(string name)
    {
        if (_chatFriend is not null && _chatFriend.Name == name) CloseChat();
    }

    private void CopyText(string text)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
        };
        pkg.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }

    private async void InviteToChannel(string name, int userId, ChannelInfo ch)
    {
        if (_api is null || _user is null) return;
        try { await _api.SendMessageAsync(userId, $"{_user.Name} приглашает тебя на сервер: голосовой канал «{ch.Name}»!"); }
        catch (ApiException ex) when (ex.Message == "unauthorized") { Unauthorized?.Invoke(); }
        catch { }
    }

    private async void AddFriendFromMenu(string name)
    {
        if (_api is null) return;
        try
        {
            await _api.AddFriendAsync(name);
            await RefreshAllAsync();
        }
        catch (ApiException ex) { await ShowInfoAsync(ex.Message); }
        catch { await ShowInfoAsync("нет связи с сервером"); }
    }

    private void IgnoreUser(string name)
    {
        if (_settings is null) return;
        if (!_settings.Ignored.Contains(name)) _settings.Ignored.Add(name);
        _settings.Save();
        _ = RefreshAllAsync();
    }

    private async void BlockUser(string name)
    {
        if (_settings is null) return;
        var dialog = new ContentDialog
        {
            Title = $"Заблокировать {name}?",
            Content = "аккаунт исчезнет из списков, ЛС будет недоступно",
            PrimaryButtonText = "ЗАБЛОКИРОВАТЬ",
            CloseButtonText = "ОТМЕНА",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (!_settings.Blocked.Contains(name)) _settings.Blocked.Add(name);
        _settings.Ignored.Remove(name);
        _settings.Save();
        if (_chatFriend is not null && _chatFriend.Name == name) CloseChat();
        await RefreshAllAsync();
    }

    // ---------- чат ----------

    private void OpenDm(UserInfo friend)
    {
        if (_settings is not null && _settings.Blocked.Contains(friend.Name))
        {
            _ = ShowInfoAsync("пользователь заблокирован");
            return;
        }
        _chatChannel = null;
        _chatFriend = friend;
        StartChat($"личные сообщения — {friend.Name}", friend.Name[..1].ToUpperInvariant(),
            MainWindow.BrushFromHex(friend.Color), friend.Online ? "в сети" : "не в сети", showJoin: false);
    }

    private void OpenChannelChat(ChannelInfo ch)
    {
        _chatChannel = ch;
        _chatFriend = null;
        StartChat(ch.Name, "🔊", MainWindow.BrushFromHex("#5865f2"), "текстовый чат канала", showJoin: true);
    }

    private void StartChat(string title, string avatarLetter, Brush avatarColor, string subtitle, bool showJoin)
    {
        _lastMsgId = 0;
        ChatMessages.Children.Clear();
        ChatTitle.Text = title;
        ChatSubtitle.Text = subtitle;
        ChatAvatar.Background = avatarColor;
        ChatAvatarLetter.Text = avatarLetter;
        ChatJoinBtn.Visibility = showJoin ? Visibility.Visible : Visibility.Collapsed;
        ChatOverlay.Visibility = Visibility.Visible;
        _ = LoadChatAsync();
        if (_active) _chatTimer.Start();
    }

    private void CloseChat()
    {
        ChatOverlay.Visibility = Visibility.Collapsed;
        _chatChannel = null;
        _chatFriend = null;
        _chatTimer.Stop();
    }

    private void ChatBackBtn_Click(object sender, RoutedEventArgs e) => CloseChat();

    private void ChatJoinBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_chatChannel is not null) JoinRequested?.Invoke(_chatChannel);
    }

    private async Task LoadChatAsync()
    {
        if (_api is null || ChatOverlay.Visibility != Visibility.Visible) return;
        try
        {
            List<MessageInfo> msgs;
            if (_chatChannel is not null)
            {
                msgs = await _api.GetChannelMessagesAsync(_chatChannel.Id, 100);
            }
            else if (_chatFriend is not null)
            {
                msgs = await _api.GetMessagesAsync(_chatFriend.Id, 100);
                await _api.MarkAsReadAsync(_chatFriend.Id);
            }
            else return;

            foreach (var m in msgs.Where(m => m.Id > _lastMsgId))
            {
                bool own = _user is not null && m.SenderId == _user.Id;
                string senderName = _chatChannel is not null ? m.SenderName : (own ? _user!.Name : _chatFriend!.Name);
                string senderColor = _chatChannel is not null ? m.SenderColor : (own ? _user!.Color : _chatFriend!.Color);
                ChatMessages.Children.Add(BuildBubble(senderName, senderColor, m.Text, m.SentAt.ToLocalTime().ToString("HH:mm")));
                _lastMsgId = m.Id;
            }
            if (ChatMessages.Children.Count > 0)
            {
                ChatScroll.UpdateLayout();
                ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, 0);
            }
        }
        catch (ApiException ex) when (ex.Message == "unauthorized") { Unauthorized?.Invoke(); }
        catch { }
    }

    private Border BuildBubble(string senderName, string senderColor, string text, string time)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var av = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Top };
        av.Children.Add(new Ellipse { Fill = MainWindow.BrushFromHex(senderColor) });
        av.Children.Add(new TextBlock
        {
            Text = senderName.Length > 0 ? senderName[..1].ToUpperInvariant() : "?",
            FontFamily = Cascadia,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(ColWhite),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(av, 0);
        grid.Children.Add(av);

        var body = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Text = senderName,
            FontFamily = Cascadia,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = MainWindow.BrushFromHex(senderColor)
        });
        head.Children.Add(new TextBlock
        {
            Text = time,
            FontFamily = Cascadia,
            FontSize = 10,
            Foreground = new SolidColorBrush(ColMuted),
            VerticalAlignment = VerticalAlignment.Center
        });
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = Cascadia,
            FontSize = 13,
            Foreground = new SolidColorBrush(ColText),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 0)
        });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(8)
        };
    }

    private async void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await SendChatAsync();
    }

    private async void ChatSendBtn_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        if (_api is null) return;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        try
        {
            if (_chatChannel is not null)
                await _api.SendChannelMessageAsync(_chatChannel.Id, text);
            else if (_chatFriend is not null)
                await _api.SendMessageAsync(_chatFriend.Id, text);
            else return;
            ChatInput.Text = "";
            await LoadChatAsync();
        }
        catch (ApiException ex) when (ex.Message == "unauthorized") { Unauthorized?.Invoke(); }
        catch
        {
            ChatInput.Text = text;
        }
    }

    // ---------- прочее ----------

    private async Task ShowInfoAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Инфо",
            Content = message,
            CloseButtonText = "ОК",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void HomeTitleBtn_Click(object sender, RoutedEventArgs e) => HubRequested?.Invoke();

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => RegenerateStars();

    private void RegenerateStars()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w < 50 || h < 50) return;
        StarsCanvas.Children.Clear();
        int count = Math.Min(170, (int)(w * h / 8500));
        for (int i = 0; i < count; i++)
        {
            var c = _rnd.Next(100) switch
            {
                < 70 => Color.FromArgb(255, 235, 240, 255),
                < 85 => ColCyan,
                _ => ColMagenta
            };
            double size = 1 + _rnd.NextDouble() * 1.8;
            var s = new Ellipse
            {
                Width = size,
                Height = size,
                Opacity = 0.12 + _rnd.NextDouble() * 0.5,
                Fill = new SolidColorBrush(c)
            };
            Canvas.SetLeft(s, _rnd.NextDouble() * w);
            Canvas.SetTop(s, _rnd.NextDouble() * h);
            StarsCanvas.Children.Add(s);
        }
    }
}

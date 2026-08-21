# VoxCore — Инструкция для UI-разработчика

## 1. Быстрый старт

```bash
# Клонируй репозиторий
git clone https://github.com/R3G1ST/VoxCore.git
cd VoxCore

# Переключись на ветку ui
git checkout ui

# Открой в Visual Studio 2022
# (или Rider, но лучше VS — WinUI 3 лучше поддерживается)
```

## 2. Структура проекта

```
VoxCore/
├── VoxCore.Client/          ← ТВОЯ РАБОТА
│   ├── MainWindow.xaml      ← Главное окно (Discord-UI)
│   ├── AuthWindow.xaml      ← Экран входа/регистрации
│   ├── SettingsWindow.xaml  ← Настройки
│   ├── App.xaml             ← Стили, ресурсы, конвертеры
│   ├── ChatWindow.xaml      ← Окно ЛС (старое, не трогай)
│   └── *.cs                 ← Code-behind (минимум)
│
├── VoxCore.Server/          ← НЕ ТРОГАЙ
└── VoxCore.Client.csproj    ← Зависимости (не трогай)
```

## 3. Твои файлы (можно менять)

| Файл | Что внутри | Приоритет |
|------|-----------|-----------|
| `MainWindow.xaml` | Discord-UI: каналы, участники, чат, voice | ⭐ высокий |
| `AuthWindow.xaml` | Вход/регистрация, автологин | ⭐ высокий |
| `SettingsWindow.xaml` | Микрофон, громкость, шумоподавление | ⭐ средний |
| `App.xaml` | Стили кнопок, полей, конвертеры | ⭐ средний |
| `ChatWindow.xaml` | Окно ЛС (если нужно переделать) | ⚡ низкий |

## 4. Файлы, которые НЕЛЬЗЯ трогать

| Файл | Почему |
|------|--------|
| `ApiClient.cs` | Backend-логика, API-контракты |
| `VoiceClient.cs` | UDP-голос, Opus, AES-GCM |
| `WebRTCVoiceClient.cs` | WebRTC логика |
| `UpdateService.cs` | Проверка обновлений на GitHub |
| `*.csproj` | Зависимости (Concentus, SIPSorcery, NAudio) |
| `VoxCore.Server/*` | Серверная часть |

## 5. Цвета и стили (Discord-тема)

```xml
<!-- Основные цвета -->
Background:     #313338
Card:           #2b2d31
Sidebar:        #1e1f22
Input:          #383a40
Border:         #1e1f22
Accent:         #5865f2 (синий)
Success:        #57f287 (зелёный)
Danger:         #ed4245 (красный)
Text:           #dbdee1
Muted:          #949ba4
Link:           #00a8fc

<!-- Шрифт -->
FontFamily: Cascadia Code, Consolas, monospace
```

## 6. Стили в App.xaml

Все стили определены в `App.xaml`. Вот что доступно:

```xml
<!-- Кнопки -->
Style="{ThemeResource AuthButton}"      — основная кнопка
Style="{ThemeResource AuthLinkButton}"  — ссылка
Style="{ThemeResource VcSmallButton}"   — маленькая кнопка

<!-- Поля -->
Style="{ThemeResource AuthTextBox}"     — текстовое поле
Style="{ThemeResource AuthPassBox}"     — поле пароля

<!-- Конвертеры -->
BoolToVisibility         — bool → Visibility
InverseBoolToVisibility  — !bool → Visibility
BoolToOpacity            — bool → opacity (0/1)
```

## 7. IPC с backend (как общаться с ApiClient)

### AuthWindow → MainWindow

```csharp
// AuthWindow.xaml.cs
private async void LoginBtn_Click(object sender, RoutedEventArgs e)
{
    var api = new ApiClient("194.31.204.5:9988");
    try
    {
        var user = await api.LoginAsync(LoginBox.Text, PassBox.Password);
        var settings = new AppSettings { Server = "194.31.204.5:9988" };
        var main = new MainWindow(api, settings, user);
        main.Activate();
        Close();
    }
    catch (ApiException ex)
    {
        ErrorText.Text = ex.Message;
    }
}
```

### MainWindow — загрузка каналов

```csharp
// Автоматически загружается при старте
private async Task LoadChannelsAsync()
{
    var channels = await _api.GetChannelsAsync();
    _channels = channels;
    ChannelsList.ItemsSource = _channels;
}
```

### MainWindow — загрузка участников

```csharp
// Автоматически при входе в канал
private async Task LoadMembersAsync(ChannelInfo ch)
{
    // Для голоса: опрашивает UDP сервер
    // Для WebRTC: через API
    // Результат обновляет _members
}
```

### MainWindow — отправка сообщений

```csharp
// Канал-чат
private async Task SendChannelChatAsync()
{
    var text = ChatInput.Text.Trim();
    if (text.Length == 0) return;
    await _api.SendChannelMessageAsync(_currentChannel.Id, text);
    ChatInput.Text = "";
    await LoadChannelChatAsync(); // перезагрузка с сервера
}

// Личные сообщения
private async Task SendDmAsync()
{
    var text = DmChatInput.Text.Trim();
    if (text.Length == 0 || _currentDmFriend == null) return;
    await _api.SendMessageAsync(_currentDmFriend.Name, text);
    DmChatInput.Text = "";
    await LoadDmChatAsync();
}
```

## 8. Модели данных (что приходит с сервера)

```csharp
// Канал
public record ChannelInfo(int Id, string Name, int Users, bool HasPassword);

// Пользователь
public record UserInfo(string Name, string Color);

// Сообщение (канал)
public record MessageInfo(int Id, string From, string Text, long Ts);

// Сообщение (ЛС)
public record DmInfo(string From, string Text, long Ts);

// Участник голоса
public class MemberItem
{
    public string Name { get; set; }
    public bool Speaking { get; set; }
    public string SpeakingText => Speaking ? "●" : "";
    public SolidColorBrush SpeakingBrush => Speaking ? Green : Gray;
}
```

## 9. Как добавить новый элемент UI

### Пример: добавить бейдж непрочитанных

```xml
<!-- В ChannelItem шаблоне -->
<Grid>
    <StackPanel Orientation="Horizontal" Spacing="8">
        <TextBlock Text="{Binding Name}" />
        <Border Background="#ed4245" CornerRadius="8" Padding="6,2"
                Visibility="{Binding Unread, Converter={StaticResource BoolToVisibility}}">
            <TextBlock Text="{Binding UnreadCount}" FontSize="11" Foreground="White" />
        </Border>
    </StackPanel>
</Grid>
```

## 10. Как добавить анимацию

```xml
<!-- Пример: пульсация говорящего -->
<Border x:Name="SpeakingIndicator" Background="#57f287" CornerRadius="10"
        Width="10" Height="10" Opacity="0">
    <Border.RenderTransform>
        <ScaleTransform ScaleX="1" ScaleY="1" />
    </Border.RenderTransform>
    <Border.Triggers>
        <EventTrigger RoutedEvent="Loaded">
            <BeginStoryboard>
                <Storyboard RepeatBehavior="Forever">
                    <DoubleAnimation Storyboard.TargetProperty="Opacity"
                                     From="0.3" To="1" Duration="0:0:0.5"
                                     AutoReverse="True" />
                    <DoubleAnimation Storyboard.TargetName="SpeakingIndicator"
                                     Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleX)"
                                     From="0.8" To="1.2" Duration="0:0:0.5"
                                     AutoReverse="True" />
                </Storyboard>
            </BeginStoryboard>
        </EventTrigger>
    </Border.Triggers>
</Border>
```

## 11. Git workflow

```bash
# Ты работаешь в ветке ui
git checkout ui

# Добавляй изменения
git add MainWindow.xaml
git add App.xaml
git add MainWindow.xaml.cs  # только UI-логика!

# Коммить с описательным сообщением
git commit -m "feat: add unread badges to channels"

# Пуш
git push origin ui

# Когда фича готова — создай PR в dev
# Или попроси backend-разработчика замержить
```

## 12. Типичные ошибки

### ❌ "Не удалось загрузить XAML"
- Проверь синтаксис XML
- Убедись что все `x:Name` уникальны
- Не используй `BooleanToVisibilityConverter` (удалён из-за бага XamlCompiler)

### ❌ "Type not found"
- Проверь что тип существует в code-behind
- Пересобери проект (Build → Rebuild)

### ❌ "ApiException"
- Это backend-ошибка, не UI
- Проверь что сервер запущен
- Проверь IP и порт в `AppSettings.Server`

## 13. Чеклист перед коммитом

- [ ] Проект собирается без ошибок
- [ ] Нет `BooleanToVisibilityConverter` в XAML
- [ ] Все `x:Name` уникальны
- [ ] Не тронуты `ApiClient.cs`, `VoiceClient.cs`, `WebRTCVoiceClient.cs`
- [ ] Не тронуты `*.csproj` файлы
- [ ] Git status показывает только твои файлы

## 14. Связь с backend-разработчиком

### Если нужен новый API-endpoint:
1. Напиши в чат: "Мне нужен endpoint для X"
2. Backend добавит в `ApiClient.cs` и `Program.cs`
3. Ты обновишь UI

### Если сломался существующий API:
1. Проверь `git pull origin backend` (обнови ветку)
2. Если не помогло — напиши backend-разработчику

### API-контракты (что ожидать от сервера):

```
POST /api/auth/login
  Request:  {"login":"...","password":"..."}
  Response: {"ok":true,"data":{"token":"...","name":"...","color":"#5865f2"}}

POST /api/channels
  Request:  {"token":"..."}
  Response: {"ok":true,"data":[{"id":1,"name":"General","users":3,"hasPassword":false}]}

POST /api/channels/create
  Request:  {"token":"...","name":"...","password":"..."}
  Response: {"ok":true,"data":{"id":2}}

POST /api/friends
  Request:  {"token":"..."}
  Response: {"ok":true,"data":[{"name":"User1","color":"#57f287","online":true}]}

POST /api/send_message
  Request:  {"token":"...","to":"...","text":"..."}
  Response: {"ok":true}

POST /api/get_messages
  Request:  {"token":"...","from":"...","after":0}
  Response: {"ok":true,"data":[{"from":"User1","text":"hello","ts":1234567890}]}
```

## 15. Контакты

- **Backend-разработчик:** R3G1ST
- **GitHub:** https://github.com/R3G1ST/VoxCore
- **Discord:** (укажи свой Discord)

---

**Удачи! Если будут вопросы — пиши в чат.**

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace VoxCore.Client;

public sealed partial class AuthWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<ApiClient, AppSettings, UserInfo> _onAuthenticated;
    private bool _registerMode;

    public AuthWindow(AppSettings settings, Action<ApiClient, AppSettings, UserInfo> onAuthenticated)
    {
        _settings = settings;
        _onAuthenticated = onAuthenticated;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 520));
        AppWindow.Title = "VoxCore — вход";
        var host = settings.Server.Split(':')[0];
        _api = new ApiClient(host, 9988);
        NameBox.Text = settings.UserName;
    }

    private readonly ApiClient _api;

    private async void AuthBtn_Click(object sender, RoutedEventArgs e)
    {
        await DoAuthAsync();
    }

    private void PassBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = DoAuthAsync();
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        _registerMode = !_registerMode;
        AuthBtn.Content = _registerMode ? "ЗАРЕГИСТРИРОВАТЬСЯ" : "ВОЙТИ";
        ToggleBtn.Content = _registerMode ? "уже есть аккаунт? войти" : "нет аккаунта? зарегистрироваться";
        ErrorText.Text = "";
    }

    private async Task DoAuthAsync()
    {
        var name = NameBox.Text.Trim();
        var pass = PassBox.Password;
        if (name.Length == 0 || pass.Length == 0)
        {
            ErrorText.Text = "заполни ник и пароль";
            return;
        }
        AuthBtn.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            UserInfo user;
            if (_registerMode)
                (_, user) = await _api.RegisterAsync(name, pass);
            else
                (_, user) = await _api.LoginAsync(name, pass);

            _settings.Token = _api.Token;
            _settings.UserId = user.Id;
            _settings.UserName = user.Name;
            _settings.UserColor = user.Color;
            _settings.Save();
            _onAuthenticated(_api, _settings, user);
            Close();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
        }
        catch
        {
            ErrorText.Text = "нет связи с сервером";
        }
        finally
        {
            AuthBtn.IsEnabled = true;
        }
    }
}
using Microsoft.UI.Xaml;

namespace VoxCore.Client;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash("UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as System.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var settings = AppSettings.Load();
            var host = settings.Server.Split(':')[0];

            if (settings.Token is string token && settings.UserName.Length > 0)
            {
                var api = new ApiClient(host, 9988);
                api.RestoreToken(token);
                var user = new UserInfo { Id = settings.UserId, Name = settings.UserName, Color = settings.UserColor };
                _window = new MainWindow(api, settings, user);
            }
            else
            {
                _window = new AuthWindow(settings, OnAuthenticated);
            }
            _window.Activate();
        }
        catch (System.Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw;
        }
    }

    private void OnAuthenticated(ApiClient api, AppSettings settings, UserInfo user)
    {
        _window = new MainWindow(api, settings, user);
        _window.Activate();
    }

    private static void LogCrash(string source, System.Exception? ex)
    {
        try
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "voxcore-client-crash.log");
            System.IO.File.AppendAllText(log,
                $"[{System.DateTime.Now:HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { }
    }
}
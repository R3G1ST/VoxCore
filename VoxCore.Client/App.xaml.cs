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
            _window = new MainWindow();
            _window.Activate();
        }
        catch (System.Exception ex)
        {
            LogCrash("OnLaunched", ex);
            throw;
        }
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
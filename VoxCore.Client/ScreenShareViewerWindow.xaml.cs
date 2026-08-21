using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace VoxCore.Client;

public sealed partial class ScreenShareViewerWindow : Window
{
    private readonly string _sharerName;
    private readonly DispatcherTimer _pollTimer;

    public ScreenShareViewerWindow(string sharerName)
    {
        _sharerName = sharerName;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(960, 540));
        AppWindow.Title = $"Демонстрация — {sharerName}";

        SharerNameText.Text = sharerName;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += (_, _) => UpdateFrame();
        _pollTimer.Start();

        Closed += (_, _) => _pollTimer.Stop();
    }

    private void UpdateFrame()
    {
        try
        {
            var frame = ScreenShareReceiver.GetLastFrame(_sharerName);
            if (frame == null || frame.Length == 0) return;

            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(frame);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(frame);
                writer.StoreAsync().AsTask().Wait();
                writer.FlushAsync().AsTask().Wait();
            }
            stream.Seek(0);
            bitmap.SetSource(stream);
            ScreenImage.Source = bitmap;
            StatusText.Text = "демонстрация активна";
        }
        catch { }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}

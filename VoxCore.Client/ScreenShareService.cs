using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VoxCore.Client;

public sealed class ScreenShareService : IDisposable
{
    private const int TargetFps = 15;
    private const int JpegQuality = 70;

    private CancellationTokenSource? _cts;
    private volatile bool _isCapturing;
    private Thread? _captureThread;
    private IntPtr _targetHwnd = IntPtr.Zero;
    private bool _captureFullScreen;
    private int _screenIndex;
    private readonly ApiClient _api;
    private readonly string _roomId;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int dx, int dy, int dw, int dh, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const int SRCCOPY = 0x00CC0020;
    const int SM_XSCREEN = 76;
    const int SM_YSCREEN = 77;
    const int SM_CXSCREEN = 0;
    const int SM_CYSCREEN = 1;

    public bool IsCapturing => _isCapturing;
    public event Action<string>? StatusChanged;
    public event Action<byte[]>? FrameSent;

    public ScreenShareService(ApiClient api, string roomId)
    {
        _api = api;
        _roomId = roomId;
    }

    public void SetTargetDisplay(int index)
    {
        _captureFullScreen = true;
        _screenIndex = index;
        _targetHwnd = IntPtr.Zero;
    }

    public void SetTargetWindow(IntPtr hwnd)
    {
        _captureFullScreen = false;
        _targetHwnd = hwnd;
    }

    public void StartCapture()
    {
        if (_isCapturing) return;

        try
        {
            _cts = new CancellationTokenSource();
            _isCapturing = true;
            _captureThread = new Thread(() =>
            {
                try { CaptureLoop(_cts.Token); }
                catch (Exception ex) { StatusChanged?.Invoke($"критическая ошибка: {ex.Message}"); }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _captureThread.Start();
            StatusChanged?.Invoke("демонстрация экрана активна");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"ошибка запуска: {ex.Message}");
        }
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
        int frameCount = 0;

        while (!ct.IsCancellationRequested && _isCapturing)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                byte[]? jpeg = null;

                if (_captureFullScreen)
                {
                    jpeg = CaptureDisplay(_screenIndex);
                }
                else if (_targetHwnd != IntPtr.Zero && IsWindow(_targetHwnd))
                {
                    jpeg = CaptureWindow(_targetHwnd);
                }

                if (jpeg != null && jpeg.Length > 0)
                {
                    frameCount++;
                    if (frameCount % 30 == 1)
                        StatusChanged?.Invoke($"кадр #{frameCount}, {jpeg.Length} байт");

                    _ = SendFrameAsync(jpeg);
                    FrameSent?.Invoke(jpeg);
                }
                else
                {
                    if (frameCount == 0)
                        StatusChanged?.Invoke("захват вернул пустой кадр");
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"ошибка захвата: {ex.Message}");
            }

            var sleep = interval - sw.Elapsed;
            if (sleep > TimeSpan.Zero)
                Thread.Sleep(sleep);
        }

        StatusChanged?.Invoke($"захват остановлен, кадров: {frameCount}");
    }

    private byte[]? CaptureDisplay(int index)
    {
        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (index >= screens.Length) index = 0;
            var screen = screens[index];
            var bounds = screen.Bounds;

            hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) { StatusChanged?.Invoke("GetDC failed"); return null; }

            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, bounds.Width, bounds.Height);
            IntPtr hOld = SelectObject(hdcMem, hBitmap);

            BitBlt(hdcMem, 0, 0, bounds.Width, bounds.Height, hdcScreen, bounds.X, bounds.Y, SRCCOPY);
            SelectObject(hdcMem, hOld);

            var bmp = Image.FromHbitmap(hBitmap);
            var bytes = ImageToJpeg(bmp, JpegQuality);
            bmp.Dispose();
            return bytes;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"ошибка захвата: {ex.Message}");
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            if (hdcScreen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcScreen);
        }
    }

    private byte[]? CaptureWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowRect(hwnd, out RECT rect);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return null;

            IntPtr hdcWindow = GetDC(hwnd);
            IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
            IntPtr hBitmap = CreateCompatibleBitmap(hdcWindow, w, h);
            IntPtr hOld = SelectObject(hdcMem, hBitmap);

            BitBlt(hdcMem, 0, 0, w, h, hdcWindow, 0, 0, SRCCOPY);
            SelectObject(hdcMem, hOld);

            var bmp = Image.FromHbitmap(hBitmap);
            var bytes = ImageToJpeg(bmp, JpegQuality);
            bmp.Dispose();
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(hwnd, hdcWindow);

            return bytes;
        }
        catch { return null; }
    }

    private static byte[] ImageToJpeg(Image bmp, int quality)
    {
        var ms = new MemoryStream();

        var codec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        var eps = new EncoderParameters(1);
        eps.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

        bmp.Save(ms, codec, eps);
        return ms.ToArray();
    }

    private async Task SendFrameAsync(byte[] jpeg)
    {
        try
        {
            await _api.SendScreenFrameAsync(_roomId, jpeg);
        }
        catch { }
    }

    public void StopCapture()
    {
        _isCapturing = false;
        _cts?.Cancel();
        _captureThread?.Join(2000);
        StatusChanged?.Invoke("демонстрация экрана остановлена");
    }

    public void Dispose()
    {
        StopCapture();
    }
}

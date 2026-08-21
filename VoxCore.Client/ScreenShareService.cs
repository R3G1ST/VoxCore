using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VoxCore.Client;

public sealed class ScreenShareService : IDisposable
{
    private const int TargetWidth = 1920;
    private const int TargetHeight = 1080;
    private const int TargetFps = 60;

    private CancellationTokenSource? _cts;
    private volatile bool _isCapturing;
    private readonly object _lock = new();
    private byte[] _lastFrame = [];
    private readonly Stopwatch _fpsSw = new();
    private int _frameCount;
    private Thread? _captureThread;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;

    public bool IsCapturing => _isCapturing;
    public int ActualFps { get; private set; }
    public event Action<byte[]>? FrameCaptured;
    public event Action<string>? StatusChanged;

    public async Task StartCaptureAsync()
    {
        if (_isCapturing) return;

        try
        {
            _cts = new CancellationTokenSource();
            _isCapturing = true;
            _fpsSw.Restart();
            _frameCount = 0;

            _captureThread = new Thread(() => CaptureLoop(_cts.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _captureThread.Start();

            StatusChanged?.Invoke("демонстрация экрана активна");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"ошибка: {ex.Message}");
            StopCapture();
        }
    }

    private void CaptureLoop(CancellationToken ct)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / TargetFps);
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        while (!ct.IsCancellationRequested && _isCapturing)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var frame = CaptureScreen(screenWidth, screenHeight);
                if (frame.Length > 0)
                {
                    lock (_lock)
                    {
                        _lastFrame = frame;
                    }
                    FrameCaptured?.Invoke(frame);
                }

                _frameCount++;
                if (_fpsSw.ElapsedMilliseconds >= 1000)
                {
                    ActualFps = (int)(_frameCount * 1000.0 / _fpsSw.ElapsedMilliseconds);
                    _frameCount = 0;
                    _fpsSw.Restart();
                }
            }
            catch { }

            var elapsed = sw.Elapsed;
            var sleepTime = frameInterval - elapsed;
            if (sleepTime > TimeSpan.Zero)
                Thread.Sleep(sleepTime);
        }
    }

    private byte[] CaptureScreen(int screenW, int screenH)
    {
        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, TargetWidth, TargetHeight);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        // StretchBlt for scaling
        int rop = 0x00CC0020; // SRCCOPY
        BitBlt(hdcMem, 0, 0, TargetWidth, TargetHeight, hdcScreen, 0, 0, rop);

        SelectObject(hdcMem, hOld);

        // Convert to byte array
        var bmp = Image.FromHbitmap(hBitmap);
        var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        var bytes = ms.ToArray();

        bmp.Dispose();
        DeleteObject(hBitmap);
        DeleteDC(hdcMem);
        ReleaseDC(IntPtr.Zero, hdcScreen);

        return bytes;
    }

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

    public void StopCapture()
    {
        _isCapturing = false;
        _cts?.Cancel();
        _captureThread?.Join(1000);
        StatusChanged?.Invoke("демонстрация экрана остановлена");
    }

    public byte[] GetLastFrame()
    {
        lock (_lock)
        {
            return _lastFrame;
        }
    }

    public void Dispose()
    {
        StopCapture();
    }
}

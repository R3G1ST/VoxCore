using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace VoxCore.Client;

public sealed partial class ScreenSharePickerWindow : Window
{
    private readonly List<DisplayInfo> _displays = [];
    private readonly List<WindowInfo> _windows = [];
    public DisplayInfo? SelectedDisplay { get; private set; }
    public WindowInfo? SelectedWindow { get; private set; }
    public bool Confirmed { get; private set; }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

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

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public ScreenSharePickerWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 500));
        AppWindow.Title = "Выбор демонстрации";

        LoadDisplays();
        LoadWindows();
    }

    private void LoadDisplays()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen != null)
        {
            _displays.Add(new DisplayInfo
            {
                Name = $"Дисплей 1: {screen.Bounds.Width}x{screen.Bounds.Height}",
                Width = screen.Bounds.Width,
                Height = screen.Bounds.Height,
                Index = 0
            });
        }

        int i = 1;
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            if (s != screen)
            {
                _displays.Add(new DisplayInfo
                {
                    Name = $"Дисплей {i + 1}: {s.Bounds.Width}x{s.Bounds.Height}",
                    Width = s.Bounds.Width,
                    Height = s.Bounds.Height,
                    Index = i
                });
            }
            i++;
        }

        DisplaysList.ItemsSource = _displays;
        if (_displays.Count > 0) DisplaysList.SelectedIndex = 0;
    }

    private void LoadWindows()
    {
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (string.IsNullOrWhiteSpace(title)) return true;
            if (title.Contains("VoxCore")) return true;

            GetWindowRect(hWnd, out RECT rect);
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w < 100 || h < 100) return true;

            _windows.Add(new WindowInfo
            {
                Title = title,
                Handle = hWnd,
                Width = w,
                Height = h
            });
            return true;
        }, IntPtr.Zero);

        WindowsList.ItemsSource = _windows;
    }

    private void Display_Click(object sender, RoutedEventArgs e)
    {
        if (DisplaysList.SelectedItem is DisplayInfo d)
        {
            SelectedDisplay = d;
            SelectedWindow = null;
        }
    }

    private void Window_Click(object sender, RoutedEventArgs e)
    {
        if (WindowsList.SelectedItem is WindowInfo w)
        {
            SelectedWindow = w;
            SelectedDisplay = null;
        }
    }

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDisplay == null && SelectedWindow == null)
        {
            InfoText.Text = "выбери дисплей или окно";
            return;
        }
        Confirmed = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();
}

public class DisplayInfo
{
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Index { get; set; }
}

public class WindowInfo
{
    public string Title { get; set; } = "";
    public IntPtr Handle { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

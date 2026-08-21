using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoxCore.Client;

public sealed partial class ScreenSharePickerWindow : Window
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);

    private readonly List<DisplayItem> _displays = new();
    private readonly List<WindowItem> _windows = new();
    private int _selectedDisplay = -1;
    private IntPtr _selectedWindow = IntPtr.Zero;

    public static TaskCompletionSource<bool>? PickerTcs { get; private set; }

    public ScreenSharePickerWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 520));
        AppWindow.Title = "Выбор демонстрации";

        PickerTcs = new TaskCompletionSource<bool>();
        Closed += (_, _) =>
        {
            if (PickerTcs.Task.Status == TaskStatus.WaitingForActivation)
                PickerTcs.TrySetResult(false);
        };

        LoadItems();
    }

    private void LoadItems()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            _displays.Add(new DisplayItem
            {
                Index = i,
                Name = $"Дисплей {i + 1}: {s.Bounds.Width}x{s.Bounds.Height}",
                Primary = s.Primary
            });
        }
        DisplaysList.ItemsSource = _displays;

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title) || title.Contains("VoxCore")) return true;
            GetWindowRect(h, out RECT r);
            int w = r.R - r.L, h2 = r.B - r.T;
            if (w < 100 || h2 < 100) return true;
            _windows.Add(new WindowItem { Handle = h, Title = title, Width = w, Height = h2 });
            return true;
        }, IntPtr.Zero);

        WindowsList.ItemsSource = _windows;
    }

    private void DisplaysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplaysList.SelectedIndex >= 0)
        {
            _selectedDisplay = _displays[DisplaysList.SelectedIndex].Index;
            _selectedWindow = IntPtr.Zero;
            WindowsList.SelectedIndex = -1;
        }
    }

    private void WindowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowsList.SelectedIndex >= 0)
        {
            _selectedWindow = _windows[WindowsList.SelectedIndex].Handle;
            _selectedDisplay = -1;
            DisplaysList.SelectedIndex = -1;
        }
    }

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDisplay < 0 && _selectedWindow == IntPtr.Zero)
        {
            InfoText.Text = "выбери дисплей или окно";
            return;
        }

        ScreenSharePickerResult.DisplayIndex = _selectedDisplay;
        ScreenSharePickerResult.WindowHandle = _selectedWindow;
        ScreenSharePickerResult.Confirmed = true;

        PickerTcs?.TrySetResult(true);
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        ScreenSharePickerResult.Reset();
        PickerTcs?.TrySetResult(false);
        Close();
    }
}

public class DisplayItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public bool Primary { get; set; }
}

public class WindowItem
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

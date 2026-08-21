namespace VoxCore.Client;

public static class ScreenSharePickerResult
{
    public static bool Confirmed { get; set; }
    public static int DisplayIndex { get; set; } = -1;
    public static IntPtr WindowHandle { get; set; } = IntPtr.Zero;
    public static string WindowTitle { get; set; } = "";

    public static void Reset()
    {
        Confirmed = false;
        DisplayIndex = -1;
        WindowHandle = IntPtr.Zero;
        WindowTitle = "";
    }
}

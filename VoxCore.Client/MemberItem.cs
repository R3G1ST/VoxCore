using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed class MemberItem : INotifyPropertyChanged
{
    private static readonly string[] Colors =
        ["#5865f2", "#eb459e", "#faa61a", "#3ba55d", "#ed4245", "#9b59b6", "#00b0f4", "#f0b232"];

    public string Name { get; }
    public string Letter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public Brush ColorBrush { get; }

    private bool _isSpeaking;
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingBorderVisibility)));
        }
    }
    public string SpeakingText => _isSpeaking ? "●" : "";
    public Brush SpeakingBrush => _isSpeaking ? MainWindow.BrushFromHex("#3ba55d") : MainWindow.BrushFromHex("#00000000");
    public Visibility SpeakingBorderVisibility => _isSpeaking ? Visibility.Visible : Visibility.Collapsed;

    private bool _isScreenSharing;
    public bool IsScreenSharing
    {
        get => _isScreenSharing;
        set
        {
            if (_isScreenSharing == value) return;
            _isScreenSharing = value;
            _screenShareStarted = value ? DateTime.Now : null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScreenSharing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenShareStatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenShareStatusBrush)));
        }
    }

    public string ScreenShareStatusText
    {
        get
        {
            if (!_isScreenSharing) return "";
            var elapsed = _screenShareStarted.HasValue ? DateTime.Now - _screenShareStarted.Value : TimeSpan.Zero;
            return $"🖥 демонстрация экрана — {elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }
    }

    public Brush ScreenShareStatusBrush => _isScreenSharing
        ? MainWindow.BrushFromHex("#3ba55d")
        : MainWindow.BrushFromHex("#00000000");

    private DateTime? _screenShareStarted;

    public MemberItem(string name)
    {
        Name = name;
        var hex = Colors[Math.Abs(name.GetHashCode()) % Colors.Length];
        ColorBrush = MainWindow.BrushFromHex(hex);
    }

    public void RefreshShareTime()
    {
        if (_isScreenSharing)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScreenShareStatusText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
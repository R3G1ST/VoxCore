using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed class MemberItem : INotifyPropertyChanged
{
    private static readonly string[] Colors =
        ["#5865f2", "#eb459e", "#faa61a", "#3ba55d", "#ed4245", "#9b59b6", "#00b0f4", "#f0b232"];

    public string Name { get; }
    public string Letter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public Brush ColorBrush { get; }
    public string Status { get; } = "в голосовом канале";

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
        }
    }

    public string SpeakingText => _isSpeaking ? "●" : "";
    public Brush SpeakingBrush => _isSpeaking ? MainWindow.BrushFromHex("#3ba55d") : MainWindow.BrushFromHex("#00000000");

    public MemberItem(string name)
    {
        Name = name;
        var hex = Colors[Math.Abs(name.GetHashCode()) % Colors.Length];
        ColorBrush = MainWindow.BrushFromHex(hex);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
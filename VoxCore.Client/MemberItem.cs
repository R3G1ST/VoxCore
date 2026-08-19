using System.ComponentModel;

namespace VoxCore.Client;

public sealed class MemberItem : INotifyPropertyChanged
{
    public string Name { get; }
    private bool _isSpeaking;

    public bool IsSpeaking
    {
        get => _isSpeaking;
        set
        {
            if (_isSpeaking == value) return;
            _isSpeaking = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
        }
    }

    public MemberItem(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged;
}
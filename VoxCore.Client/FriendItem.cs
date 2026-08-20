using Microsoft.UI.Xaml.Media;

namespace VoxCore.Client;

public sealed class FriendItem
{
    public int Id { get; }
    public string Name { get; }
    public string Letter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public Brush ColorBrush { get; }
    public bool Online { get; }
    public string State { get; }

    public string Status => State == "None" ? (Online ? "в сети" : "не в сети") : StateText;

    public string StateText => State switch
    {
        "Friend" => "уже друзья",
        "Requested" => "запрос отправлен",
        "Incoming" => "прими запрос во вкладке Друзья",
        _ => Online ? "в сети" : "не в сети"
    };

    public SolidColorBrush OnlineBrush =>
        Online ? MainWindow.BrushFromHex("#3ba55d") : MainWindow.BrushFromHex("#4e5058");

    public SolidColorBrush StateBrush => State switch
    {
        "Friend" => MainWindow.BrushFromHex("#3ba55d"),
        "Requested" => MainWindow.BrushFromHex("#f0b232"),
        "Incoming" => MainWindow.BrushFromHex("#00b0f4"),
        _ => MainWindow.BrushFromHex("#949ba4")
    };

    public FriendItem(int id, string name, string color, bool online, string state = "None")
    {
        Id = id;
        Name = name;
        ColorBrush = MainWindow.BrushFromHex(color);
        Online = online;
        State = state;
    }

    public static FriendItem FromUser(UserInfo u) => new(u.Id, u.Name, u.Color, u.Online, u.State);
}
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace VoxCore.Client;

public sealed partial class PilotProfile : UserControl
{
    public event EventHandler? SettingsRequested;
    public event EventHandler<bool>? MicToggled;
    public event EventHandler<bool>? SpeakerToggled;

    public PilotProfile()
    {
        InitializeComponent();
    }

    public void SetUser(string name, string color)
    {
        AvatarBorder.Background = MainWindow.BrushFromHex(color);
        AvatarLetter.Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        UserNameText.Text = name;
    }

    public void SetStatus(string status, string color)
    {
        UserStatusText.Text = status;
        UserStatusText.Foreground = MainWindow.BrushFromHex(color);
    }

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void MicToggle_Checked(object sender, RoutedEventArgs e) =>
        MicToggled?.Invoke(this, true);

    private void MicToggle_Unchecked(object sender, RoutedEventArgs e) =>
        MicToggled?.Invoke(this, false);

    private void SpeakerToggle_Checked(object sender, RoutedEventArgs e) =>
        SpeakerToggled?.Invoke(this, true);

    private void SpeakerToggle_Unchecked(object sender, RoutedEventArgs e) =>
        SpeakerToggled?.Invoke(this, false);
}

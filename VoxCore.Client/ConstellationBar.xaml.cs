using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace VoxCore.Client;

public sealed partial class ConstellationBar : UserControl
{
    public event EventHandler? SettingsRequested;
    public event EventHandler? AddServerRequested;

    private readonly List<Border> _serverIcons = [];
    private int _selectedServerIndex = -1;

    public ConstellationBar()
    {
        InitializeComponent();
    }

    public void SetServers(List<ChannelInfo> channels)
    {
        ServerList.Children.Clear();
        _serverIcons.Clear();
        _selectedServerIndex = -1;

        var groups = channels.GroupBy(c => c.Name[..Math.Min(2, c.Name.Length)].ToUpperInvariant());
        int idx = 0;
        foreach (var group in groups)
        {
            var serverName = group.Key;
            var border = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(24),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 8, 8, 14)),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 24, 24, 32)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Tag = idx,
                IsHitTestVisible = true
            };

            var tb = new TextBlock
            {
                Text = serverName,
                FontFamily = new FontFamily("Cascadia Code"),
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 90, 90, 106)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            border.Child = tb;

            var tip = new ToolTip { Content = string.Join(", ", group.Select(c => c.Name)) };
            ToolTipService.SetToolTip(border, tip);

            border.PointerEntered += ServerIcon_PointerEntered;
            border.PointerExited += ServerIcon_PointerExited;
            border.Tapped += ServerIcon_Tapped;

            ServerList.Children.Add(border);
            _serverIcons.Add(border);
            idx++;
        }
    }

    private void ServerIcon_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.Child is TextBlock tb)
        {
            b.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0, 229, 255));
            b.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 13, 15, 30));
            tb.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 240));
        }
    }

    private void ServerIcon_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.Child is TextBlock tb)
        {
            var isSelected = _selectedServerIndex == (int)b.Tag;
            b.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 24, 24, 32));
            b.Background = isSelected
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 13, 15, 30))
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 8, 8, 14));
            tb.Foreground = isSelected
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 240))
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 90, 90, 106));
        }
    }

    private void ServerIcon_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border b)
        {
            _selectedServerIndex = (int)b.Tag;
            foreach (var icon in _serverIcons)
            {
                if (icon.Child is TextBlock t)
                {
                    var sel = (int)icon.Tag == _selectedServerIndex;
                    icon.Background = sel
                        ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 13, 15, 30))
                        : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 8, 8, 14));
                    t.Foreground = sel
                        ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 240))
                        : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 90, 90, 106));
                }
            }
        }
    }

    private void AddServerBtn_Click(object sender, RoutedEventArgs e) =>
        AddServerRequested?.Invoke(this, EventArgs.Empty);

    private void GearBtn_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);
}

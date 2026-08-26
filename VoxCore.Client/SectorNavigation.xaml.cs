using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;

namespace VoxCore.Client;

public sealed partial class SectorNavigation : UserControl
{
    public event EventHandler<ChannelInfo>? ChannelSelected;
    public event EventHandler? AddChannelRequested;
    public event EventHandler? AlliesTabRequested;
    public event EventHandler<string>? PilotSearched;

    private bool _showingAllies;

    public SectorNavigation()
    {
        InitializeComponent();
    }

    public void SetSystemName(string name)
    {
        SystemNameText.Text = name.ToUpperInvariant();
    }

    public void SetChannels(List<ChannelInfo> channels)
    {
        ChannelsPanel.Children.Clear();

        foreach (var ch in channels)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = ch.HasPassword ? "🔒" : "🔊";
            var iconTb = new TextBlock
            {
                Text = icon,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var nameTb = new TextBlock
            {
                Text = ch.Name,
                FontFamily = new FontFamily("Cascadia Code"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 240)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            Grid.SetColumn(iconTb, 0);
            Grid.SetColumn(nameTb, 1);
            row.Children.Add(iconTb);
            row.Children.Add(nameTb);

            row.Tag = ch;
            row.Tapped += (_, _) => ChannelSelected?.Invoke(this, ch);
            row.PointerEntered += (_, _) =>
            {
                row.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 13, 15, 30));
            };
            row.PointerExited += (_, _) =>
            {
                row.Background = null;
            };

            ChannelsPanel.Children.Add(row);
        }

        // Add channel button
        var addBtn = new Button
        {
            Content = "＋ создать сектор",
            FontFamily = new FontFamily("Cascadia Code"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 59, 165, 93)),
            Background = null,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 6, 0, 6),
            Margin = new Thickness(0, 8, 0, 0)
        };
        addBtn.Click += (_, _) => AddChannelRequested?.Invoke(this, EventArgs.Empty);
        ChannelsPanel.Children.Add(addBtn);
    }

    public void SetAllies(List<FriendItem> allies)
    {
        AlliesListPanel.Children.Clear();
        foreach (var ally in allies)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var avatar = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = ally.ColorBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ally.Letter,
                    FontFamily = new FontFamily("Cascadia Code"),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var nameTb = new TextBlock
            {
                Text = ally.Name,
                FontFamily = new FontFamily("Cascadia Code"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 232, 232, 240)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };

            var statusDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = ally.OnlineBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };

            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(nameTb, 1);
            Grid.SetColumn(statusDot, 2);
            row.Children.Add(avatar);
            row.Children.Add(nameTb);
            row.Children.Add(statusDot);

            AlliesListPanel.Children.Add(row);
        }
    }

    private void SectorsTabBtn_Click(object sender, RoutedEventArgs e)
    {
        _showingAllies = false;
        SectorsTabBtn.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 13, 58, 84));
        SectorsTabBtn.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 127, 227, 255));
        AlliesTabBtn.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 8, 8, 14));
        AlliesTabBtn.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 159, 255, 255));
        ChannelsPanel.Visibility = Visibility.Visible;
        AlliesPanel.Visibility = Visibility.Collapsed;
    }

    private void AlliesTabBtn_Click(object sender, RoutedEventArgs e)
    {
        _showingAllies = true;
        AlliesTabBtn.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 13, 58, 84));
        AlliesTabBtn.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 127, 227, 255));
        SectorsTabBtn.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 8, 8, 14));
        SectorsTabBtn.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 159, 255, 255));
        ChannelsPanel.Visibility = Visibility.Collapsed;
        AlliesPanel.Visibility = Visibility.Visible;
        AlliesTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PilotSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            PilotSearched?.Invoke(this, PilotSearchBox.Text);
    }

    private void PilotSearchBtn_Click(object sender, RoutedEventArgs e) =>
        PilotSearched?.Invoke(this, PilotSearchBox.Text);
}

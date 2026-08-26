using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Threading.Tasks;

namespace VoxCore.Client;

public sealed partial class BootSequenceView : UserControl
{
    public event EventHandler? BootCompleted;

    private readonly string[] _stages = new[]
    {
        "Инициализация квантовой связи...",
        "Сканирование звёздных систем...",
        "Загрузка учётных данных пилота...",
        "Связь с Нексусом установлена"
    };

    public BootSequenceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RunBootSequence();
    }

    private async Task RunBootSequence()
    {
        // Stage 0: Logo fade in
        var logoFade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(600) };
        Storyboard.SetTarget(logoFade, LogoText);
        Storyboard.SetTargetProperty(logoFade, "Opacity");
        var sb1 = new Storyboard();
        sb1.Children.Add(logoFade);
        sb1.Begin();

        await Task.Delay(400);

        // Tagline fade in
        var tagFade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(400) };
        Storyboard.SetTarget(tagFade, TaglineText);
        Storyboard.SetTargetProperty(tagFade, "Opacity");
        var sb2 = new Storyboard();
        sb2.Children.Add(tagFade);
        sb2.Begin();

        await Task.Delay(300);

        // Progress bar appear
        var progFade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(300) };
        Storyboard.SetTarget(progFade, ProgressContainer);
        Storyboard.SetTargetProperty(progFade, "Opacity");
        var sb3 = new Storyboard();
        sb3.Children.Add(progFade);
        sb3.Begin();

        await Task.Delay(200);

        // Stage text appear
        var stageFade = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(300) };
        Storyboard.SetTarget(stageFade, StageText);
        Storyboard.SetTargetProperty(stageFade, "Opacity");
        var sb4 = new Storyboard();
        sb4.Children.Add(stageFade);
        sb4.Begin();

        await Task.Delay(300);

        // Run through stages
        for (int i = 0; i < _stages.Length; i++)
        {
            StageText.Text = _stages[i];
            double targetWidth = 400.0 * (i + 1) / _stages.Length;

            var widthAnim = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(800),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(widthAnim, ProgressFill);
            Storyboard.SetTargetProperty(widthAnim, "Width");
            var sb = new Storyboard();
            sb.Children.Add(widthAnim);
            sb.Begin();

            await Task.Delay(900);
        }

        // Flash green on complete
        ProgressFill.Background = new SolidColorBrush(Microsoft.UI.Colors.Green);

        await Task.Delay(500);

        // Fade out everything
        var fadeOut = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(400) };
        Storyboard.SetTarget(fadeOut, RootGrid);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var sbFade = new Storyboard();
        sbFade.Children.Add(fadeOut);
        sbFade.Begin();

        await Task.Delay(500);

        BootCompleted?.Invoke(this, EventArgs.Empty);
    }
}

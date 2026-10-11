using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using Nighty.Controls;
using Nighty.Services;

namespace Nighty.Views;

/// <summary>
/// The loading screen. Start-up reports each real step here (loading settings, preparing the interface, checking Roblox...)
/// and the bar glides to it. <see cref="CloseAsync"/> fades it away once the main window is ready.
/// </summary>
public partial class SplashWindow : Window
{
    private const double TrackWidth = 280;

    public SplashWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "Version " + AppVersion.Text(typeof(SplashWindow).Assembly.GetName().Version);
        if (Seasons.Active is { } season) UseSeasonLook(season);
    }

    private IReadOnlyDictionary<string, string>? _steps;

    /// <summary>The loading screen during a season: its night scene, emoji, glow and greeting, and playful step names.
    /// It uses its own colours because it appears before the saved theme is loaded.</summary>
    private void UseSeasonLook(Season season)
    {
        _steps = season.Steps;
        var p = ThemeService.Palettes.First(t => t.Id == season.Id);
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        SolidColorBrush B(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
        Resources["PanelBrush"] = B(p.Panel); Resources["BorderStrongBrush"] = B(p.BorderStrong); Resources["Panel3Brush"] = B(p.Panel3);
        Resources["TextBrush"] = B(p.Text); Resources["MutedBrush"] = B(p.Muted); Resources["DimBrush"] = B(p.Dim);
        Resources["AccentBrush"] = B(p.Accent); Resources["AccentColor"] = C(p.Accent);
        var grad = new LinearGradientBrush(C(p.Accent), C(p.Accent2), 0); grad.Freeze();
        Resources["PremiumBrush"] = grad;
        Scene.Source = SeasonScenes.Get(season.Id);
        Scene.Visibility = Visibility.Visible;
        LogoIcon.Visibility = Visibility.Collapsed;
        LogoPumpkin.Text = season.Emoji;
        LogoPumpkin.Visibility = Visibility.Visible;
        Greeting.Text = season.Greeting;
        Greeting.Visibility = Visibility.Visible;
        Sfx.Launch(season.Id);
    }

    /// <summary>Shows a step and moves the bar to <paramref name="progress"/> (0-1), then lets the UI paint it.</summary>
    public async Task StepAsync(string text, double progress, int holdMs = 140)
    {
        StepText.Text = _steps != null && _steps.TryGetValue(text, out var themed) ? themed : text;
        StepText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(220)));
        Fill.BeginAnimation(WidthProperty, new DoubleAnimation(TrackWidth * Math.Clamp(progress, 0, 1), TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        await Dispatcher.Yield(DispatcherPriority.Render);
        if (holdMs > 0) await Task.Delay(holdMs);
    }

    public async Task CloseAsync()
    {
        var done = new TaskCompletionSource();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => done.TrySetResult();
        BeginAnimation(OpacityProperty, fade);
        await Task.WhenAny(done.Task, Task.Delay(500));
        Close();
    }
}

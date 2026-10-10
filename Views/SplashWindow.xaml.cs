using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

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
        VersionLabel.Text = "Version " + (typeof(SplashWindow).Assembly.GetName().Version?.ToString(3) ?? "");
    }

    /// <summary>Shows a step and moves the bar to <paramref name="progress"/> (0-1), then lets the UI paint it.</summary>
    public async Task StepAsync(string text, double progress, int holdMs = 140)
    {
        StepText.Text = text;
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

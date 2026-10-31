using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Services;

namespace Nighty.Controls;

/// <summary>Shows the shake as it would move, drawn with the same path generator the helper uses, so the sliders can be judged by eye.</summary>
public sealed class ShakePreview : FrameworkElement
{
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(nameof(Settings), typeof(ShakeSettings), typeof(ShakePreview));
    public ShakeSettings? Settings { get => (ShakeSettings?)GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly ShakePath _path = new(0xC0FFEEUL, 3, 90, 0.6);
    private readonly Queue<Point> _trail = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public ShakePreview()
    {
        Height = 150;
        _timer.Tick += (_, _) => { Step(); InvalidateVisual(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Step()
    {
        var s = Settings;
        if (s == null) return;
        _path.Configure(ShakePath.AmplitudeFor(s.Strength), ShakePath.PeriodFor(s.Speed), ShakePath.SmoothnessFor(s.Smoothness));
        var o = _path.Sample(_clock.Elapsed.TotalMilliseconds);
        _trail.Enqueue(new Point(o.X, o.Y));
        while (_trail.Count > 90) _trail.Dequeue();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var panel = (Brush)(Application.Current.TryFindResource("Panel2Brush") ?? Brushes.Black);
        var accent = (Brush)(Application.Current.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue);
        var border = (Brush)(Application.Current.TryFindResource("BorderBrush") ?? Brushes.Gray);
        dc.DrawRoundedRectangle(panel, new Pen(border, 1), new Rect(0.5, 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)), 10, 10);
        double cx = w / 2, cy = h / 2;
        var faint = new Pen(border, 1);
        dc.DrawLine(faint, new Point(cx - 14, cy), new Point(cx + 14, cy));
        dc.DrawLine(faint, new Point(cx, cy - 14), new Point(cx, cy + 14));
        if (_trail.Count < 2) return;

        // 1 pixel of shake is drawn as k screen units; the widest possible shake always fits the box.
        double reach = Math.Max(1, (Settings != null ? ShakePath.AmplitudeFor(Settings.Strength) : 3) * 1.25);
        double k = Math.Min(w, h) / 2 * 0.8 / reach;
        var pts = _trail.ToArray();
        for (int i = 1; i < pts.Length; i++)
        {
            var pen = new Pen(accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var b = accent.Clone(); b.Opacity = (double)i / pts.Length; b.Freeze();
            pen.Brush = b;
            dc.DrawLine(pen, new Point(cx + pts[i - 1].X * k, cy + pts[i - 1].Y * k), new Point(cx + pts[i].X * k, cy + pts[i].Y * k));
        }
        var last = pts[^1];
        dc.DrawEllipse(accent, null, new Point(cx + last.X * k, cy + last.Y * k), 4, 4);
    }
}

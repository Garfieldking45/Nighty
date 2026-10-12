using System.Windows;
using System.Windows.Media;

namespace Nighty.Controls;

/// <summary>A small line chart of recent values (newest on the right). Draws nothing when there are no values, so a missing reading never shows as a flat line at zero.</summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
        var v = Values;
        if (v == null || v.Count < 2 || w < 8 || h < 8) return;
        var stroke = Stroke ?? (Brush)Application.Current.FindResource("AccentBrush");
        double max = Math.Max(1e-6, Maximum);
        double step = w / (v.Count - 1);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < v.Count; i++) ctx.LineTo(new Point(i * step, h - Math.Clamp(v[i] / max, 0, 1) * (h - 2) - 1), true, false);
            ctx.LineTo(new Point(w, h), true, false);
        }
        geo.Freeze();
        var fill = stroke.Clone(); fill.Opacity = 0.14; fill.Freeze();
        dc.DrawGeometry(fill, null, geo);
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(0, h - Math.Clamp(v[0] / max, 0, 1) * (h - 2) - 1), false, false);
            for (int i = 1; i < v.Count; i++) ctx.LineTo(new Point(i * step, h - Math.Clamp(v[i] / max, 0, 1) * (h - 2) - 1), true, false);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}

using System.Windows;
using System.Windows.Media;
using Nighty.Models;

namespace Nighty.Controls;

/// <summary>Draws a crosshair from an <see cref="OverlayConfig"/>. Call <see cref="Update"/> after the config changes.</summary>
public sealed class CrosshairVisual : FrameworkElement
{
    private const int Pad = 3;   // room for the outline
    private readonly OverlayConfig _cfg;

    public CrosshairVisual(OverlayConfig cfg)
    {
        _cfg = cfg;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);   // crisp one-pixel lines
    }

    public void Update() { InvalidateMeasure(); InvalidateVisual(); }

    private int Reach => _cfg.Gap + _cfg.ArmLength + (_cfg.Style == CrosshairStyle.Circle ? _cfg.Thickness : 0);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Even size so the centre falls on a pixel boundary.
        int side = (Reach + Pad) * 2;
        return new Size(side, side);
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0x00, 0xFF, 0x55); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double c = (Reach + Pad);
        int t = _cfg.Thickness, gap = _cfg.Gap, arm = _cfg.ArmLength;
        var fill = new SolidColorBrush(ParseColor(_cfg.Color)); fill.Freeze();
        var edge = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0)); edge.Freeze();
        double half = t / 2.0;

        void Rect(double x, double y, double w, double h)
        {
            if (_cfg.Outline) dc.DrawRectangle(edge, null, new Rect(x - 1, y - 1, w + 2, h + 2));
            dc.DrawRectangle(fill, null, new Rect(x, y, w, h));
        }
        void Dot(double r)
        {
            if (_cfg.Outline) dc.DrawEllipse(edge, null, new Point(c, c), r + 1, r + 1);
            dc.DrawEllipse(fill, null, new Point(c, c), r, r);
        }

        switch (_cfg.Style)
        {
            case CrosshairStyle.Cross:
            case CrosshairStyle.CrossDot:
                Rect(c - gap - arm, c - half, arm, t);   // left
                Rect(c + gap, c - half, arm, t);         // right
                Rect(c - half, c - gap - arm, t, arm);   // top
                Rect(c - half, c + gap, t, arm);         // bottom
                if (_cfg.Style == CrosshairStyle.CrossDot) Dot(Math.Max(1, t * 0.75));
                break;
            case CrosshairStyle.Dot:
                Dot(Math.Max(1.5, t * 1.25));
                break;
            case CrosshairStyle.Circle:
            {
                double r = gap + arm / 2.0;
                if (_cfg.Outline) dc.DrawEllipse(null, new Pen(edge, t + 2), new Point(c, c), r, r);
                dc.DrawEllipse(null, new Pen(fill, t), new Point(c, c), r, r);
                break;
            }
        }
    }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nighty.Controls;

/// <summary>The built-in pictures behind the window for the seasonal themes (drawn in code, nothing shipped as files).</summary>
public static class SeasonScenes
{
    private const int W = HalloweenScene.W, H = HalloweenScene.H;
    private static readonly Dictionary<string, BitmapSource> Cache = new();

    public static BitmapSource Get(string themeId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(themeId, out var hit)) return hit;
            var made = themeId switch
            {
                "thanksgiving" => Render(DrawThanksgiving),
                "christmas" => Render(DrawChristmas),
                _ => HalloweenScene.Get(),
            };
            Cache[themeId] = made;
            return made;
        }
    }

    private static SolidColorBrush Br(string hex, double a = 1) => HalloweenScene.Br(hex, a);

    private static BitmapSource Render(Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Hill(DrawingContext dc, string color, double baseY, double amp, double freq, double phase)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, H), true, true);
            for (int x = 0; x <= W; x += 20) c.LineTo(new Point(x, baseY + Math.Sin(x * freq + phase) * amp), true, false);
            c.LineTo(new Point(W, H), true, false);
        }
        g.Freeze();
        dc.DrawGeometry(Br(color), null, g);
    }

    // ---------------------------------------------------------------- Thanksgiving

    private static void DrawThanksgiving(DrawingContext dc)
    {
        var rnd = new Random(7);
        dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0x14, 0x0B, 0x10), 0), new(Color.FromRgb(0x4A, 0x20, 0x26), 0.5), new(Color.FromRgb(0xC2, 0x6A, 0x2A), 0.86), new(Color.FromRgb(0xE2, 0xA1, 0x2D), 1),
        }, 90), null, new Rect(0, 0, W, H));

        // Low harvest sun / moon on the horizon.
        var sun = new Point(1380, 700);
        dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x90, 0xFF, 0xC0, 0x5A), 0), new(Color.FromArgb(0x00, 0xFF, 0x9A, 0x2A), 1),
        }), null, sun, 520, 420);
        dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0xFF, 0xE6, 0xA8), 0), new(Color.FromRgb(0xFF, 0xB4, 0x4A), 1),
        }), null, sun, 150, 150);

        // Birds heading south.
        var pen = new Pen(Br("#1A0C10", 0.85), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        foreach (var (x, y, s) in new[] { (520.0, 260.0, 1.3), (610.0, 300.0, 1.0), (700.0, 245.0, 1.1), (430.0, 330.0, 0.8), (780.0, 330.0, 0.9), (1100.0, 220.0, 1.0), (1180.0, 260.0, 0.8) })
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x - 22 * s, y + 4 * s), false, false);
                c.QuadraticBezierTo(new Point(x - 10 * s, y - 14 * s), new Point(x, y), true, false);
                c.QuadraticBezierTo(new Point(x + 10 * s, y - 14 * s), new Point(x + 22 * s, y + 4 * s), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }

        // Hills of harvest fields.
        Hill(dc, "#6B3418", 800, 70, 0.0032, 0.9);
        Hill(dc, "#4B2412", 880, 60, 0.0048, 2.4);
        Hill(dc, "#2C150C", 960, 45, 0.0066, 4.1);

        // Bare autumn tree on the left, with leaves clinging to it.
        Tree(dc, new Point(260, 1000), -90, 250, 24, 0, rnd);
        var leafColors = new[] { "#C4492B", "#E2A12D", "#B5651D", "#8F2D1B", "#D98324" };
        for (int i = 0; i < 90; i++)
            Leaf(dc, new Point(60 + rnd.NextDouble() * 560, 170 + rnd.NextDouble() * 480), 6 + rnd.NextDouble() * 6, rnd.NextDouble() * 360, leafColors[rnd.Next(leafColors.Length)], 0.9);

        // Leaves drifting down across the whole window.
        for (int i = 0; i < 70; i++)
            Leaf(dc, new Point(rnd.NextDouble() * W, rnd.NextDouble() * (H - 120)), 7 + rnd.NextDouble() * 11, rnd.NextDouble() * 360, leafColors[rnd.Next(leafColors.Length)], 0.55 + rnd.NextDouble() * 0.4);

        // Pumpkins and a corn-stalk fence of golden wheat.
        for (int i = 0; i < 70; i++)
        {
            double x = 20 + i * 27 + rnd.Next(-6, 6), y = 1000 + rnd.Next(-10, 10), h = 70 + rnd.Next(0, 40);
            var stalk = new Pen(Br("#C9902F", 0.85), 3) { StartLineCap = PenLineCap.Round };
            stalk.Freeze();
            dc.DrawLine(stalk, new Point(x, y), new Point(x + rnd.Next(-8, 9), y - h));
            dc.DrawEllipse(Br("#E2B04A", 0.9), null, new Point(x + rnd.Next(-8, 9), y - h - 6), 4, 11);
        }
        HalloweenScene.DrawPumpkin(dc, new Point(1250, 960), 1.2, false);
        HalloweenScene.DrawPumpkin(dc, new Point(1520, 990), 0.85, false);
        HalloweenScene.DrawPumpkin(dc, new Point(1740, 955), 1.5, false);
        HalloweenScene.DrawPumpkin(dc, new Point(1000, 1005), 0.7, false);
        dc.DrawRectangle(Br("#1E0F08"), null, new Rect(0, 1035, W, 45));
    }

    private static void Tree(DrawingContext dc, Point from, double angle, double len, double width, int depth, Random rnd)
    {
        if (depth > 6 || len < 18) return;
        double rad = angle * Math.PI / 180;
        var to = new Point(from.X + Math.Cos(rad) * len, from.Y + Math.Sin(rad) * len);
        var pen = new Pen(Br("#1A0C08"), Math.Max(2, width)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawLine(pen, from, to);
        int kids = depth < 2 ? 3 : 2;
        for (int i = 0; i < kids; i++)
        {
            double spread = (i - (kids - 1) / 2.0) * (28 + rnd.Next(0, 18));
            Tree(dc, to, angle + spread + rnd.Next(-8, 9), len * (0.68 + rnd.NextDouble() * 0.08), width * 0.64, depth + 1, rnd);
        }
    }

    private static readonly Geometry LeafShape = MakeLeaf();
    private static Geometry MakeLeaf()
    {
        var g = Geometry.Parse("M0,-14 C9,-8 9,6 0,14 C-9,6 -9,-8 0,-14 Z");
        g.Freeze();
        return g;
    }

    private static void Leaf(DrawingContext dc, Point at, double size, double rot, string color, double opacity)
    {
        double k = size / 14.0;
        dc.PushTransform(new TransformGroup { Children = { new ScaleTransform(k, k), new RotateTransform(rot), new TranslateTransform(at.X, at.Y) } });
        dc.DrawGeometry(Br(color, opacity), null, LeafShape);
        dc.Pop();
    }

    // ---------------------------------------------------------------- Christmas

    private static void DrawChristmas(DrawingContext dc)
    {
        var rnd = new Random(12);
        dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0x05, 0x0D, 0x16), 0), new(Color.FromRgb(0x0B, 0x24, 0x34), 0.6), new(Color.FromRgb(0x1D, 0x4A, 0x58), 1),
        }, 90), null, new Rect(0, 0, W, H));

        for (int i = 0; i < 170; i++)
        {
            double r = 0.8 + rnd.NextDouble() * 1.6;
            dc.DrawEllipse(Br("#F4FBF6", 0.3 + rnd.NextDouble() * 0.6), null, new Point(rnd.NextDouble() * W, rnd.NextDouble() * H * 0.62), r, r);
        }

        // Cold white moon.
        var moon = new Point(1560, 230);
        dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x70, 0xCC, 0xE8, 0xFF), 0), new(Color.FromArgb(0x00, 0xCC, 0xE8, 0xFF), 1),
        }), null, moon, 340, 340);
        dc.DrawEllipse(Br("#F2F8FF"), null, moon, 110, 110);
        dc.DrawEllipse(Br("#D3E2F2", 0.6), null, new Point(moon.X - 30, moon.Y - 20), 20, 20);
        dc.DrawEllipse(Br("#D3E2F2", 0.6), null, new Point(moon.X + 28, moon.Y + 30), 26, 26);

        // Snowy hills.
        Hill(dc, "#C9DBE8", 860, 70, 0.0034, 0.4);
        Hill(dc, "#E6F0F7", 940, 55, 0.005, 2.0);
        Hill(dc, "#F6FAFD", 1010, 30, 0.007, 3.6);

        // A cosy cabin with a glowing window.
        var cabin = new Point(1300, 905);
        dc.DrawRectangle(Br("#4A2E1E"), null, new Rect(cabin.X, cabin.Y, 260, 130));
        var roof = new StreamGeometry();
        using (var c = roof.Open()) { c.BeginFigure(new Point(cabin.X - 24, cabin.Y + 4), true, true); c.LineTo(new Point(cabin.X + 130, cabin.Y - 90), true, false); c.LineTo(new Point(cabin.X + 284, cabin.Y + 4), true, false); }
        roof.Freeze();
        dc.DrawGeometry(Br("#F4F8FC"), null, roof);
        dc.DrawRectangle(Br("#6A3B2A"), null, new Rect(cabin.X + 200, cabin.Y - 100, 28, 60));
        dc.DrawRectangle(Br("#F4F8FC"), null, new Rect(cabin.X + 196, cabin.Y - 108, 36, 12));
        dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x80, 0xFF, 0xC8, 0x5A), 0), new(Color.FromArgb(0x00, 0xFF, 0xC8, 0x5A), 1),
        }), null, new Point(cabin.X + 70, cabin.Y + 60), 130, 100);
        dc.DrawRectangle(Br("#FFD27A"), null, new Rect(cabin.X + 40, cabin.Y + 36, 60, 50));
        dc.DrawRectangle(Br("#4A2E1E"), null, new Rect(cabin.X + 68, cabin.Y + 36, 4, 50));
        dc.DrawRectangle(Br("#4A2E1E"), null, new Rect(cabin.X + 40, cabin.Y + 58, 60, 4));
        dc.DrawRectangle(Br("#2E1A10"), null, new Rect(cabin.X + 160, cabin.Y + 46, 50, 84));

        // Decorated pine trees, front and back.
        Pine(dc, new Point(260, 1020), 760, 1.0, rnd, true);
        Pine(dc, new Point(640, 1000), 480, 0.8, rnd, false);
        Pine(dc, new Point(930, 990), 330, 0.7, rnd, false);
        Pine(dc, new Point(1740, 1010), 560, 0.9, rnd, true);
        Pine(dc, new Point(1950, 1000), 420, 0.8, rnd, false);

        // Snowfall: dots and a few six-armed flakes.
        for (int i = 0; i < 240; i++)
        {
            double r = 1.2 + rnd.NextDouble() * 3.2;
            dc.DrawEllipse(Br("#FFFFFF", 0.35 + rnd.NextDouble() * 0.55), null, new Point(rnd.NextDouble() * W, rnd.NextDouble() * H), r, r);
        }
        for (int i = 0; i < 18; i++) Flake(dc, new Point(rnd.NextDouble() * W, rnd.NextDouble() * (H - 200)), 10 + rnd.NextDouble() * 16, 0.5 + rnd.NextDouble() * 0.4);
    }

    private static void Pine(DrawingContext dc, Point baseCenter, double height, double scale, Random rnd, bool lights)
    {
        double trunkH = height * 0.07, w = height * 0.46;
        dc.DrawRectangle(Br("#3A2417"), null, new Rect(baseCenter.X - 14 * scale, baseCenter.Y - trunkH, 28 * scale, trunkH));
        string[] greens = { "#0B3B25", "#0F4A2E", "#145A37" };
        int tiers = 5;
        var rim = Br("#F4F8FC", 0.9);
        for (int t = 0; t < tiers; t++)
        {
            double tierH = height * 0.30, topY = baseCenter.Y - trunkH - (height - trunkH) * (t + 1) / tiers - tierH * 0.15;
            double tierW = w * (1 - t * 0.17), botY = topY + tierH;
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(baseCenter.X, topY), true, true); c.LineTo(new Point(baseCenter.X + tierW / 2, botY), true, false); c.LineTo(new Point(baseCenter.X - tierW / 2, botY), true, false); }
            g.Freeze();
            dc.DrawGeometry(Br(greens[t % 3]), null, g);
            // Snow along the lower edge of the tier.
            dc.DrawRoundedRectangle(rim, null, new Rect(baseCenter.X - tierW / 2 + 4, botY - 6, tierW - 8, 8), 4, 4);
        }
        double topPoint = baseCenter.Y - height + 4;
        if (lights)
        {
            string[] bulbs = { "#FF4B4B", "#FFD84A", "#4BC8FF", "#7CFF7A", "#FF8AE0" };
            for (int t = 0; t < tiers; t++)
            {
                double tierH = height * 0.30, topY = baseCenter.Y - trunkH - (height - trunkH) * (t + 1) / tiers - tierH * 0.15;
                double tierW = w * (1 - t * 0.17), botY = topY + tierH;
                for (int k = 0; k < 6; k++)
                {
                    double f = 0.25 + k * 0.14 + rnd.NextDouble() * 0.04;
                    double y = topY + tierH * (0.45 + 0.5 * (k % 2 == 0 ? 0.85 : 0.55)) * 1.0;
                    double half = tierW / 2 * ((y - topY) / tierH);
                    double x = baseCenter.X - half + 2 * half * ((k + 0.5) / 6.0);
                    string color = bulbs[(t + k) % bulbs.Length];
                    dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection { new(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), 0), new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1) }), null, new Point(x, y), 14 * scale, 14 * scale);
                    dc.DrawEllipse(Br(color), null, new Point(x, y), 5.5 * scale, 5.5 * scale);
                    _ = f;
                }
            }
            // Star on top.
            var star = StarGeometry(new Point(baseCenter.X, topPoint - 10), 26 * scale, 11 * scale);
            dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection { new(Color.FromArgb(0xA0, 0xFF, 0xE0, 0x7A), 0), new(Color.FromArgb(0x00, 0xFF, 0xE0, 0x7A), 1) }), null, new Point(baseCenter.X, topPoint - 10), 60 * scale, 60 * scale);
            dc.DrawGeometry(Br("#FFE070"), null, star);
        }
    }

    private static Geometry StarGeometry(Point c, double outer, double inner)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            for (int i = 0; i < 10; i++)
            {
                double r = i % 2 == 0 ? outer : inner, a = -Math.PI / 2 + i * Math.PI / 5;
                var p = new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
                if (i == 0) s.BeginFigure(p, true, true); else s.LineTo(p, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    private static void Flake(DrawingContext dc, Point c, double r, double opacity)
    {
        var pen = new Pen(Br("#FFFFFF", opacity), 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        for (int i = 0; i < 3; i++)
        {
            double a = i * Math.PI / 3;
            dc.DrawLine(pen, new Point(c.X - Math.Cos(a) * r, c.Y - Math.Sin(a) * r), new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r));
        }
    }
}

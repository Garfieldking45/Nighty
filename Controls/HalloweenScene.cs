using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nighty.Controls;

/// <summary>The built-in picture behind the window for the Halloween theme: moon, bats, a dead tree, a cobweb and glowing pumpkins.</summary>
public static class HalloweenScene
{
    internal const int W = 1920, H = 1080;
    private static BitmapSource? _cached;

    public static BitmapSource Get() => _cached ??= Render();

    internal static SolidColorBrush Br(string hex, double a = 1)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static BitmapSource Render()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var rnd = new Random(31);

            // Night sky: deep violet fading to a smoky orange glow at the horizon.
            dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x0C, 0x07, 0x10), 0), new(Color.FromRgb(0x24, 0x10, 0x3A), 0.62), new(Color.FromRgb(0x5A, 0x22, 0x18), 1),
            }, 90), null, new Rect(0, 0, W, H));

            // Stars.
            for (int i = 0; i < 140; i++)
            {
                double r = 0.8 + rnd.NextDouble() * 1.4;
                dc.DrawEllipse(Br("#FFF1E3", 0.25 + rnd.NextDouble() * 0.5), null, new Point(rnd.NextDouble() * W, rnd.NextDouble() * H * 0.6), r, r);
            }

            // Moon with a soft halo.
            var moon = new Point(1480, 250);
            dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(0x70, 0xFF, 0xB0, 0x5A), 0), new(Color.FromArgb(0x00, 0xFF, 0x8A, 0x2A), 1),
            }), null, moon, 380, 380);
            dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0xFF, 0xE9, 0xB8), 0), new(Color.FromRgb(0xFF, 0xC8, 0x7A), 1),
            }), null, moon, 130, 130);
            foreach (var (dx, dy, r) in new[] { (-40.0, -30.0, 22.0), (35.0, 25.0, 30.0), (-10.0, 55.0, 14.0), (50.0, -45.0, 12.0) })
                dc.DrawEllipse(Br("#E9A85B", 0.35), null, new Point(moon.X + dx, moon.Y + dy), r, r);

            // Thin clouds crossing the moon.
            for (int i = 0; i < 4; i++)
            {
                double y = 200 + i * 38, x = 1250 + rnd.Next(-60, 80);
                dc.DrawRoundedRectangle(Br("#1A0C2B", 0.55), null, new Rect(x, y, 420 + rnd.Next(0, 120), 16), 8, 8);
            }

            // Rolling hills.
            DrawHill(dc, "#120A1C", 880, 90, 0.0035, 0.5);
            DrawHill(dc, "#0B0612", 950, 70, 0.0052, 2.1);

            // Dead tree on the left.
            Branch(dc, new Point(230, 1000), -90, 240, 26, 0, rnd);

            // Graveyard crosses far back.
            foreach (var x in new[] { 760.0, 840.0, 1010.0 })
            {
                dc.DrawRectangle(Br("#07040B"), null, new Rect(x, 880, 10, 70));
                dc.DrawRectangle(Br("#07040B"), null, new Rect(x - 17, 898, 44, 10));
            }

            // Pumpkins in the foreground.
            DrawPumpkin(dc, new Point(1290, 960), 1.25);
            DrawPumpkin(dc, new Point(1560, 990), 0.9);
            DrawPumpkin(dc, new Point(1735, 950), 1.5);
            DrawPumpkin(dc, new Point(1050, 1005), 0.7);

            // Ground.
            dc.DrawRectangle(Br("#06030A"), null, new Rect(0, 1030, W, 60));

            // Bats.
            var bat = Geometry.Parse("M0,0 C-8,-10 -22,-12 -34,-4 C-28,-4 -26,2 -22,6 C-18,2 -12,2 -8,6 C-5,3 -2,3 0,8 C2,3 5,3 8,6 C12,2 18,2 22,6 C26,2 28,-4 34,-4 C22,-12 8,-10 0,0 Z");
            bat.Freeze();
            foreach (var (x, y, s, rot) in new[] { (620.0, 210.0, 1.7, -12.0), (880.0, 330.0, 1.1, 8.0), (1120.0, 150.0, 1.4, 14.0), (420.0, 420.0, 0.9, -6.0), (1700.0, 520.0, 1.2, 10.0), (1010.0, 520.0, 0.8, -10.0) })
            {
                dc.PushTransform(new TransformGroup { Children = { new ScaleTransform(s, s), new RotateTransform(rot), new TranslateTransform(x, y) } });
                dc.DrawGeometry(Br("#05030A", 0.92), null, bat);
                dc.Pop();
            }

            DrawWeb(dc, new Point(0, 0), 420);
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void DrawHill(DrawingContext dc, string color, double baseY, double amp, double freq, double phase)
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

    private static void Branch(DrawingContext dc, Point from, double angle, double len, double width, int depth, Random rnd)
    {
        if (depth > 6 || len < 18) return;
        double rad = angle * Math.PI / 180;
        var to = new Point(from.X + Math.Cos(rad) * len, from.Y + Math.Sin(rad) * len);
        var pen = new Pen(Br("#07040B"), Math.Max(2, width)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawLine(pen, from, to);
        int kids = depth < 2 ? 3 : 2;
        for (int i = 0; i < kids; i++)
        {
            double spread = (i - (kids - 1) / 2.0) * (28 + rnd.Next(0, 18));
            Branch(dc, to, angle + spread + rnd.Next(-8, 9), len * (0.68 + rnd.NextDouble() * 0.08), width * 0.64, depth + 1, rnd);
        }
    }

    private static void DrawWeb(DrawingContext dc, Point corner, double size)
    {
        var pen = new Pen(Br("#D9C7F0", 0.28), 1.4);
        pen.Freeze();
        int spokes = 6;
        for (int i = 0; i <= spokes; i++)
        {
            double a = i * (Math.PI / 2) / spokes;
            dc.DrawLine(pen, corner, new Point(corner.X + Math.Cos(a) * size, corner.Y + Math.Sin(a) * size));
        }
        for (int ring = 1; ring <= 6; ring++)
        {
            double r = size * ring / 6.0;
            for (int i = 0; i < spokes; i++)
            {
                double a1 = i * (Math.PI / 2) / spokes, a2 = (i + 1) * (Math.PI / 2) / spokes, am = (a1 + a2) / 2;
                var p1 = new Point(corner.X + Math.Cos(a1) * r, corner.Y + Math.Sin(a1) * r);
                var p2 = new Point(corner.X + Math.Cos(a2) * r, corner.Y + Math.Sin(a2) * r);
                var mid = new Point(corner.X + Math.Cos(am) * r * 0.9, corner.Y + Math.Sin(am) * r * 0.9);   // sagging threads
                var g = new StreamGeometry();
                using (var c = g.Open()) { c.BeginFigure(p1, false, false); c.QuadraticBezierTo(mid, p2, true, false); }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }
        // A little spider dangling from the web.
        var thread = new Pen(Br("#D9C7F0", 0.4), 1.2);
        dc.DrawLine(thread, new Point(190, 40), new Point(190, 205));
        dc.DrawEllipse(Br("#07040B"), null, new Point(190, 220), 13, 15);
        dc.DrawEllipse(Br("#07040B"), null, new Point(190, 203), 8, 8);
        var leg = new Pen(Br("#07040B"), 2.2);
        for (int s = -1; s <= 1; s += 2)
            for (int i = 0; i < 4; i++)
                dc.DrawLine(leg, new Point(190, 218), new Point(190 + s * (26 + i * 3), 200 + i * 14));
    }

    internal static void DrawPumpkin(DrawingContext dc, Point c, double s, bool carved = true)
    {
        // Glow on the ground.
        dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x60, 0xFF, 0x7A, 0x1A), 0), new(Color.FromArgb(0x00, 0xFF, 0x7A, 0x1A), 1),
        }), null, c, 150 * s, 110 * s);
        var body = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromRgb(0xFF, 0xA0, 0x3A), 0), new(Color.FromRgb(0xE2, 0x5A, 0x08), 0.75), new(Color.FromRgb(0x8E, 0x2C, 0x04), 1),
        });
        dc.DrawEllipse(body, null, new Point(c.X - 52 * s, c.Y), 62 * s, 72 * s);
        dc.DrawEllipse(body, null, new Point(c.X + 52 * s, c.Y), 62 * s, 72 * s);
        dc.DrawEllipse(body, null, new Point(c.X - 24 * s, c.Y), 62 * s, 78 * s);
        dc.DrawEllipse(body, null, new Point(c.X + 24 * s, c.Y), 62 * s, 78 * s);
        // Stem.
        dc.DrawRoundedRectangle(Br("#3E5A1E"), null, new Rect(c.X - 8 * s, c.Y - 98 * s, 16 * s, 28 * s), 4 * s, 4 * s);
        if (!carved) return;
        // Carved glowing face.
        var glow = Br("#FFD25A");
        dc.DrawGeometry(glow, null, Tri(c.X - 46 * s, c.Y - 22 * s, c.X - 14 * s, c.Y - 22 * s, c.X - 30 * s, c.Y - 52 * s));
        dc.DrawGeometry(glow, null, Tri(c.X + 14 * s, c.Y - 22 * s, c.X + 46 * s, c.Y - 22 * s, c.X + 30 * s, c.Y - 52 * s));
        var mouth = new StreamGeometry();
        using (var g = mouth.Open())
        {
            g.BeginFigure(new Point(c.X - 52 * s, c.Y + 10 * s), true, true);
            g.LineTo(new Point(c.X - 28 * s, c.Y + 36 * s), true, false);
            g.LineTo(new Point(c.X - 14 * s, c.Y + 22 * s), true, false);
            g.LineTo(new Point(c.X, c.Y + 40 * s), true, false);
            g.LineTo(new Point(c.X + 14 * s, c.Y + 22 * s), true, false);
            g.LineTo(new Point(c.X + 28 * s, c.Y + 36 * s), true, false);
            g.LineTo(new Point(c.X + 52 * s, c.Y + 10 * s), true, false);
            g.LineTo(new Point(c.X + 30 * s, c.Y + 52 * s), true, false);
            g.LineTo(new Point(c.X - 30 * s, c.Y + 52 * s), true, false);
        }
        mouth.Freeze();
        dc.DrawGeometry(glow, null, mouth);
    }

    private static Geometry Tri(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(new Point(x1, y1), true, true); c.LineTo(new Point(x2, y2), true, false); c.LineTo(new Point(x3, y3), true, false); }
        g.Freeze();
        return g;
    }
}

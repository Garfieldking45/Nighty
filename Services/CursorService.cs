using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nighty.Models;

namespace Nighty.Services;

/// <summary>Draws cursors (layers on a 64 x 64 grid) into bitmaps, adjusts brightness, and writes PNG / .cur files.</summary>
public static class CursorRenderer
{
    public const int Grid = 64;

    public static BitmapSource Render(CursorSpec spec, int px, double brightness = 0, bool checker = false)
    {
        if (spec.Kind == CursorKind.Image) return RenderImage(spec, px, brightness, checker);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (checker) DrawChecker(dc, px);
            dc.PushTransform(new ScaleTransform(px / (double)Grid, px / (double)Grid));
            foreach (var layer in spec.Layers) DrawLayer(dc, layer);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return brightness == 0 ? rtb : Brighten(rtb, brightness);
    }

    private static BitmapSource RenderImage(CursorSpec spec, int px, double brightness, bool checker)
    {
        var path = Path.Combine(CursorLibrary.Folder, spec.ImageFile ?? "");
        BitmapSource? src = null;
        try
        {
            if (File.Exists(path))
            {
                var b = new BitmapImage();
                b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(path); b.EndInit(); b.Freeze();
                src = b;
            }
        }
        catch { }
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            if (checker) DrawChecker(dc, px);
            if (src != null) dc.DrawImage(src, new Rect(0, 0, px, px));
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv); rtb.Freeze();
        return brightness == 0 ? rtb : Brighten(rtb, brightness);
    }

    private static void DrawChecker(DrawingContext dc, int px)
    {
        var a = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x38)); var b = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x26));
        double s = Math.Max(4, px / 8.0);
        for (int y = 0; y * s < px; y++)
            for (int x = 0; x * s < px; x++)
                dc.DrawRectangle((x + y) % 2 == 0 ? a : b, null, new Rect(x * s, y * s, s, s));
    }

    private static Color C(string hex, double opacity = 100)
    {
        var c = ThemeService.TryParse(hex, out var v) ? v : Colors.White;
        return Color.FromArgb((byte)Math.Clamp(opacity / 100 * 255, 0, 255), c.R, c.G, c.B);
    }

    private static void DrawLayer(DrawingContext dc, CursorLayer l)
    {
        double cx = Grid / 2.0 + l.OffsetX, cy = Grid / 2.0 + l.OffsetY;
        var geo = ShapeGeometry(l, cx, cy, out bool strokeOnly);
        if (geo == null) return;
        dc.PushOpacity(l.Opacity / 100);
        var fill = new SolidColorBrush(C(l.Color));
        double t = l.Thickness;

        // soft glow: the shape again and again, a little wider and fainter each time
        if (l.GlowSize > 0)
        {
            var g = C(l.GlowColor);
            int steps = (int)Math.Ceiling(l.GlowSize);
            for (int i = steps; i >= 1; i--)
            {
                var gb = new SolidColorBrush(Color.FromArgb((byte)(70.0 / steps * (steps - i + 1) * 0.9), g.R, g.G, g.B));
                dc.DrawGeometry(null, new Pen(gb, (strokeOnly ? t : 0) + i * 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
            }
        }
        if (l.Outline && l.OutlineWidth > 0)
        {
            var ob = new SolidColorBrush(C(l.OutlineColor));
            dc.DrawGeometry(null, new Pen(ob, (strokeOnly ? t : 0) + l.OutlineWidth * 2) { LineJoin = PenLineJoin.Miter }, geo);
        }
        if (strokeOnly) dc.DrawGeometry(null, new Pen(fill, t) { LineJoin = PenLineJoin.Miter }, geo);
        else dc.DrawGeometry(fill, null, geo);
        dc.Pop();
    }

    /// <summary>The shape as geometry. <paramref name="strokeOnly"/> means it is a line drawing (ring, outlined shapes).</summary>
    private static Geometry? ShapeGeometry(CursorLayer l, double cx, double cy, out bool strokeOnly)
    {
        strokeOnly = false;
        double L = l.Size, t = l.Thickness, half = L / 2;
        Geometry g;
        double extraRot = 0;
        switch (l.Shape)
        {
            case CursorShape.Plus:
            case CursorShape.Split:
            case CursorShape.X:
            {
                double gap = l.Shape == CursorShape.Split ? Math.Max(l.Gap, 3) : l.Gap;
                var grp = new GeometryGroup { FillRule = FillRule.Nonzero };
                if (gap <= 0)
                {
                    grp.Children.Add(new RectangleGeometry(new Rect(cx - half, cy - t / 2, L, t)));
                    grp.Children.Add(new RectangleGeometry(new Rect(cx - t / 2, cy - half, t, L)));
                }
                else
                {
                    double len = Math.Max(1, half - gap);
                    grp.Children.Add(new RectangleGeometry(new Rect(cx - half, cy - t / 2, len, t)));
                    grp.Children.Add(new RectangleGeometry(new Rect(cx + gap, cy - t / 2, len, t)));
                    grp.Children.Add(new RectangleGeometry(new Rect(cx - t / 2, cy - half, t, len)));
                    grp.Children.Add(new RectangleGeometry(new Rect(cx - t / 2, cy + gap, t, len)));
                }
                g = grp;
                if (l.Shape == CursorShape.X) extraRot = 45;
                break;
            }
            case CursorShape.Dot: g = new EllipseGeometry(new Point(cx, cy), half, half); break;
            case CursorShape.Ring: g = new EllipseGeometry(new Point(cx, cy), Math.Max(1, half - t / 2), Math.Max(1, half - t / 2)); strokeOnly = true; break;
            case CursorShape.Square:
                g = new RectangleGeometry(new Rect(cx - half, cy - half, L, L)); strokeOnly = !l.Filled; break;
            case CursorShape.Diamond:
                g = new RectangleGeometry(new Rect(cx - half * 0.72, cy - half * 0.72, L * 0.72, L * 0.72)); extraRot = 45; strokeOnly = !l.Filled; break;
            case CursorShape.Triangle:
                g = Poly(new[] { new Point(cx, cy - half), new Point(cx + half, cy + half * 0.8), new Point(cx - half, cy + half * 0.8) }); strokeOnly = !l.Filled; break;
            case CursorShape.Star:
            {
                var pts = new List<Point>();
                for (int i = 0; i < 10; i++)
                {
                    double ang = -Math.PI / 2 + i * Math.PI / 5, r = i % 2 == 0 ? half : half * 0.42;
                    pts.Add(new Point(cx + Math.Cos(ang) * r, cy + Math.Sin(ang) * r));
                }
                g = Poly(pts.ToArray()); strokeOnly = !l.Filled; break;
            }
            case CursorShape.Arrow:
            {
                // classic pointer, top-left aligned inside its box
                double x0 = cx - half, y0 = cy - half, s = L;
                g = Poly(new[]
                {
                    new Point(x0, y0), new Point(x0, y0 + s * 0.86), new Point(x0 + s * 0.22, y0 + s * 0.66), new Point(x0 + s * 0.40, y0 + s),
                    new Point(x0 + s * 0.56, y0 + s * 0.92), new Point(x0 + s * 0.38, y0 + s * 0.58), new Point(x0 + s * 0.68, y0 + s * 0.58),
                });
                break;
            }
            case CursorShape.Line:
                g = new RectangleGeometry(new Rect(cx - half, cy - t / 2, L, t)); break;
            default: return null;
        }
        double rot = l.Rotation + extraRot;
        if (rot != 0)
        {
            var clone = g.Clone();
            clone.Transform = new RotateTransform(rot, cx, cy);
            g = clone;
        }
        g.Freeze();
        return g;
    }

    private static Geometry Poly(Point[] pts)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts.Skip(1).ToList(), true, true);
        }
        return sg;
    }

    /// <summary>-100 (darker) to +100 (brighter). Alpha is kept.</summary>
    public static BitmapSource Brighten(BitmapSource src, double amount)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var data = new byte[stride * h];
        bgra.CopyPixels(data, stride, 0);
        double k = amount / 100.0;
        for (int i = 0; i < data.Length; i += 4)
            for (int c = 0; c < 3; c++)
            {
                double v = data[i + c];
                data[i + c] = (byte)Math.Clamp(k >= 0 ? v + (255 - v) * k : v * (1 + k), 0, 255);
            }
        var res = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, data, stride);
        res.Freeze();
        return res;
    }

    public static byte[] ToPng(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A Windows .cur file holding PNG frames of every size (Vista and later read these).</summary>
    public static byte[] ToCur(IReadOnlyDictionary<int, byte[]> pngBySize, bool centerHotspot)
    {
        var sizes = pngBySize.Keys.OrderBy(s => s).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)sizes.Count);
        int offset = 6 + 16 * sizes.Count;
        foreach (var s in sizes)
        {
            var png = pngBySize[s];
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)(centerHotspot ? s / 2 : 0)); w.Write((ushort)(centerHotspot ? s / 2 : 0));
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var s in sizes) w.Write(pngBySize[s]);
        return ms.ToArray();
    }
}

/// <summary>The cursor library: built-in designs plus everything saved in %AppData%\Nighty\cursors.</summary>
public static class CursorLibrary
{
    public static string Folder => Path.Combine(AppPaths.Root, "cursors");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static CursorLayer L(CursorShape s, string color, double size = 36, double thickness = 4, double gap = 0, string? glow = null, double glowSize = 0, bool outline = true, string outlineColor = "#000000", double rot = 0)
        => new() { Shape = s, Color = color, Size = size, Thickness = thickness, Gap = gap, GlowColor = glow ?? color, GlowSize = glowSize, Outline = outline, OutlineColor = outlineColor, Rotation = rot };

    private static CursorSpec B(string id, string name, string desc, params CursorLayer[] layers)
        => new() { Id = "builtin-" + id, Name = name, Description = desc, IsBuiltIn = true, Layers = layers.ToList() };

    public static IReadOnlyList<CursorSpec> BuiltIn { get; } = new[]
    {
        B("ice-cross", "Ice Cross", "Bright plus with a blue outline", L(CursorShape.Plus, "#F2FAFF", 38, 4, outlineColor: "#3B82F6")),
        B("violet-glow", "Violet Glow", "White dot with a soft violet halo", L(CursorShape.Dot, "#FFFFFF", 12, glow: "#8B5CF6", glowSize: 10, outline: false)),
        B("green-crosshair", "Green Crosshair", "Split crosshair with a dark edge", L(CursorShape.Split, "#22E27A", 40, 3, 5)),
        B("red-square", "Red Square", "Solid red block, no outline", L(CursorShape.Square, "#EF4444", 14, outline: false)),
        B("classic-plus", "Classic Plus", "Small white plus, black edge", L(CursorShape.Plus, "#FFFFFF", 28, 3)),
        B("violet-plus", "Violet Plus", "Ice Cross shape in violet", L(CursorShape.Plus, "#C4B5FD", 38, 4, outlineColor: "#6D28D9")),
        B("halo-ring", "Halo Ring", "Thin cyan ring around a center dot", L(CursorShape.Ring, "#22D3EE", 34, 3, outline: false), L(CursorShape.Dot, "#FFFFFF", 6)),
        B("amber-dot", "Amber Dot", "Round amber dot, dark edge", L(CursorShape.Dot, "#F59E0B", 14)),
        B("rose-x", "Rose X", "Diagonal cross in rose", L(CursorShape.X, "#FB7185", 34, 4)),
        B("ember-glow", "Ember Glow", "White dot with a warm orange halo", L(CursorShape.Dot, "#FFFFFF", 11, glow: "#FB923C", glowSize: 11, outline: false)),
        B("mint-gap", "Mint Gap", "Long split crosshair with a center dot", L(CursorShape.Split, "#6EE7B7", 52, 3, 7), L(CursorShape.Dot, "#6EE7B7", 4, outline: false)),
        B("frost-diamond", "Frost Diamond", "Pale diamond with a blue edge", L(CursorShape.Diamond, "#E0F2FE", 30, outlineColor: "#2563EB")),
        B("neon-ring", "Neon Ring", "Glowing cyan ring", L(CursorShape.Ring, "#67E8F9", 32, 3, glow: "#22D3EE", glowSize: 8, outline: false)),
        B("pinpoint", "Pinpoint", "Tiny white dot for precise aim", L(CursorShape.Dot, "#FFFFFF", 6)),
        new CursorSpec { Id = "builtin-arrow", Name = "Arrow", Description = "A clean pointer", IsBuiltIn = true, CenterHotspot = false,
            Layers = new List<CursorLayer> { L(CursorShape.Arrow, "#FFFFFF", 46, outlineColor: "#111111") } },
    };

    public static List<CursorSpec> LoadUser()
    {
        var list = new List<CursorSpec>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var f in Directory.EnumerateFiles(Folder, "*.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                try { var s = JsonSerializer.Deserialize<CursorSpec>(File.ReadAllText(f), Json); if (s != null && s.Id.Length > 0) list.Add(s); }
                catch (Exception ex) { Log.Warn("Cursor file unreadable: " + f, ex); }
            }
        }
        catch (Exception ex) { Log.Warn("Listing cursors failed", ex); }
        return list;
    }

    public static IEnumerable<CursorSpec> All() => BuiltIn.Concat(LoadUser());
    public static CursorSpec? Find(string id) => string.IsNullOrEmpty(id) ? null : All().FirstOrDefault(c => c.Id == id);

    public static void Save(CursorSpec spec)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, spec.Id + ".json"), JsonSerializer.Serialize(spec, Json));
    }

    public static void Delete(CursorSpec spec)
    {
        if (spec.IsBuiltIn) return;
        try { File.Delete(Path.Combine(Folder, spec.Id + ".json")); } catch { }
        if (spec.ImageFile != null) { try { File.Delete(Path.Combine(Folder, spec.ImageFile)); } catch { } }
    }
}

public sealed record ImageCursorOptions(bool RemoveBackground = true, double Strength = 45, bool KeepInside = true, bool RemoveSpecks = true,
    bool CleanEdges = true, bool PixelArt = false, double Sharpen = 0);

/// <summary>Turns any picture into a clean, transparent, square cursor image: removes a solid background, tidies the edges, enlarges and sharpens.</summary>
public static class ImageCursorProcessor
{
    public sealed record Result(BitmapSource Image, string Notes);

    public static BitmapSource Load(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; bmp.UriSource = new Uri(path); bmp.EndInit(); bmp.Freeze();
        return bmp;
    }

    public static Result Process(BitmapSource source, ImageCursorOptions o, int size)
    {
        var notes = new List<string>();
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);

        long semi = 0;
        for (int i = 3; i < px.Length; i += 4) if (px[i] < 250) semi++;
        bool hasAlpha = semi > (long)w * h / 100;

        if (hasAlpha) notes.Add("Kept its own transparency");
        else if (o.RemoveBackground)
        {
            int removed = RemoveBackground(px, w, h, o);
            notes.Add($"Background {(removed * 100.0 / (w * h)):0}% removed");
        }
        if (o.RemoveSpecks) { int n = RemoveSpecks(px, w, h); if (n > 0) notes.Add($"{n} stray pixels cleaned"); }

        // crop to what is left, then fit into a square canvas
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] > 8) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        if (maxX < 0) { minX = 0; minY = 0; maxX = w - 1; maxY = h - 1; }
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        var cropped = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        var crop = new CroppedBitmap(cropped, new Int32Rect(minX, minY, cw, ch));

        double scale = Math.Min((size - 2.0) / cw, (size - 2.0) / ch);
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, o.PixelArt ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            double dw = cw * scale, dh = ch * scale;
            dc.DrawImage(crop, new Rect((size - dw) / 2, (size - dh) / 2, dw, dh));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        notes.Add(scale > 1.01 ? $"Upscaled {cw}x{ch} to {size} px{(o.PixelArt ? " (pixel art)" : "")}" : $"Resized to {size} px");

        BitmapSource result = rtb;
        if (o.Sharpen > 0) { result = Sharpen(rtb, o.Sharpen / 100.0); notes.Add("Sharpened"); }
        result.Freeze();
        return new Result(result, string.Join(". ", notes));
    }

    private static int RemoveBackground(byte[] px, int w, int h, ImageCursorOptions o)
    {
        int stride = w * 4;
        // background colour: the most common colour among the border pixels (coarsely binned)
        var counts = new Dictionary<int, (int N, long R, long G, long B)>();
        void Sample(int x, int y)
        {
            int i = y * stride + x * 4;
            int key = (px[i + 2] >> 4) << 8 | (px[i + 1] >> 4) << 4 | (px[i] >> 4);
            counts.TryGetValue(key, out var c);
            counts[key] = (c.N + 1, c.R + px[i + 2], c.G + px[i + 1], c.B + px[i]);
        }
        for (int x = 0; x < w; x++) { Sample(x, 0); Sample(x, h - 1); }
        for (int y = 0; y < h; y++) { Sample(0, y); Sample(w - 1, y); }
        var best = counts.OrderByDescending(k => k.Value.N).First().Value;
        double br = best.R / (double)best.N, bg = best.G / (double)best.N, bb = best.B / (double)best.N;

        double tol = 10 + o.Strength / 100.0 * 110;   // colour distance that still counts as background
        bool IsBg(int i, double t) { double dr = px[i + 2] - br, dg = px[i + 1] - bg, db = px[i] - bb; return Math.Sqrt(dr * dr + dg * dg + db * db) <= t; }

        var gone = new bool[w * h];
        int removed = 0;
        if (o.KeepInside)
        {
            // flood from the border so holes inside the picture (the middle of a ring) stay
            var q = new Queue<int>();
            void Seed(int x, int y) { int p = y * w + x; if (!gone[p] && IsBg(y * stride + x * 4, tol)) { gone[p] = true; q.Enqueue(p); } }
            for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
            for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }
            while (q.Count > 0)
            {
                int p = q.Dequeue(); int x = p % w, y = p / w;
                if (x > 0) Seed(x - 1, y); if (x < w - 1) Seed(x + 1, y); if (y > 0) Seed(x, y - 1); if (y < h - 1) Seed(x, y + 1);
            }
        }
        else
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (IsBg(y * stride + x * 4, tol)) gone[y * w + x] = true;

        if (o.CleanEdges)
        {
            // second pass: pixels touching the removed area that are still close to the background colour are its leftover outline
            double t2 = tol * 1.7;
            for (int pass = 0; pass < 2; pass++)
            {
                var more = new List<int>();
                for (int y = 1; y < h - 1; y++)
                    for (int x = 1; x < w - 1; x++)
                    {
                        int p = y * w + x;
                        if (gone[p]) continue;
                        if ((gone[p - 1] || gone[p + 1] || gone[p - w] || gone[p + w]) && IsBg(y * stride + x * 4, t2)) more.Add(p);
                    }
                foreach (var p in more) gone[p] = true;
            }
        }
        for (int p = 0; p < gone.Length; p++) if (gone[p]) { px[p * 4 + 3] = 0; px[p * 4] = px[p * 4 + 1] = px[p * 4 + 2] = 0; removed++; }
        return removed;
    }

    /// <summary>Removes small isolated blobs of opaque pixels (dust left after background removal).</summary>
    private static int RemoveSpecks(byte[] px, int w, int h)
    {
        int stride = w * 4;
        var seen = new bool[w * h];
        int limit = Math.Max(6, w * h / 600), removed = 0;
        var comp = new List<int>();
        var q = new Queue<int>();
        for (int s = 0; s < w * h; s++)
        {
            if (seen[s] || px[s * 4 + 3] < 40) continue;
            comp.Clear(); q.Enqueue(s); seen[s] = true;
            while (q.Count > 0)
            {
                int p = q.Dequeue(); comp.Add(p); int x = p % w, y = p / w;
                void N(int nx, int ny) { if (nx < 0 || ny < 0 || nx >= w || ny >= h) return; int np = ny * w + nx; if (!seen[np] && px[np * 4 + 3] >= 40) { seen[np] = true; q.Enqueue(np); } }
                N(x - 1, y); N(x + 1, y); N(x, y - 1); N(x, y + 1);
            }
            if (comp.Count < limit) { foreach (var p in comp) px[p * 4 + 3] = 0; removed += comp.Count; }
        }
        return removed;
    }

    private static BitmapSource Sharpen(BitmapSource src, double amount)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var s = new byte[stride * h]; bgra.CopyPixels(s, stride, 0);
        var d = (byte[])s.Clone();
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * stride + x * 4;
                if (s[i + 3] == 0) continue;
                for (int c = 0; c < 3; c++)
                {
                    double blur = (s[i + c - 4] + s[i + c + 4] + s[i + c - stride] + s[i + c + stride]) / 4.0;
                    d[i + c] = (byte)Math.Clamp(s[i + c] + (s[i + c] - blur) * amount * 1.5, 0, 255);
                }
            }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, d, stride);
    }
}

/// <summary>Runs work that draws with WPF visuals on its own STA thread, so heavy image processing never blocks the UI.</summary>
public static class Sta
{
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Nighty image worker" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}

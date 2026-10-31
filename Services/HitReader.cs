using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nighty.Native;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Nighty.Services;

/// <summary>
/// Reads a number (the game's hit counter) from a rectangle of the screen with Windows' built-in text recognition. Nothing leaves the PC:
/// the rectangle is copied, cleaned up (greyscale, stretched contrast, enlarged, padded), recognised locally, and only the digits are kept.
/// </summary>
internal static class HitReader
{
    /// <summary>A rectangle in physical screen pixels.</summary>
    public readonly record struct Region(int X, int Y, int W, int H)
    {
        public bool IsEmpty => W < 2 || H < 2;
        public override string ToString() => $"{W}×{H} at ({X}, {Y})";
    }

    /// <summary>One read: the number (null if none was found), the raw recognised text, and the captured pixels for a preview.</summary>
    public sealed record Reading(int? Value, string Text, byte[] Bgra, int Width, int Height, string? Problem = null, Region? Found = null);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static OcrEngine? _engine;
    private static OcrEngine? Engine() =>
        _engine ??= OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));

    public static string NoEngineMessage =>
        "Windows has no text-recognition language installed. Add English in Settings > Time & language > Language & region (with \"Optical character recognition\" ticked).";

    /// <summary>Copies the region off the screen and reads it.</summary>
    public static async Task<Reading> ReadAsync(Region r)
    {
        if (r.IsEmpty) return new Reading(null, "", Array.Empty<byte>(), 0, 0, "No area selected.");
        byte[] px;
        try
        {
            using var grab = new ScreenGrabber(r.W, r.H);
            if (!grab.Grab(r.X, r.Y)) return new Reading(null, "", Array.Empty<byte>(), r.W, r.H, "Could not copy that part of the screen.");
            px = (byte[])grab.Pixels.Clone();
        }
        catch (Exception ex) { return new Reading(null, "", Array.Empty<byte>(), r.W, r.H, "Screen copy failed: " + ex.Message); }
        return await ReadPixelsAsync(px, r.W, r.H);
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    /// <summary>One recognised word with its box (pixels of the image it came from).</summary>
    public readonly record struct WordBox(string Text, double X, double Y, double W, double H);

    private static Reading Fail(string problem) => new(null, "", Array.Empty<byte>(), 0, 0, problem);

    /// <summary>
    /// Reads a whole window (even one that is covered by other windows) and returns the number sitting next to <paramref name="keyword"/>,
    /// for example the 38 in "Personal Best Hits 38". No box to draw, and it follows the game if its layout moves.
    /// </summary>
    public static async Task<Reading> ReadWindowAsync(IntPtr hwnd, string keyword)
    {
        if (hwnd == IntPtr.Zero) return Fail("The game window was not found. Is Roblox running?");
        if (string.IsNullOrWhiteSpace(keyword)) return Fail("Type the word that sits next to the number (for example Hits).");
        if (IsIconic(hwnd)) return Fail("The game window is minimised, so there is nothing to read.");
        var engine = Engine();
        if (engine == null) return Fail(NoEngineMessage);

        GetWindowRect(hwnd, out var rc);
        int w = rc.R - rc.L, h = rc.B - rc.T;
        if (w < 16 || h < 16 || w > 9000 || h > 9000) return Fail("The game window has an odd size.");
        byte[] px;
        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 2); } finally { g.ReleaseHdc(hdc); }   // 2 = also capture hardware-drawn content
            }
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            px = new byte[w * h * 4];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
        }
        catch (Exception ex) { return Fail("Could not copy the game window: " + ex.Message); }
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;

        var words = new List<WordBox>();
        await Gate.WaitAsync();
        try
        {
            using var dw = new DataWriter();
            dw.WriteBytes(px);
            var sb = SoftwareBitmap.CreateCopyFromBuffer(dw.DetachBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(sb);
            foreach (var line in result.Lines)
                foreach (var word in line.Words)
                    words.Add(new WordBox(word.Text, word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height));
        }
        finally { Gate.Release(); }

        var hit = FindNumberBeside(words, keyword);
        if (hit is { } f)
        {
            // Preview: just the neighbourhood of the words that were used.
            int x0 = Math.Max(0, (int)f.Box.X - 24), y0 = Math.Max(0, (int)f.Box.Y - 16);
            int x1 = Math.Min(w, (int)(f.Box.X + f.Box.W) + 24), y1 = Math.Min(h, (int)(f.Box.Y + f.Box.H) + 16);
            int cw = Math.Max(1, x1 - x0), ch = Math.Max(1, y1 - y0);
            var crop = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++) System.Buffer.BlockCopy(px, ((y0 + y) * w + x0) * 4, crop, y * cw * 4, cw * 4);
            return new Reading(f.Value, f.Text, crop, cw, ch, null, new Region(rc.L + x0, rc.T + y0, cw, ch));
        }
        // Not found: a small picture of the whole window, so it is clear what was looked at.
        int step = Math.Max(1, (int)Math.Ceiling(w / 800.0));
        int pw = w / step, ph = h / step;
        var small = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
                System.Buffer.BlockCopy(px, ((y * step) * w + x * step) * 4, small, (y * pw + x) * 4, 4);
        string seen = words.Count == 0 ? "(no text found in the window)" : string.Join(" ", words.Take(40).Select(x => x.Text));
        return new Reading(null, seen, small, pw, ph, $"Could not find a number next to \"{keyword.Trim()}\" in the game window.");
    }

    /// <summary>
    /// The number beside the keyword: a number inside the keyword's own word ("Hits:38"), else the nearest number on the same row, preferring
    /// the one to the right ("Personal Best Hits   38"). The keyword may be off by one letter, as text recognition often is. Null if none.
    /// </summary>
    public static (int Value, string Text, (double X, double Y, double W, double H) Box)? FindNumberBeside(IReadOnlyList<WordBox> words, string keyword)
    {
        static string Letters(string t) => new string(t.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        string key = Letters(keyword);
        if (key.Length == 0) return null;
        bool Matches(string t)
        {
            string n = Letters(t);
            if (n.Length == 0) return false;
            if (n == key) return true;
            return key.Length >= 4 && Math.Abs(n.Length - key.Length) <= 1 && Distance(n, key) <= 1;
        }
        (int Value, string Text, (double X, double Y, double W, double H) Box)? best = null;
        double bestDist = double.MaxValue;
        foreach (var kw in words.Where(x => Matches(x.Text)))
        {
            if (ParseHits(kw.Text) is int inside && kw.Text.Any(char.IsDigit))
                return (inside, kw.Text, (kw.X, kw.Y, kw.W, kw.H));
            foreach (var c in words)
            {
                if (c.Equals(kw) || !c.Text.Any(char.IsDigit) || ParseHits(c.Text) is not int v) continue;
                double rowTolerance = Math.Max(kw.H, c.H) * 0.75;
                if (Math.Abs((c.Y + c.H / 2) - (kw.Y + kw.H / 2)) > rowTolerance) continue;
                double dx = c.X - (kw.X + kw.W);
                if (dx >= -4 && dx > kw.H * 20) continue;                          // too far to the right to belong to it
                double dist = dx >= -4 ? Math.Max(0, dx) : Math.Abs(dx) + 5000;    // right of the word first, then left
                if (dist < bestDist)
                {
                    bestDist = dist;
                    double x0 = Math.Min(kw.X, c.X), y0 = Math.Min(kw.Y, c.Y), x1 = Math.Max(kw.X + kw.W, c.X + c.W), y1 = Math.Max(kw.Y + kw.H, c.Y + c.H);
                    best = (v, $"{kw.Text} {c.Text}", (x0, y0, x1 - x0, y1 - y0));
                }
            }
        }
        return best;
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Reads the number in a BGRA image: several sizes, both ways round, plus repeated copies, with a vote.</summary>
    public static async Task<Reading> ReadPixelsAsync(byte[] bgra, int w, int h)
    {
        var engine = Engine();
        if (engine == null) return new Reading(null, "", bgra, w, h, NoEngineMessage);
        // Windows' recogniser is touchy: the same digits can read at one enlargement and come back empty at the next, and a lone narrow "1"
        // (as in 31 or 41) is often dropped. So the image is tried at several sizes, both ways round; the plain image first, then two and
        // three copies side by side (the repetition gives the recogniser the context it wants). The number most attempts agree on wins,
        // and two matching attempts end it early.
        var votes = new Dictionary<int, int>();
        var seen = new List<string>();
        // One read at a time: the engine is a single shared object and gives empty answers when two threads use it together.
        await Gate.WaitAsync();
        try
        {
        var (cropped, cw, ch) = AutoCrop(bgra, w, h);   // a box drawn by hand is never tight: trim the empty background around the digits
        foreach (var (src, sw, sh, target) in Attempts(cropped, cw, ch))
        {
            foreach (bool invert in new[] { false, true })
            {
                var prepared = Prepare(src, sw, sh, invert, out int pw, out int ph, target);
                using var dw = new DataWriter();
                dw.WriteBytes(prepared);
                var bmp = SoftwareBitmap.CreateCopyFromBuffer(dw.DetachBuffer(), BitmapPixelFormat.Bgra8, pw, ph, BitmapAlphaMode.Premultiplied);
                var result = await engine.RecognizeAsync(bmp);
                string text = (result.Text ?? "").Trim();
                if (text.Length > 0) seen.Add(text);
                if (ParseHits(text) is int v)
                {
                    votes[v] = votes.GetValueOrDefault(v) + 1;
                    if (votes[v] >= 2) return new Reading(v, text, bgra, w, h);
                }
            }
        }
        }
        finally { Gate.Release(); }
        if (votes.Count > 0)
        {
            int best = votes.OrderByDescending(kv => kv.Value).First().Key;
            return new Reading(best, seen.FirstOrDefault() ?? "", bgra, w, h);
        }
        return new Reading(null, string.Join(" | ", seen.Distinct()), bgra, w, h);
    }

    /// <summary>The images to try, in order: plain, then 2 and 3 copies side by side at the main size, then the plain image at other sizes.</summary>
    private static IEnumerable<(byte[] Px, int W, int H, double Target)> Attempts(byte[] bgra, int w, int h)
    {
        yield return (bgra, w, h, Targets[0]);
        foreach (int copies in new[] { 2, 3 })
        {
            var tiled = Tile(bgra, w, h, copies, Math.Max(4, w / 2), out int tw);
            yield return (tiled, tw, h, Targets[0]);
        }
        for (int i = 1; i < Targets.Length; i++) yield return (bgra, w, h, Targets[i]);
    }

    /// <summary>
    /// Trims the empty background around whatever is drawn in the image (pixels clearly different from the border colour), keeping a margin
    /// of about a third of the text height. Returns the image unchanged when nothing stands out or when trimming would save little.
    /// </summary>
    internal static (byte[] Px, int W, int H) AutoCrop(byte[] bgra, int w, int h)
    {
        if (w < 8 || h < 8) return (bgra, w, h);
        var gray = new int[w * h];
        for (int i = 0; i < w * h; i++) gray[i] = (bgra[i * 4 + 2] * 77 + bgra[i * 4 + 1] * 150 + bgra[i * 4] * 29) >> 8;
        var border = new List<int>(2 * (w + h));
        for (int x = 0; x < w; x++) { border.Add(gray[x]); border.Add(gray[(h - 1) * w + x]); }
        for (int y = 0; y < h; y++) { border.Add(gray[y * w]); border.Add(gray[y * w + w - 1]); }
        border.Sort();
        int bg = border[border.Count / 2];
        int minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (Math.Abs(gray[y * w + x] - bg) > 40)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < 0) return (bgra, w, h);
        int margin = Math.Max(4, (maxY - minY + 1) / 3);
        int x0 = Math.Max(0, minX - margin), x1 = Math.Min(w - 1, maxX + margin), y0 = Math.Max(0, minY - margin), y1 = Math.Min(h - 1, maxY + margin);
        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        if ((long)cw * ch >= (long)w * h * 85 / 100) return (bgra, w, h);
        var o = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++) System.Buffer.BlockCopy(bgra, ((y0 + y) * w + x0) * 4, o, y * cw * 4, cw * 4);
        return (o, cw, ch);
    }

    /// <summary>Puts <paramref name="copies"/> copies of the image side by side, separated by a gap filled with the top-left pixel colour.</summary>
    internal static byte[] Tile(byte[] px, int w, int h, int copies, int gap, out int outW)
    {
        outW = w * copies + gap * (copies - 1);
        var o = new byte[outW * h * 4];
        for (int i = 0; i < o.Length; i += 4) { o[i] = px[0]; o[i + 1] = px[1]; o[i + 2] = px[2]; o[i + 3] = 255; }
        for (int c = 0; c < copies; c++)
            for (int y = 0; y < h; y++)
                System.Buffer.BlockCopy(px, y * w * 4, o, (y * outW + c * (w + gap)) * 4, w * 4);
        return o;
    }

    /// <summary>Enlargement targets (character-area height in pixels) tried in turn.</summary>
    private static readonly double[] Targets = { 90, 60, 200, 130, 40 };

    /// <summary>
    /// First whole number in the text (1 to 4 characters). Inside a number the recogniser's look-alikes are accepted (O or ø for 0, l or I
    /// for 1), but a token must contain at least one real digit, so a label such as "Hits" is never turned into a number.
    /// </summary>
    public static int? ParseHits(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z0-9])[0-9OoøØIl|]{1,4}(?![A-Za-z0-9])"))
        {
            if (!m.Value.Any(char.IsDigit)) continue;
            var digits = m.Value.Replace('O', '0').Replace('o', '0').Replace('ø', '0').Replace('Ø', '0').Replace('l', '1').Replace('I', '1').Replace('|', '1');
            if (int.TryParse(digits, out int v)) return v;
        }
        return null;
    }

    /// <summary>
    /// Several reads taken while the counter settles: the last number seen twice in a row, else the last number seen at all.
    /// A counter that is still ticking up gives its final value; a stray misread in the middle is ignored.
    /// </summary>
    public static int? Settle(IReadOnlyList<int?> reads)
    {
        int? last = null, stable = null;
        int? prev = null;
        foreach (var v in reads)
        {
            if (v != null) { last = v; if (v == prev) stable = v; }
            prev = v;
        }
        return stable ?? last;
    }

    /// <summary>Greyscale, stretch the contrast, enlarge small text, add a margin, optionally invert. Returns BGRA.</summary>
    internal static byte[] Prepare(byte[] bgra, int w, int h, bool invert, out int outW, out int outH, double targetHeight = 90, int pad = 24)
    {
        var gray = new byte[w * h];
        int lo = 255, hi = 0;
        for (int i = 0; i < w * h; i++)
        {
            int g = (bgra[i * 4 + 2] * 77 + bgra[i * 4 + 1] * 150 + bgra[i * 4] * 29) >> 8;
            gray[i] = (byte)g;
            if (g < lo) lo = g;
            if (g > hi) hi = g;
        }
        if (hi - lo >= 20)
            for (int i = 0; i < gray.Length; i++) gray[i] = (byte)((gray[i] - lo) * 255 / (hi - lo));

        // Recognition works best when the characters are tall; small text is enlarged (bilinear), big text is left alone.
        int scale = Math.Clamp((int)Math.Ceiling(targetHeight / Math.Max(1, h)), 1, 6);
        while (scale > 1 && w * scale > 3000) scale--;
        outW = w * scale + pad * 2; outH = h * scale + pad * 2;
        byte corner = (byte)((gray[0] + gray[w - 1] + gray[(h - 1) * w] + gray[h * w - 1]) / 4);
        var output = new byte[outW * outH * 4];
        for (int y = 0; y < outH; y++)
        {
            for (int x = 0; x < outW; x++)
            {
                int sx = x - pad, sy = y - pad;
                byte v;
                if (sx < 0 || sy < 0 || sx >= w * scale || sy >= h * scale) v = corner;
                else
                {
                    double fx = (sx + 0.5) / scale - 0.5, fy = (sy + 0.5) / scale - 0.5;
                    int x0 = Math.Clamp((int)Math.Floor(fx), 0, w - 1), y0 = Math.Clamp((int)Math.Floor(fy), 0, h - 1);
                    int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
                    double tx = Math.Clamp(fx - x0, 0, 1), ty = Math.Clamp(fy - y0, 0, 1);
                    double top = gray[y0 * w + x0] * (1 - tx) + gray[y0 * w + x1] * tx;
                    double bot = gray[y1 * w + x0] * (1 - tx) + gray[y1 * w + x1] * tx;
                    v = (byte)Math.Round(top * (1 - ty) + bot * ty);
                }
                if (invert) v = (byte)(255 - v);
                int o = (y * outW + x) * 4;
                output[o] = output[o + 1] = output[o + 2] = v;
                output[o + 3] = 255;
            }
        }
        return output;
    }
}

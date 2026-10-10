using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Auto fish: casts by holding the mouse for a moment, then watches the screen for the fishing bar. The GREEN box is
/// yours; the target is the thin line (it changes colour: cyan, red, yellow, blue, grey...). The mouse is held and
/// released to keep the green box over the line until the fish is caught, then it waits a second and casts again.
/// Only screen pixels are read and only mouse input is sent; nothing touches the game's memory.
/// </summary>
public sealed class FishingService
{
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts != null;
    public string Status { get; private set; } = "Off";
    public event Action? Changed;

    // ---------- session stats ----------
    private readonly int[] _counts = new int[5];
    public int Total => _counts.Sum();
    public string TrackerText =>
        $"Fish caught: {Total}\nCommon {_counts[(int)FishRarity.Common]} · Blue {_counts[(int)FishRarity.Blue]} · Special {_counts[(int)FishRarity.Special]} · Gold {_counts[(int)FishRarity.Gold]} · Emerald {_counts[(int)FishRarity.Emerald]}";
    public void ResetStats() { Array.Clear(_counts); Changed?.Invoke(); }

    /// <summary>Colour of the target line to rarity: grey common, red special, yellow gold, green emerald, cyan/blue blue.</summary>
    public static FishRarity Classify(int r, int g, int b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 50) return FishRarity.Unknown;
        double sat = (max - min) / (double)max;
        if (sat < 0.22) return FishRarity.Common;
        double d = max - min, hue;
        if (max == r) hue = 60 * (((g - b) / d) % 6);
        else if (max == g) hue = 60 * ((b - r) / d + 2);
        else hue = 60 * ((r - g) / d + 4);
        if (hue < 0) hue += 360;
        if (hue < 20 || hue >= 340) return FishRarity.Special;
        if (hue < 70) return FishRarity.Gold;
        if (hue < 170) return FishRarity.Emerald;
        if (hue < 260) return FishRarity.Blue;
        return FishRarity.Unknown;
    }

    public void Toggle() { if (IsRunning) Stop(); else Start(); }

    public void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        SetStatus("Starting…");
        new Thread(() => Run(cts.Token)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Nighty fishing" }.Start();
    }

    public void Stop() { _cts?.Cancel(); }

    private void SetStatus(string s) { if (Status == s) return; Status = s; Changed?.Invoke(); }

    // The green of the player's box (sampled from the game). The thin progress bar under it is a slightly different green.
    private static bool IsGreen(byte r, byte g, byte b) => Math.Abs(r - 99) < 26 && Math.Abs(g - 235) < 22 && Math.Abs(b - 158) < 28;
    private static int Diff(byte[] p, int a, int b) => Math.Abs(p[a] - p[b]) + Math.Abs(p[a + 1] - p[b + 1]) + Math.Abs(p[a + 2] - p[b + 2]);

    private const int Side = 12;          // how far left/right of a pixel we compare, to tell a thin line from flat areas
    private const int LineDiff = 70;      // colour distance from both sides for "this pixel is a line"
    private const int SideDiff = 70;      // ...while both sides look alike (same background on each side)

    private void Run(CancellationToken ct)
    {
        NativeMethods.timeBeginPeriod(1);
        int sw = ScreenGrabber.GetSystemMetrics(0), sh = ScreenGrabber.GetSystemMetrics(1);
        int bandTop = (int)(sh * 0.2), bandH = (int)(sh * 0.75);
        using var band = new ScreenGrabber(sw, bandH);
        bool holding = false;
        void Hold(bool down)
        {
            if (down == holding) return;
            holding = down;
            InputSender.MouseButton(ClickButton.Left, down);
        }
        try
        {
            long lastBar = Wait.Now, lastCast = 0;
            while (!ct.IsCancellationRequested)
            {
                var s = Svc.S.Fishing;
                if (!Svc.Roblox.IsForeground || RobloxService.IsOwnWindowForeground()) { Hold(false); SetStatus("Waiting for Roblox"); Wait.Ms(150, ct); continue; }

                if (FindBox(band, bandTop, out int boxTop, out int boxBottom))
                {
                    SetStatus("Reeling");
                    var res = Reel(boxTop, boxBottom, sw, s, ct, Hold);
                    Hold(false);
                    if (res.Skipped)
                    {
                        SetStatus($"Skipped a {res.Rarity} fish");
                        InputSender.Key(0x20, true); Wait.Ms(30, ct); InputSender.Key(0x20, false);   // one jump dismisses the fish
                        Wait.Ms(400, ct);
                    }
                    else if (res.Rarity != FishRarity.Unknown && res.ReeledMs > 300)
                    {
                        _counts[(int)res.Rarity]++;
                        Changed?.Invoke();
                    }
                    lastBar = Wait.Now;
                    lastCast = 0;   // caught (or lost): after the pause below, cast again right away
                    SetStatus("Waiting a second before the next cast");
                    continue;
                }

                // No bar: after a one second pause cast, then wait for a bite (recast if nothing happens for a while).
                double idleMs = (Wait.Now - lastBar) / (double)Wait.FromMs(1);
                double sinceCast = lastCast == 0 ? double.MaxValue : (Wait.Now - lastCast) / (double)Wait.FromMs(1);
                if (idleMs > 1000 && sinceCast > 20000)
                {
                    SetStatus("Casting");
                    Hold(true); Wait.Ms(s.CastHoldMs, ct); Hold(false);
                    lastCast = Wait.Now;
                    SetStatus("Waiting for a bite");
                }
                else if (sinceCast == double.MaxValue && idleMs <= 1000) SetStatus("Waiting a second before the next cast");
                Wait.Ms(40, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Fishing failed", ex); }
        finally
        {
            Hold(false);
            NativeMethods.timeEndPeriod(1);
            _cts = null;
            SetStatus("Off");
        }
    }

    /// <summary>Finds the player's green box: a block of green rows 24-100 px tall. Returns its screen rows.</summary>
    private static bool FindBox(ScreenGrabber band, int top, out int boxTop, out int boxBottom)
    {
        boxTop = boxBottom = 0;
        if (!band.Grab(0, top)) return false;
        var px = band.Pixels;
        int w = band.Width, h = band.Height;
        int runStart = -1, bestStart = -1, bestLen = 0;
        for (int y = 0; y <= h; y += 2)
        {
            int greens = 0;
            if (y < h)
            {
                int off = y * w * 4;
                for (int x = 0; x < w; x += 2) { int i = off + x * 4; if (IsGreen(px[i + 2], px[i + 1], px[i])) greens++; }
            }
            if (greens >= 15) { if (runStart < 0) runStart = y; }
            else if (runStart >= 0)
            {
                int len = y - runStart;
                if (len >= 24 && len <= 100 && len > bestLen) { bestLen = len; bestStart = runStart; }
                runStart = -1;
            }
        }
        if (bestStart < 0) return false;
        boxTop = top + bestStart;
        boxBottom = top + bestStart + bestLen;
        return true;
    }

    private readonly record struct ReelResult(FishRarity Rarity, bool Skipped, double ReeledMs);

    private ReelResult Reel(int boxTop, int boxBottom, int sw, FishingSettings s, CancellationToken ct, Action<bool> hold)
    {
        var votes = new int[6];
        int voted = 0;
        FishRarity rarity = FishRarity.Unknown;
        long startedAt = Wait.Now;
        var rs = new List<int>(16); var gs = new List<int>(16); var bs = new List<int>(16);
        // A strip a little taller than the box: the line sticks out above and below it.
        int stripTop = Math.Max(0, boxTop - 8), stripH = boxBottom - boxTop + 16;
        using var strip = new ScreenGrabber(sw, stripH);
        int lost = 0;
        var hist = new Queue<(long T, double X)>();
        bool wantRight = false;
        long next = Wait.Now;
        var colScore = new int[sw];
        var rows = new int[Math.Min(9, stripH)];
        for (int i = 0; i < rows.Length; i++) rows[i] = (int)((i + 0.5) * stripH / rows.Length);

        while (!ct.IsCancellationRequested && lost < 100)
        {
            Wait.Until(next, ct);
            next += Wait.FromMs(2);
            if (!Svc.Roblox.IsForeground) { hold(false); break; }
            if (!strip.Grab(0, stripTop)) { lost++; continue; }
            var px = strip.Pixels;

            // Box: green extent on the middle row.
            int midOff = (stripH / 2) * sw * 4, gMin = int.MaxValue, gMax = -1;
            for (int x = 0; x < sw; x++) { int i = midOff + x * 4; if (IsGreen(px[i + 2], px[i + 1], px[i])) { if (x < gMin) gMin = x; if (x > gMax) gMax = x; } }
            if (gMax - gMin < 20) { lost++; continue; }

            // Target line: thin vertical element that differs from both sides on most sampled rows.
            Array.Clear(colScore, 0, colScore.Length);
            foreach (int ry in rows)
            {
                int off = ry * sw * 4;
                for (int x = Side; x < sw - Side; x++)
                {
                    int i = off + x * 4, l = i - Side * 4, r = i + Side * 4;
                    if (IsGreen(px[i + 2], px[i + 1], px[i])) continue;
                    int dl = Diff(px, i, l), dr = Diff(px, i, r);
                    if (dl > LineDiff && dr > LineDiff && Diff(px, l, r) < SideDiff) colScore[x] += Math.Min(dl, dr);
                }
            }
            int need = rows.Length * 6 / 10, bestSum = 0, bestC = -1;
            for (int x = Side; x < sw - Side;)
            {
                if (colScore[x] <= 0) { x++; continue; }
                int start = x, sum = 0, cnt = 0, weighted = 0, gap = 0, last = x;
                while (x < sw - Side && gap <= 2)
                {
                    if (colScore[x] > 0) { sum += colScore[x]; weighted += x * colScore[x]; cnt++; last = x; gap = 0; } else gap++;
                    x++;
                }
                int width = last - start + 1;
                if (width <= 40 && cnt >= 2 && sum > bestSum && sum >= need * LineDiff) { bestSum = sum; bestC = weighted / sum; }
            }
            if (bestC < 0) { lost++; continue; }
            lost = 0;

            // Rarity: the line's colour, voted over the first few frames.
            if (voted < 8)
            {
                rs.Clear(); gs.Clear(); bs.Clear();
                foreach (int ry in rows)
                {
                    int i = ry * sw * 4 + bestC * 4;
                    if (IsGreen(px[i + 2], px[i + 1], px[i]) && Diff(px, i, i - Side * 4) < 40) continue;
                    rs.Add(px[i + 2]); gs.Add(px[i + 1]); bs.Add(px[i]);
                }
                if (rs.Count >= 3)
                {
                    rs.Sort(); gs.Sort(); bs.Sort();
                    votes[(int)Classify(rs[rs.Count / 2], gs[gs.Count / 2], bs[bs.Count / 2])]++;
                    voted++;
                    if (voted == 8)
                    {
                        int best = 0; for (int k = 1; k < votes.Length; k++) if (votes[k] > votes[best]) best = k;
                        rarity = (FishRarity)best;
                        if (s.ShouldSkip(rarity)) { hold(false); return new ReelResult(rarity, true, 0); }
                    }
                }
            }

            double boxC = (gMin + gMax) / 2.0;
            long now = Wait.Now;
            // Box speed over a ~60 ms window. The game draws ~60 frames a second, so comparing consecutive 2 ms reads
            // gives zero most of the time and huge spikes otherwise; a longer window is steady.
            hist.Enqueue((now, boxC));
            while (hist.Count > 1 && (now - hist.Peek().T) > Wait.FromMs(90)) hist.Dequeue();
            double boxV = 0;
            var oldest = hist.Peek();
            double span = (now - oldest.T) / (double)Wait.FromMs(1000);
            if (span >= 0.04) boxV = (boxC - oldest.X) / span;

            // Where the box will be shortly versus where the line is. Inside a small dead zone keep doing what we were
            // doing, so it doesn't flutter on and off around the line.
            double err = bestC - (boxC + boxV * 0.04);
            if (Math.Abs(err) > 3) wantRight = err > 0;
            hold(s.HoldMovesRight ? wantRight : !wantRight);
            string kind = rarity == FishRarity.Unknown ? "" : $"{rarity} fish — ";
            SetStatus(kind + (Math.Abs(bestC - boxC) < (gMax - gMin) / 2.0 ? "on target" : "chasing the line"));
        }
        return new ReelResult(rarity, false, (Wait.Now - startedAt) / (double)Wait.FromMs(1));
    }
}

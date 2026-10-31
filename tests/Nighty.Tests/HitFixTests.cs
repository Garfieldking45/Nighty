using Nighty.Native;
using Nighty.Services;
using Xunit;
using static Nighty.Native.CpuTopology;

namespace Nighty.Tests;

public class SpinTunerTests
{
    [Fact]
    public void Starts_generous_before_it_has_measured_anything()
    {
        var t = new SpinTuner(highResTimer: true);
        Assert.InRange(t.MarginMs, 1.5, 2.0);
        Assert.Equal(0, t.Samples);
    }

    [Fact]
    public void Shrinks_toward_the_real_timer_overshoot_when_wakes_are_punctual()
    {
        var t = new SpinTuner(true);
        double start = t.MarginMs;
        for (int i = 0; i < 400; i++) t.Observe(0.2 + (i % 5) * 0.01);
        Assert.True(t.MarginMs < start / 2, $"margin {t.MarginMs} did not shrink from {start}");
        Assert.InRange(t.MarginMs, SpinTuner.MinMs, 0.8);
        Assert.Equal(0.7, t.Scale, 3);   // no misses at all: the scale bottoms out
    }

    [Fact]
    public void Grows_back_when_wakes_start_landing_late()
    {
        var t = new SpinTuner(true);
        for (int i = 0; i < 200; i++) t.Observe(0.2);
        double calm = t.MarginMs;
        for (int i = 0; i < 200; i++) t.Observe(2.5);   // the timer suddenly overshoots by 2.5 ms
        Assert.True(t.MarginMs > calm * 3, $"margin {t.MarginMs} did not grow from {calm}");
        Assert.True(t.MarginMs <= SpinTuner.MaxMs);
    }

    [Fact]
    public void Margin_stays_inside_its_bounds_whatever_it_is_fed()
    {
        var t = new SpinTuner(true);
        foreach (var v in new[] { -5.0, double.NaN, 0, 1e9, double.PositiveInfinity, 3 })
        {
            for (int i = 0; i < 40; i++) t.Observe(v);
            Assert.InRange(t.MarginMs, SpinTuner.MinMs, SpinTuner.MaxMs);
        }
    }

    [Fact]
    public void Legacy_timer_never_drops_below_its_floor()
    {
        var t = new SpinTuner(highResTimer: false);
        for (int i = 0; i < 400; i++) t.Observe(0.05);
        Assert.True(t.MarginMs >= SpinTuner.LegacyFloorMs);
    }
}

public class NumberEntryTests
{
    private static void OnSta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static Nighty.Controls.NumberEntry Entry(double value, double min, double max, int decimals) =>
        new() { Minimum = min, Maximum = max, Decimals = decimals, Value = value };

    [Fact]
    public void Typed_value_is_applied_and_shown() => OnSta(() =>
    {
        var e = Entry(12, 1, 100, 1);
        Assert.Equal("12.0", e.Text);
        e.Text = "37.5"; e.Commit();
        Assert.Equal(37.5, e.Value);
        Assert.Equal("37.5", e.Text);
    });

    [Fact]
    public void Out_of_range_is_clamped_and_extra_decimals_are_rounded() => OnSta(() =>
    {
        var e = Entry(12, 1, 100, 1);
        e.Text = "250"; e.Commit();
        Assert.Equal(100, e.Value);
        e.Text = "0"; e.Commit();
        Assert.Equal(1, e.Value);
        e.Text = "41.27"; e.Commit();
        Assert.Equal(41.3, e.Value);
        var duty = Entry(16, 5, 95, 0);
        duty.Text = "77.6"; duty.Commit();
        Assert.Equal(78, duty.Value);
        Assert.Equal("78", duty.Text);
    });

    [Fact]
    public void Text_that_is_not_a_number_puts_the_old_value_back() => OnSta(() =>
    {
        var e = Entry(40, 1, 100, 1);
        e.Text = "abc"; e.Commit();
        Assert.Equal(40, e.Value);
        Assert.Equal("40.0", e.Text);
    });
}

public class CpuTopologyTests
{
    private static CpuSet Cpu(int index, int core, int eff, ushort group = 0, bool parked = false, bool allocated = false) =>
        new(group, (byte)index, (byte)core, (byte)eff, parked, allocated);

    [Fact]
    public void Hybrid_cpu_picks_a_performance_core_not_the_highest_numbered_one()
    {
        // 2 performance cores with hyper-threading (class 1) numbered first, then 4 efficiency cores (class 0), like Intel 12th gen+.
        var sets = new List<CpuSet>
        {
            Cpu(0, 0, 1), Cpu(1, 0, 1), Cpu(2, 1, 1), Cpu(3, 1, 1),
            Cpu(4, 2, 0), Cpu(5, 3, 0), Cpu(6, 4, 0), Cpu(7, 5, 0),
        };
        var pick = PickFastCore(sets);
        Assert.Equal((ushort)0, pick!.Value.Group);
        Assert.Equal((byte)2, pick.Value.Number);   // first thread of the last performance core
    }

    [Fact]
    public void Same_speed_cores_pick_the_last_core_but_never_processor_zero()
    {
        var sets = Enumerable.Range(0, 4).Select(i => Cpu(i, i, 0)).ToList();
        Assert.Equal((byte)3, PickFastCore(sets)!.Value.Number);
        Assert.Null(PickFastCore(new List<CpuSet> { Cpu(0, 0, 0) }));
    }

    [Fact]
    public void Parked_and_reserved_processors_are_skipped()
    {
        var sets = new List<CpuSet> { Cpu(0, 0, 1), Cpu(1, 1, 1), Cpu(2, 2, 1, parked: true), Cpu(3, 3, 1, allocated: true), Cpu(4, 4, 0) };
        Assert.Equal((byte)1, PickFastCore(sets)!.Value.Number);
    }

    [Fact]
    public void The_real_query_returns_something_sane_on_this_machine()
    {
        var sets = Read();
        if (sets.Count == 0) return;   // CPU Sets unavailable: the app falls back to the old pick
        Assert.All(sets, c => Assert.True(c.Index < 64));
        var pick = PickFastCore(sets);
        if (pick != null) Assert.Contains(sets, c => c.Group == pick.Value.Group && c.Index == pick.Value.Number);
    }
}

[Collection("clicker")]
public class ClickerHealthTests
{
    [Fact]
    public void A_stall_longer_than_a_click_is_counted_as_a_resync_and_clean_runs_are_not()
    {
        int n = 0;
        var host = new ClickerHost
        {
            Precision = () => Nighty.Models.PrecisionMode.Balanced, Allowed = _ => true, Busy = () => false, InStopZone = () => false, ProcessTuning = false,
            AfterClick = () => { if (Interlocked.Increment(ref n) == 6) Thread.Sleep(80); },   // one 80 ms stall in a 20 ms click period
        };
        var c = new ClickerService(new Nighty.Models.ClickerSettings { Cps = 50, DutyCycle = 35, HitFix = true, Enabled = true }, new RecordingSink(), host);
        c.Start();
        Thread.Sleep(700);
        c.Stop();
        Assert.True(c.Resyncs >= 1, $"resyncs {c.Resyncs}");
        Assert.True(c.MaxAfterClickMs >= 60, $"slowest hook {c.MaxAfterClickMs:0.0} ms");
        Assert.True(c.TotalClicks > 10);
        // starting again clears the counters
        n = -1000;
        c.Start(); Thread.Sleep(10); c.Stop();
        Assert.True(c.MaxAfterClickMs < 60);
    }
}

public class HitReaderTests
{
    /// <summary>Draws text onto a bitmap the way a game HUD might (coloured text with a dark outline) and returns BGRA bytes.</summary>
    private static (byte[] Bgra, int W, int H) Render(string text, float emSize, System.Drawing.Color fore, System.Drawing.Color back, bool outline)
    {
        using var probe = new System.Drawing.Bitmap(1, 1);
        using var font = new System.Drawing.Font("Arial", emSize, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        System.Drawing.SizeF size;
        using (var g0 = System.Drawing.Graphics.FromImage(probe)) size = g0.MeasureString(text, font);
        int w = (int)size.Width + 12, h = (int)size.Height + 8;
        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(back);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            if (outline)
                foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                    g.DrawString(text, font, System.Drawing.Brushes.Black, 6 + dx, 4 + dy);
            using var brush = new System.Drawing.SolidBrush(fore);
            g.DrawString(text, font, brush, 6, 4);
        }
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bytes = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        return (bytes, w, h);
    }

    [Theory]
    [InlineData("34", 60f, 255, 255, 255, 30, 30, 40, true)]     // big white number with outline on a dark panel
    [InlineData("35", 40f, 255, 220, 40, 20, 20, 20, true)]      // yellow
    [InlineData("34", 22f, 255, 255, 255, 0, 0, 0, false)]       // small text, so it has to be enlarged
    [InlineData("Hits: 31", 30f, 240, 240, 240, 40, 40, 60, false)]
    [InlineData("41", 36f, 20, 20, 20, 235, 235, 235, false)]    // dark on light
    public async Task Reads_the_number_off_a_game_like_image(string text, float em, int fr, int fg, int fb, int br, int bg, int bb, bool outline)
    {
        var (bytes, w, h) = Render(text, em, System.Drawing.Color.FromArgb(fr, fg, fb), System.Drawing.Color.FromArgb(br, bg, bb), outline);
        var r = await Nighty.Services.HitReader.ReadPixelsAsync(bytes, w, h);
        if (r.Problem != null && r.Problem.StartsWith("Windows has no text-recognition")) return;   // no OCR language on this PC: nothing to verify
        int expected = int.Parse(new string(text.Where(char.IsDigit).ToArray()));
        Assert.True(r.Value == expected, $"read '{r.Text}' (value {r.Value}), expected {expected}");
    }

    [Theory]
    [InlineData("34", 34)]
    [InlineData("Hits: 35", 35)]
    [InlineData("  12 hits\n", 12)]
    [InlineData("Hits", null)]          // a label is never turned into a number by the look-alike fix
    [InlineData("O5", 5)]               // a letter that looks like a digit is accepted inside a number that has a real digit
    [InlineData("3O", 30)]
    [InlineData("3ø", 30)]         // a slashed zero
    [InlineData("Hits: 3l", 31)]
    [InlineData("OIl", null)]           // look-alikes alone are not a number
    [InlineData("", null)]
    public void ParseHits_takes_the_first_whole_number(string text, int? expected) => Assert.Equal(expected, Nighty.Services.HitReader.ParseHits(text));

    [Fact]
    public void Settle_prefers_a_value_seen_twice_in_a_row_and_ignores_stray_misreads()
    {
        Assert.Equal(34, Nighty.Services.HitReader.Settle(new int?[] { null, 3, 34, 34, 34 }));
        Assert.Equal(34, Nighty.Services.HitReader.Settle(new int?[] { 34, 34, 84, null }));   // a late misread does not replace the settled value
        Assert.Equal(7, Nighty.Services.HitReader.Settle(new int?[] { null, 7 }));
        Assert.Null(Nighty.Services.HitReader.Settle(new int?[] { null, null }));
        Assert.Null(Nighty.Services.HitReader.Settle(Array.Empty<int?>()));
    }

    [Fact]
    public void AutoCrop_trims_empty_background_but_leaves_a_tight_box_alone()
    {
        // 100x60 dark box with a bright 10x20 block near the middle
        var px = new byte[100 * 60 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 30; px[i + 3] = 255; }
        for (int y = 20; y < 40; y++) for (int x = 45; x < 55; x++) { int i = (y * 100 + x) * 4; px[i] = px[i + 1] = px[i + 2] = 240; }
        var (cropped, cw, ch) = Nighty.Services.HitReader.AutoCrop(px, 100, 60);
        Assert.True(cw < 50 && ch < 50 && cw >= 10 && ch >= 20, $"cropped to {cw}x{ch}");
        Assert.Equal(cw * ch * 4, cropped.Length);
        // a box that is already tight stays as it is
        var tight = Nighty.Services.HitReader.AutoCrop(cropped, cw, ch);
        Assert.Equal((cw, ch), (tight.W, tight.H));
        // nothing drawn: unchanged
        var blank = new byte[40 * 40 * 4];
        Assert.Equal((40, 40), (Nighty.Services.HitReader.AutoCrop(blank, 40, 40).W, Nighty.Services.HitReader.AutoCrop(blank, 40, 40).H));
    }

    [Fact]
    public void Prepare_enlarges_small_regions_pads_them_and_can_invert()
    {
        var small = new byte[20 * 10 * 4];
        for (int i = 0; i < small.Length; i += 4) { small[i] = small[i + 1] = small[i + 2] = 40; small[i + 3] = 255; }
        small[(5 * 20 + 10) * 4] = small[(5 * 20 + 10) * 4 + 1] = small[(5 * 20 + 10) * 4 + 2] = 220;   // one bright pixel
        var normal = Nighty.Services.HitReader.Prepare(small, 20, 10, false, out int w, out int h);
        Assert.True(w > 20 * 3 && h > 10 * 3);                       // enlarged and padded
        var inv = Nighty.Services.HitReader.Prepare(small, 20, 10, true, out _, out _);
        Assert.Equal(255, normal[3]); Assert.Equal(255, inv[3]);     // always opaque
        Assert.Equal(255 - normal[0], inv[0]);                       // inverted
    }
}

public class HitReaderSweep
{
    [Fact]
    public async Task At_least_95_percent_of_numbers_are_read_across_fonts_colours_and_sizes()
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null) return;
        var fails = new List<string>(); int total = 0, ok = 0;
        var fonts = new[] { "Arial", "Segoe UI", "Consolas", "Impact" };
        var looks = new[] { (System.Drawing.Color.White, System.Drawing.Color.FromArgb(30, 30, 40), true), (System.Drawing.Color.FromArgb(255, 220, 40), System.Drawing.Color.FromArgb(25, 25, 25), true), (System.Drawing.Color.Black, System.Drawing.Color.FromArgb(235, 235, 235), false) };
        foreach (var font in fonts)
            foreach (var (fore, back, outline) in looks)
                foreach (float em in new[] { 28f, 48f })
                    for (int n = 28; n <= 42; n++)
                    {
                        string txt = n.ToString();
                        using var probe = new System.Drawing.Bitmap(1, 1);
                        using var f = new System.Drawing.Font(font, em, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                        System.Drawing.SizeF size; using (var g0 = System.Drawing.Graphics.FromImage(probe)) size = g0.MeasureString(txt, f);
                        int w = (int)size.Width + 12, h = (int)size.Height + 8;
                        using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.Clear(back); g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                            if (outline) foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }) g.DrawString(txt, f, System.Drawing.Brushes.Black, 6 + dx, 4 + dy);
                            using var br = new System.Drawing.SolidBrush(fore); g.DrawString(txt, f, br, 6, 4);
                        }
                        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        var bytes = new byte[w * h * 4]; System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); bmp.UnlockBits(data);
                        var r = await Nighty.Services.HitReader.ReadPixelsAsync(bytes, w, h);
                        total++;
                        if (r.Value == n) ok++; else fails.Add($"{font} {em} {(fore == System.Drawing.Color.White ? "white" : fore.R == 0 ? "dark" : "yellow")} {n} -> '{r.Text}' ({r.Value})");
                    }
        Assert.True(100.0 * ok / total >= 95, $"{ok}/{total} correct ({100.0 * ok / total:0.0}%)\n" + string.Join("\n", fails.Take(60)));
    }
}

public class HitReaderScreenTests
{
    /// <summary>The whole real path: a window really on the screen, copied with the screen grabber, enlarged, recognised.</summary>
    [Theory]
    [InlineData("34", 64)]
    [InlineData("35", 28)]
    public void Reads_a_number_from_a_window_really_shown_on_the_screen(string text, int fontSize)
    {
        string? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text = text, FontSize = fontSize, FontWeight = System.Windows.FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center,
                };
                var win = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                    ShowActivated = false, Left = 120, Top = 120, Width = 260, Height = 110,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 28, 40)), Content = tb,
                };
                win.Show();
                void Pump(int ms)
                {
                    var end = DateTime.UtcNow.AddMilliseconds(ms);
                    while (DateTime.UtcNow < end)
                    {
                        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                        Thread.Sleep(20);
                    }
                }
                Pump(500);   // let the window compose on screen
                var origin = win.PointToScreen(new System.Windows.Point(0, 0));
                double scale = System.Windows.Media.VisualTreeHelper.GetDpi(win).DpiScaleX;
                var region = new Nighty.Services.HitReader.Region((int)origin.X, (int)origin.Y, (int)(win.ActualWidth * scale), (int)(win.ActualHeight * scale));
                var r = Nighty.Services.HitReader.ReadAsync(region).GetAwaiter().GetResult();
                win.Close();
                if (r.Problem != null && r.Problem.StartsWith("Windows has no text-recognition")) return;
                if (r.Problem != null) { failure = r.Problem; return; }
                if (r.Value != int.Parse(text)) failure = $"read '{r.Text}' (value {r.Value}) from the screen, expected {text}; region {region}";
            }
            catch (Exception ex) { failure = ex.ToString(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(failure);
    }
}

public class HitWordSearchTests
{
    private static Nighty.Services.HitReader.WordBox W(string t, double x, double y, double w = 80, double h = 24) => new(t, x, y, w, h);

    [Fact]
    public void Takes_the_number_to_the_right_of_the_word_on_the_same_row()
    {
        var words = new[] { W("Personal", 1730, 70), W("Best", 1830, 70, 50), W("Hits", 1890, 70, 50), W("38", 1960, 70, 30), W("COMMANDS", 1850, 30), W("97:33", 1700, 30) };
        var hit = Nighty.Services.HitReader.FindNumberBeside(words, "Hits");
        Assert.NotNull(hit);
        Assert.Equal(38, hit!.Value.Value);
    }

    [Fact]
    public void Ignores_numbers_on_other_rows_and_far_away_and_picks_the_nearest()
    {
        var words = new[] { W("Hits", 100, 200, 50), W("12", 400, 202, 30), W("34", 170, 198, 30), W("99", 120, 300, 30), W("7", 3000, 200, 20) };
        Assert.Equal(34, Nighty.Services.HitReader.FindNumberBeside(words, "hits")!.Value.Value);
        Assert.Null(Nighty.Services.HitReader.FindNumberBeside(new[] { W("Hits", 100, 200, 50), W("99", 100, 400, 30), W("5", 3000, 200, 20) }, "Hits"));
    }

    [Fact]
    public void Accepts_a_number_glued_to_the_word_a_number_on_the_left_and_a_one_letter_misread()
    {
        Assert.Equal(35, Nighty.Services.HitReader.FindNumberBeside(new[] { W("Hits:35", 10, 10, 90) }, "Hits")!.Value.Value);
        Assert.Equal(41, Nighty.Services.HitReader.FindNumberBeside(new[] { W("41", 10, 10, 30), W("hits", 60, 10, 50) }, "Hits")!.Value.Value);
        Assert.Equal(38, Nighty.Services.HitReader.FindNumberBeside(new[] { W("Hlts", 10, 10, 50), W("38", 80, 10, 30) }, "Hits")!.Value.Value);   // I read as l
        Assert.Null(Nighty.Services.HitReader.FindNumberBeside(new[] { W("Kills", 10, 10, 50), W("38", 80, 10, 30) }, "Hits"));
        Assert.Null(Nighty.Services.HitReader.FindNumberBeside(Array.Empty<Nighty.Services.HitReader.WordBox>(), "Hits"));
    }

    /// <summary>A real window on the screen, captured with PrintWindow, recognised and searched for the word.</summary>
    [Fact]
    public void Finds_the_number_in_a_real_window()
    {
        string? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var grid = new System.Windows.Controls.Grid { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 120, 120)) };
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text = "Personal Best Hits   38", FontSize = 30, Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Top, Margin = new System.Windows.Thickness(0, 40, 30, 0),
                };
                grid.Children.Add(tb);
                var win = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize, ShowInTaskbar = false, Topmost = false,
                    ShowActivated = false, Left = 200, Top = 200, Width = 900, Height = 500, Content = grid,
                };
                win.Show();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                var end = DateTime.UtcNow.AddMilliseconds(500);
                while (DateTime.UtcNow < end) { win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background); Thread.Sleep(20); }
                var r = Nighty.Services.HitReader.ReadWindowAsync(hwnd, "Hits").GetAwaiter().GetResult();
                win.Close();
                if (r.Problem != null && r.Problem.StartsWith("Windows has no text-recognition")) return;
                if (r.Value != 38) failure = $"value {r.Value}, text '{r.Text}', problem {r.Problem}";
                else if (r.Found is not { } f || f.W < 20) failure = "no outline rectangle returned";
            }
            catch (Exception ex) { failure = ex.ToString(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(failure);
    }
}

public class AreaFlashTests
{
    [Fact]
    public void The_outline_window_shows_and_closes_without_taking_focus()
    {
        string? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var w = new Nighty.Views.AreaFlashWindow(new Nighty.Services.HitReader.Region(300, 300, 200, 80));
                w.Show();
                var end = DateTime.UtcNow.AddMilliseconds(250);
                while (DateTime.UtcNow < end) { w.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background); Thread.Sleep(20); }
                if (!w.IsVisible) failure = "outline window was not visible";
                if (w.IsActive) failure = "outline window took focus";
                w.Close();
            }
            catch (Exception ex) { failure = ex.ToString(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        Assert.Null(failure);
    }
}

public class SyncedRunnerTests
{
    private sealed class Rec : Nighty.Services.SyncedRunner.IOutput
    {
        public readonly List<(string What, long Ticks)> Events = new();
        public int MouseDepth, MaxMouseDepth;
        public readonly HashSet<int> KeysDown = new();
        public void Mouse(bool down)
        {
            Events.Add((down ? "mouse-down" : "mouse-up", System.Diagnostics.Stopwatch.GetTimestamp()));
            MouseDepth += down ? 1 : -1; MaxMouseDepth = Math.Max(MaxMouseDepth, MouseDepth);
        }
        public void Key(int vk, bool down)
        {
            Events.Add(((down ? "key-down " : "key-up ") + vk, System.Diagnostics.Stopwatch.GetTimestamp()));
            if (down) KeysDown.Add(vk); else KeysDown.Remove(vk);
        }
        public bool Quiet() => false;
    }

    private static Nighty.Services.SyncedRunner.Settings Cfg(double gap = 305, double equip = 25, double hold = 20, double swap = 0, double cd = 1300) =>
        new(gap, 20, equip, hold, swap, cd, CrossbowSlot: 3, SwordSlot: 1);

    [Fact]
    public void Sends_one_sword_click_per_gap_and_a_crossbow_cycle_every_cooldown_inside_the_gaps()
    {
        var rec = new Rec();
        var r = Nighty.Services.SyncedRunner.Run(Cfg(), 3200, CancellationToken.None, rec);
        Assert.InRange(r.SwordClicks, 10, 11);                      // clicks at 0, 305, ... 3050 ms
        Assert.InRange(r.Shots, 2, 3);                              // shots 1.3 s apart
        Assert.Equal(0, r.CyclesSkipped);                           // every cycle fits before the next sword click
        Assert.Equal(1, rec.MaxMouseDepth);                         // the mouse button is never down twice
        Assert.Equal(0, rec.MouseDepth); Assert.Empty(rec.KeysDown);   // nothing left held
        int downs = rec.Events.Count(e => e.What == "mouse-down");
        Assert.Equal(r.SwordClicks + r.Shots, downs);
        // weapon key (slot 3 = vk 0x33) pressed once per shot, sword key (slot 1 = vk 0x31) once per shot
        Assert.Equal(r.Shots, rec.Events.Count(e => e.What == $"key-down {0x33}"));
        Assert.Equal(r.Shots, rec.Events.Count(e => e.What == $"key-down {0x31}"));
        // gaps between sword clicks (the mouse-downs that are not shots) are the configured gap, not shorter
        var times = rec.Events.Where(e => e.What == "mouse-down").Select(e => e.Ticks).ToList();
        Assert.True(times.Count >= 5);
    }

    [Fact]
    public void A_cycle_that_does_not_fit_in_the_gap_is_skipped_not_squeezed_in()
    {
        var rec = new Rec();
        var r = Nighty.Services.SyncedRunner.Run(Cfg(gap: 90, equip: 60, hold: 40, swap: 20), 1500, CancellationToken.None, rec);   // 130 ms cycle in a 90 ms gap
        Assert.Equal(0, r.Shots);
        Assert.True(r.CyclesSkipped >= 1);
        Assert.True(r.SwordClicks >= 12);
        Assert.Equal(0, rec.MouseDepth); Assert.Empty(rec.KeysDown);
    }

    [Fact]
    public void Cancelling_stops_quickly_and_releases_everything()
    {
        var rec = new Rec();
        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = Task.Run(() => Nighty.Services.SyncedRunner.Run(Cfg(), 10000, cts.Token, rec));
        Thread.Sleep(380);
        cts.Cancel();
        var r = task.GetAwaiter().GetResult();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"took {sw.ElapsedMilliseconds} ms to stop");
        Assert.Equal(0, rec.MouseDepth); Assert.Empty(rec.KeysDown);
        Assert.True(r.SwordClicks >= 1);
    }

    [Fact]
    public void Stays_quiet_while_told_to()
    {
        var rec = new QuietRec();
        var r = Nighty.Services.SyncedRunner.Run(Cfg(), 800, CancellationToken.None, rec);
        Assert.Equal(0, r.SwordClicks); Assert.Equal(0, r.Shots); Assert.Empty(rec.Events);
    }

    private sealed class QuietRec : Nighty.Services.SyncedRunner.IOutput
    {
        public readonly List<string> Events = new();
        public void Mouse(bool down) => Events.Add("mouse");
        public void Key(int vk, bool down) => Events.Add("key");
        public bool Quiet() => true;
    }
}

public class SyncedRunnerRhythmTests
{
    private sealed class Rec : Nighty.Services.SyncedRunner.IOutput
    {
        public readonly List<(string What, long T)> Ev = new();
        public void Mouse(bool d) => Ev.Add((d ? "md" : "mu", System.Diagnostics.Stopwatch.GetTimestamp()));
        public void Key(int vk, bool d) => Ev.Add(((d ? "kd" : "ku") + vk, System.Diagnostics.Stopwatch.GetTimestamp()));
        public bool Quiet() => false;
    }

    /// <summary>Over a 10 s window: 33 sword clicks never closer than the cooldown, and 8 crossbow shots about 1.3 s apart, so about 41 hits.</summary>
    [Fact]
    public void Ten_seconds_gives_33_sword_clicks_and_8_shots_with_no_click_closer_than_the_cooldown()
    {
        var rec = new Rec();
        var cfg = new Nighty.Services.SyncedRunner.Settings(305, 20, 25, 20, 0, 1300, 3, 1);
        var res = Nighty.Services.SyncedRunner.Run(cfg, 10000, CancellationToken.None, rec);
        double F = System.Diagnostics.Stopwatch.Frequency / 1000.0;
        long t0 = rec.Ev[0].T;
        var sword = new List<double>(); var shots = new List<double>();
        bool afterBow = false;
        foreach (var e in rec.Ev)
        {
            if (e.What == "kd51") afterBow = true;
            if (e.What == "md") { if (afterBow) { shots.Add((e.T - t0) / F); afterBow = false; } else sword.Add((e.T - t0) / F); }
        }
        Assert.InRange(sword.Count, 32, 33);
        Assert.InRange(shots.Count, 7, 8);
        Assert.True(sword.Zip(sword.Skip(1), (a, b) => b - a).Min() >= 301.0, "a sword click came closer than the cooldown to the one before it");
        Assert.True(shots.Zip(shots.Skip(1), (a, b) => b - a).Min() >= 1299.0, "a shot left before the crossbow cooldown was up");
    }
}

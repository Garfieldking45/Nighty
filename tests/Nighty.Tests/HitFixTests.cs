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

using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nighty.Models;
using Nighty.Services;
using Xunit;
using Xunit.Abstractions;

namespace Nighty.Tests;

/// <summary>Keeps the app's log and settings folders out of the real %AppData%\Nighty while tests run.</summary>
internal static class TestEnv
{
    [ModuleInitializer]
    public static void Init() =>
        Environment.SetEnvironmentVariable("NIGHTY_DATA", Path.Combine(Path.GetTempPath(), "nighty-tests-" + Environment.ProcessId));
}

/// <summary>A fake mouse: records every button edge instead of sending it, and can refuse them like a blocked SendInput.</summary>
internal sealed class RecordingSink : IClickSink
{
    public readonly record struct Ev(long Ticks, ClickButton Button, bool Down, int Thread);
    private readonly ConcurrentQueue<Ev> _events = new();
    private int _inside, _downs, _ups;
    public volatile bool Refuse;
    public int Overlaps;
    public int Downs => Volatile.Read(ref _downs);
    public int Ups => Volatile.Read(ref _ups);
    public int Total => _events.Count;
    public int Outstanding => Downs - Ups;
    public Ev[] Snapshot() => _events.ToArray();
    public long FirstDownTicks => _events.Where(e => e.Down).Select(e => e.Ticks).DefaultIfEmpty(0).Min();

    public bool Button(ClickButton button, bool down)
    {
        // two threads inside at once = two clicker loops running at the same time
        if (Interlocked.Increment(ref _inside) > 1) Interlocked.Increment(ref Overlaps);
        try
        {
            Thread.SpinWait(300);
            if (Refuse) return false;
            _events.Enqueue(new Ev(Stopwatch.GetTimestamp(), button, down, Environment.CurrentManagedThreadId));
            if (down) Interlocked.Increment(ref _downs); else Interlocked.Increment(ref _ups);
            return true;
        }
        finally { Interlocked.Decrement(ref _inside); }
    }
}

[CollectionDefinition("clicker", DisableParallelization = true)]
public class ClickerCollection { }

[Collection("clicker")]
public class ClickerTests
{
    private readonly ITestOutputHelper _out;
    public ClickerTests(ITestOutputHelper output) => _out = output;

    private static ClickerHost Host(bool tuning = false) => new()
    {
        Precision = () => PrecisionMode.Balanced, Allowed = _ => true, Busy = () => false, AfterClick = () => { }, ProcessTuning = tuning,
    };

    private static ClickerSettings Settings(double cps = 50, bool hitFix = true) => new() { Cps = cps, DutyCycle = 35, HitFix = hitFix, Enabled = true };

    private static bool WaitUntil(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(1); }
        return cond();
    }

    [Fact]
    public void Start_then_stop_sends_clicks_and_stops()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(), sink, Host());
        c.Start();
        Assert.True(c.IsClicking);
        Assert.True(WaitUntil(() => sink.Downs >= 5, 1000), "no clicks arrived");
        c.Stop();
        Assert.False(c.IsClicking);
        Assert.Equal(sink.Downs, sink.Ups);
    }

    [Fact]
    public void Press_rate_is_close_to_the_requested_cps()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(cps: 40), sink, Host());
        c.Start();
        Thread.Sleep(1000);
        c.Stop();
        _out.WriteLine($"40 CPS requested, {sink.Downs} presses in ~1 s");
        Assert.InRange(sink.Downs, 36, 44);
    }

    /// <summary>After Stop() returns the mouse must be quiet and released: no late click, no button left down.</summary>
    [Fact]
    public void After_Stop_returns_nothing_more_is_sent_and_no_button_is_held()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(cps: 100), sink, Host());
        var rng = new Random(7);
        int lateEvents = 0, stuck = 0;
        for (int i = 0; i < 300; i++)
        {
            c.Start();
            Thread.Sleep(rng.Next(1, 45));
            c.Stop();
            int atStop = sink.Total;
            int heldAtStop = sink.Outstanding;
            Thread.Sleep(25);
            if (sink.Total != atStop) lateEvents++;
            if (heldAtStop != 0) stuck++;
        }
        _out.WriteLine($"300 cycles: {lateEvents} cycles had events AFTER Stop() returned, {stuck} cycles returned with the button still down");
        Assert.Equal(0, lateEvents);
        Assert.Equal(0, stuck);
        Assert.Equal(sink.Downs, sink.Ups);
    }

    [Fact]
    public void Rapid_toggling_never_runs_two_loops_at_once()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(cps: 100), sink, Host());
        for (int i = 0; i < 600; i++)
        {
            c.Start();
            if (i % 3 == 0) Thread.Sleep(2);
            c.Stop();
        }
        Thread.Sleep(100);
        _out.WriteLine($"600 rapid cycles: {sink.Overlaps} overlapping sends, {sink.Downs} presses, {sink.Ups} releases");
        Assert.Equal(0, sink.Overlaps);
        Assert.Equal(sink.Downs, sink.Ups);
        Assert.False(c.IsClicking);
    }

    /// <summary>
    /// Hold mode: a session that is already winding down must not be able to stop the session that replaced it.
    /// (The old loop called Stop(), which stops whichever session is current.)
    /// </summary>
    [Fact]
    public void A_finished_session_cannot_stop_the_next_one()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(cps: 50), sink, Host());
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        int firstThread = -1;
        c.KeepClicking = () =>
        {
            int me = Environment.CurrentManagedThreadId;
            if (Interlocked.CompareExchange(ref firstThread, me, -1) == -1)
            {
                entered.Set();
                gate.Wait(2000);     // hold the first session here, as if the UI stalled
                return false;        // ...then report the key as released
            }
            return me != firstThread || false;
        };
        c.Start();                               // session A
        Assert.True(entered.Wait(1000));
        c.Stop();                                // user toggles off...
        c.KeepClicking = () => true;             // ...and the next press starts session B (key is down)
        c.Start();
        gate.Set();                              // A finally evaluates "released"
        Thread.Sleep(150);
        Assert.True(c.IsClicking, "session B was stopped by the old session");
        c.Stop();
    }

    [Fact]
    public void Hold_mode_stops_quickly_when_the_key_is_released()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(cps: 20), sink, Host());   // slow clicks: the loop spends most time waiting
        bool down = true;
        c.KeepClicking = () => down;
        c.Start();
        Assert.True(WaitUntil(() => sink.Downs >= 3, 1000));
        var sw = Stopwatch.StartNew();
        down = false;
        Assert.True(WaitUntil(() => !c.IsClicking, 500), "clicker kept running after release");
        long ms = sw.ElapsedMilliseconds;
        int atStop = sink.Total;
        Thread.Sleep(80);
        _out.WriteLine($"stopped {ms} ms after the key was released");
        Assert.Equal(atStop, sink.Total);
        Assert.Equal(sink.Downs, sink.Ups);
        Assert.InRange(ms, 0, 120);
    }

    [Fact]
    public void Stop_after_N_clicks_sends_exactly_N_and_reports_it()
    {
        var sink = new RecordingSink();
        var s = Settings(cps: 100); s.StopAfterClicks = 7;
        var c = new ClickerService(s, sink, Host());
        string? reason = null;
        c.AutoStopped += r => reason = r;
        c.Start();
        Assert.True(WaitUntil(() => reason != null && !c.IsClicking, 2000));
        Thread.Sleep(50);
        Assert.Equal(7, sink.Downs);
        Assert.Equal(7, sink.Ups);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Time_limit_stops_the_clicker()
    {
        var sink = new RecordingSink();
        var s = Settings(cps: 50); s.TimeLimitSec = 1;
        var c = new ClickerService(s, sink, Host());
        c.Start();
        Assert.True(WaitUntil(() => !c.IsClicking, 2500));
        Assert.Equal(sink.Downs, sink.Ups);
    }

    [Fact]
    public void Refused_input_stops_the_clicker_and_reports_blocked()
    {
        var sink = new RecordingSink { Refuse = true };
        var c = new ClickerService(Settings(cps: 50), sink, Host());
        bool blocked = false;
        c.Blocked += () => blocked = true;
        c.Start();
        Assert.True(WaitUntil(() => blocked && !c.IsClicking, 1000), "blocked input should stop the clicker and raise Blocked");
        Assert.Equal(0, sink.Outstanding);
    }

    [Fact]
    public void Paused_while_not_allowed_sends_nothing_and_resumes()
    {
        var sink = new RecordingSink();
        bool allowed = false;
        var host = new ClickerHost { Precision = () => PrecisionMode.Balanced, Allowed = _ => allowed, Busy = () => false, AfterClick = () => { }, ProcessTuning = false };
        var c = new ClickerService(Settings(), sink, host);
        c.Start();
        Thread.Sleep(200);
        Assert.Equal(0, sink.Total);
        allowed = true;
        Assert.True(WaitUntil(() => sink.Downs >= 3, 1000));
        c.Stop();
    }

    /// <summary>Releasing the hold key while the clicker is waiting for its next click must cancel that click.</summary>
    [Fact]
    public void Releasing_the_key_during_the_gap_cancels_the_next_click()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(cps: 4), sink, Host());   // 250 ms between clicks
        bool down = true;
        c.KeepClicking = () => down;
        c.Start();
        Assert.True(WaitUntil(() => sink.Downs >= 1, 1000));
        Assert.True(WaitUntil(() => sink.Ups >= 1, 1000));
        Thread.Sleep(60);                               // the loop is now waiting for the next click
        int downsAtRelease = sink.Downs;
        down = false;                                   // released in the middle of the gap
        Thread.Sleep(450);                              // longer than one full gap
        _out.WriteLine($"presses at release: {downsAtRelease}, presses 450 ms later: {sink.Downs}, still clicking: {c.IsClicking}");
        Assert.False(c.IsClicking);
        Assert.Equal(downsAtRelease, sink.Downs);
    }

    [Fact]
    public void Hold_mode_press_release_cycles_always_end_released()
    {
        var sink = new RecordingSink();
        var c = new ClickerService(Settings(cps: 30), sink, Host());
        bool down = false;
        c.KeepClicking = () => down;
        var rng = new Random(3);
        for (int i = 0; i < 150; i++)
        {
            down = true; c.Start();
            Thread.Sleep(rng.Next(2, 60));
            down = false; c.Stop();
            Assert.Equal(0, sink.Outstanding);
        }
        Thread.Sleep(50);
        Assert.False(c.IsClicking);
        Assert.Equal(sink.Downs, sink.Ups);
    }

    /// <summary>Delay between pressing the hotkey (Start) and the first click, with the same process tuning the real app uses.</summary>
    [Fact]
    public void Start_to_first_click_latency_with_the_real_process_tuning()
    {
        // The real app has a sizeable managed heap; a blocking collection costs more the more there is to scan.
        var ballast = new List<object[]>();
        for (int i = 0; i < 400_000; i++) ballast.Add(new object[] { new byte[48], "x" + i });
        GC.Collect();

        var lat = new List<double>();
        for (int i = 0; i < 25; i++)
        {
            var sink = new RecordingSink();
            var c = new ClickerService(Settings(cps: 50), sink, Host(tuning: true));
            long t0 = Stopwatch.GetTimestamp();
            c.Start();
            Assert.True(WaitUntil(() => sink.Downs >= 1, 2000));
            lat.Add((sink.FirstDownTicks - t0) * 1000.0 / Stopwatch.Frequency);
            c.Stop();
            Thread.Sleep(20);
        }
        lat.Sort();
        _out.WriteLine($"start -> first click: median {lat[lat.Count / 2]:0.0} ms, p90 {lat[(int)(lat.Count * 0.9)]:0.0} ms, worst {lat[^1]:0.0} ms");
        GC.KeepAlive(ballast);
        Assert.True(lat[(int)(lat.Count * 0.9)] < 40, $"p90 {lat[(int)(lat.Count * 0.9)]:0.0} ms is too slow for a hotkey");
    }
}

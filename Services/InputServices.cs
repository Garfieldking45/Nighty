using System.Diagnostics;
using System.Windows;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Precise waits. The coarse part uses a high-resolution waitable timer (sub-millisecond on Windows 10 1803+); the last
/// <see cref="SpinMs"/> is a busy-wait for exact landing. Always returns promptly when cancelled.
/// </summary>
internal static class Wait
{
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;
    [ThreadStatic] private static IntPtr _timer;
    [ThreadStatic] private static bool _timerFailed;

    /// <summary>How long before a deadline the timer hands over to the spin loop. The clicker raises it for Precise and HitFix.</summary>
    public static volatile float SpinMsValue = 0.4f;
    public static double SpinMs { get => SpinMsValue; set => SpinMsValue = (float)value; }

    public static long Now => Stopwatch.GetTimestamp();
    public static long FromMs(double ms) => (long)(ms * TicksPerMs);
    public static double ToMs(long ticks) => ticks / TicksPerMs;

    public static void Ms(double ms, CancellationToken ct) => Until(Now + FromMs(ms), ct);

    public static void Until(long target, CancellationToken ct) => Until(target, ct, SpinMsValue);

    public static void Until(long target, CancellationToken ct, double spinMs)
    {
        while (!ct.IsCancellationRequested)
        {
            double left = (target - Now) / TicksPerMs;
            if (left <= 0) return;
            if (left > spinMs + 0.1) Sleep(Math.Min(left - spinMs, 15));
            else Thread.SpinWait(8);
        }
    }

    private static void Sleep(double ms)
    {
        if (!_timerFailed && _timer == IntPtr.Zero)
        {
            _timer = NativeMethods.CreateWaitableTimerExW(IntPtr.Zero, null, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);
            if (_timer == IntPtr.Zero) _timerFailed = true;
        }
        if (_timer != IntPtr.Zero)
        {
            long due = -(long)(ms * 10_000);   // 100 ns units, negative = relative
            if (NativeMethods.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                NativeMethods.WaitForSingleObject(_timer, 50);
                return;
            }
        }
        Thread.Sleep(ms >= 1 ? 1 : 0);   // fallback for older Windows
    }
}

/// <summary>
/// Gives the calling thread the best scheduling Windows will allow: multimedia-scheduler "Games" class at critical
/// priority, and with HitFix a time-critical thread pinned to one core (never core 0, which takes the interrupts).
/// Dispose on the same thread to undo it.
/// </summary>
internal sealed class ThreadBoost : IDisposable
{
    private IntPtr _mmcss;
    private UIntPtr _oldMask;
    private bool _pinned;
    private int _oldPriority;
    private readonly IntPtr _thread = NativeMethods.GetCurrentThread();

    /// <summary>Core the thread was pinned to, or -1.</summary>
    public int Core { get; private set; } = -1;

    public static ThreadBoost Apply(bool hitFix)
    {
        var b = new ThreadBoost();
        uint idx = 0;
        b._mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Games", ref idx);
        if (b._mmcss != IntPtr.Zero) NativeMethods.AvSetMmThreadPriority(b._mmcss, NativeMethods.AVRT_PRIORITY_CRITICAL);
        b._oldPriority = NativeMethods.GetThreadPriority(b._thread);
        NativeMethods.SetThreadPriority(b._thread, hitFix ? NativeMethods.THREAD_PRIORITY_TIME_CRITICAL : 2);

        // Pinning only helps when there is a spare core; on 2-core PCs it would fight the game for it.
        if (hitFix && Environment.ProcessorCount >= 4 && NativeMethods.GetProcessAffinityMask(NativeMethods.GetCurrentProcess(), out var procMask, out _))
        {
            ulong mask = procMask.ToUInt64();
            int core = -1;
            for (int i = Math.Min(63, Environment.ProcessorCount - 1); i >= 1; i--)
                if ((mask >> i & 1) != 0) { core = i; break; }
            if (core > 0)
            {
                var old = NativeMethods.SetThreadAffinityMask(b._thread, (UIntPtr)(1UL << core));
                if (old != UIntPtr.Zero) { b._oldMask = old; b._pinned = true; b.Core = core; NativeMethods.SetThreadIdealProcessor(b._thread, (uint)core); }
            }
        }
        return b;
    }

    public void Dispose()
    {
        if (_pinned) NativeMethods.SetThreadAffinityMask(_thread, _oldMask);
        NativeMethods.SetThreadPriority(_thread, _oldPriority);
        if (_mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(_mmcss);
    }
}

/// <summary>
/// Background auto-clicker. Uses SendInput and an absolute timeline (each click is scheduled from the previous
/// deadline, so timing error never accumulates). Honours the duty cycle, measures the clicks it really sent, and with
/// HitFix runs on a time-critical, core-pinned thread with an exact final spin before every click.
/// </summary>
public sealed class ClickerService
{
    private readonly ClickerSettings _s;
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;
    private readonly Queue<long> _stamps = new();
    private readonly object _gate = new();

    private System.Runtime.GCLatencyMode _gcMode;
    private ProcessPriorityClass _priorClass = ProcessPriorityClass.Normal;
    private bool _noGcRegion;

    public ClickerService(ClickerSettings settings) { _s = settings; }

    /// <summary>Hold mode: returns true while the activation key is still down. Checked on the clicker thread so a
    /// UI stall can never keep it swinging after release.</summary>
    public Func<bool>? KeepClicking { get; set; }

    public bool IsClicking => _cts != null;
    public event Action? StateChanged;
    /// <summary>Raised (from the clicker thread) when the clicker stops itself: click count or time limit reached.</summary>
    public event Action<string>? AutoStopped;
    /// <summary>Raised when Windows refuses the clicks, usually because the focused app runs as administrator.</summary>
    public event Action? Blocked;

    /// <summary>One line describing how the engine is running right now, for the Settings page.</summary>
    public string EngineInfo { get; private set; } = "";

    private long _total;
    /// <summary>Clicks sent since the clicker was last started.</summary>
    public long TotalClicks => Interlocked.Read(ref _total);

    /// <summary>Clicks actually sent during the last second.</summary>
    public double MeasuredCps
    {
        get
        {
            lock (_gate)
            {
                long now = Environment.TickCount64;
                while (_stamps.Count > 0 && now - _stamps.Peek() > 1000) _stamps.Dequeue();
                return _stamps.Count;
            }
        }
    }

    /// <summary>Spin window before each click, from the precision setting. HitFix always spins at least 2.5 ms.</summary>
    internal static double SpinFor(PrecisionMode mode, bool hitFix)
    {
        double spin = mode switch { PrecisionMode.Efficient => 0.25, PrecisionMode.Precise => 2.0, _ => 0.6 };
        return hitFix ? Math.Max(spin, 2.5) : spin;
    }

    /// <summary>Touches the timing code once at start-up so the first real click doesn't pay for JIT or timer creation.</summary>
    public static void Prewarm()
    {
        NativeMethods.timeBeginPeriod(1);
        try
        {
            for (int i = 0; i < 4; i++) Wait.Ms(1.5, CancellationToken.None);
            using var boost = ThreadBoost.Apply(false);
            SpinFor(PrecisionMode.Balanced, false);
        }
        catch { }
        finally { NativeMethods.timeEndPeriod(1); }
    }

    public void Start()
    {
        if (_cts != null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        Interlocked.Exchange(ref _total, 0);
        bool hitFix = _s.HitFix;
        NativeMethods.timeBeginPeriod(1);
        _gcMode = System.Runtime.GCSettings.LatencyMode;
        try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }   // avoid long GC pauses mid-fight
        try
        {
            using var proc = Process.GetCurrentProcess();
            _priorClass = proc.PriorityClass;
            // High, never Real-time: a time-critical clicker thread inside a High process lands on the same scheduling
            // level without letting the whole app (and its UI thread) outrank Windows' own system work.
            proc.PriorityClass = hitFix ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
        }
        catch { }
        var t = new Thread(() => Loop(cts.Token, hitFix)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Nighty clicker" };
        t.Start();
        Log.Info("Clicker started" + (hitFix ? " (HitFix)" : ""));
        PlaySound(start: true);
        StateChanged?.Invoke();
    }

    private static void PlaySound(bool start)
    {
        try { Application.Current?.Dispatcher.BeginInvoke(() => { if (start) Controls.Sfx.Start(); else Controls.Sfx.Stop(); }); } catch { }
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        cts.Cancel();
        NativeMethods.timeEndPeriod(1);
        if (_noGcRegion)
        {
            _noGcRegion = false;
            try { if (System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion) GC.EndNoGCRegion(); } catch { }
        }
        try { System.Runtime.GCSettings.LatencyMode = _gcMode; } catch { }
        try { using var proc = Process.GetCurrentProcess(); proc.PriorityClass = _priorClass; } catch { }
        Wait.SpinMs = 0.4;
        lock (_gate) _stamps.Clear();
        EngineInfo = "";
        Log.Info("Clicker stopped");
        PlaySound(start: false);
        StateChanged?.Invoke();
    }

    private void AutoStop(string reason)
    {
        Log.Info("Clicker auto-stop: " + reason);
        Stop();
        AutoStopped?.Invoke(reason);
    }

    private void Loop(CancellationToken ct, bool hitFix)
    {
        using var boost = ThreadBoost.Apply(hitFix);
        EngineInfo = hitFix
            ? $"HitFix on · {(boost.Core > 0 ? $"core {boost.Core}" : "shared cores")} · time-critical thread"
            : "Standard timing";
        if (hitFix)
        {
            // Take the garbage collector out of the picture while clicking. Starting the region does one collection,
            // which is why it happens here on the clicker thread and not on the UI.
            try { _noGcRegion = GC.TryStartNoGCRegion(24 * 1024 * 1024); } catch { _noGcRegion = false; }
        }
        RunLoop(ct, hitFix);
    }

    private void RunLoop(CancellationToken ct, bool hitFix)
    {
        bool allowed = true;
        long nextCheck = 0;
        int refused = 0;

        if (_s.StartDelayMs > 0) Wait.Ms(_s.StartDelayMs, ct);
        long next = Wait.Now;
        long deadline = _s.TimeLimitSec > 0 ? Wait.Now + Wait.FromMs(_s.TimeLimitSec * 1000.0) : long.MaxValue;

        while (!ct.IsCancellationRequested)
        {
            if (KeepClicking is { } keep && !keep()) { Stop(); break; }

            double spin = SpinFor(Svc.S.General.Precision, hitFix);
            Wait.SpinMs = spin;

            // Foreground checks involve process lookups, so refresh them every 40 ms instead of every click.
            if (Wait.Now >= nextCheck)
            {
                allowed = !RobloxService.IsOwnWindowForeground() && (!_s.OnlyWhenRobloxFocused || Svc.Roblox.IsForeground);
                nextCheck = Wait.Now + Wait.FromMs(40);
            }
            if (!allowed)
            {
                Wait.Ms(10, ct);
                next = Wait.Now;   // restart the timeline when resuming
                continue;
            }

            double cps = _s.UseRange ? _s.MinCps + _rng.NextDouble() * Math.Max(0, _s.MaxCps - _s.MinCps) : _s.Cps;
            double periodMs = 1000.0 / Math.Max(1, cps);
            if (_s.Jitter) periodMs *= 0.6 + _rng.NextDouble() * 0.8;
            int perHit = Math.Clamp(_s.ClicksPerHit, 1, 5);
            // The clicks of one hit share the period; each is held for the duty fraction of its own slice. A press the
            // game never samples is a press that did not happen, so holds are at least 4 ms when the slice allows it.
            double sliceMs = periodMs / perHit;
            double holdMs = Math.Clamp(sliceMs * _s.DutyCycle / 100.0, Math.Min(4, sliceMs - 0.3), Math.Max(0.5, sliceMs - 1));
            var button = _s.Button;

            Wait.Until(next, ct, spin);
            if (ct.IsCancellationRequested) break;
            if (Svc.Bow.IsRunning) { next = Wait.Now + Wait.FromMs(1); continue; }   // don't interleave sword clicks with the crossbow shot
            for (int k = 0; k < perHit && !ct.IsCancellationRequested; k++)
            {
                if (!InputSender.MouseButton(button, true))
                {
                    // SendInput returns 0 when Windows blocks injected input into a higher-integrity window.
                    if (++refused >= 3) { Stop(); Blocked?.Invoke(); return; }
                    continue;
                }
                refused = 0;
                try { Wait.Ms(holdMs, ct); }
                finally { InputSender.MouseButton(button, false); }   // never leave the button stuck down
                lock (_gate) _stamps.Enqueue(Environment.TickCount64);
                Interlocked.Increment(ref _total);
                if (k < perHit - 1) Wait.Ms(sliceMs - holdMs, ct);
            }

            Svc.Bow.TryAuto();   // react right after a click instead of waiting for the UI timer

            if (_s.StopAfterClicks > 0 && Interlocked.Read(ref _total) >= _s.StopAfterClicks) { AutoStop($"Stopped after {_s.StopAfterClicks:N0} clicks."); return; }
            if (Wait.Now >= deadline) { AutoStop("Stopped: the time limit is up."); return; }

            // Accumulate so an overshoot never compounds into a slow CPS, and resync after a stall instead of bursting.
            next += Wait.FromMs(periodMs);
            long now = Wait.Now;
            if (next < now) next = now + Wait.FromMs(periodMs);
        }
    }
}

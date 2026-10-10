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
            if (left > spinMs + 0.1) Sleep(Math.Min(left - spinMs, 15), ct);
            else Thread.SpinWait(8);
        }
    }

    [ThreadStatic] private static IntPtr[]? _handles;

    /// <summary>Sleeps on the high-resolution timer, but wakes immediately when the token is cancelled, so Stop() never waits out a 15 ms sleep.</summary>
    private static void Sleep(double ms, CancellationToken ct)
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
                var h = _handles ??= new IntPtr[2];
                h[0] = _timer;
                if (ct.CanBeCanceled)
                {
                    h[1] = ct.WaitHandle.SafeWaitHandle.DangerousGetHandle();
                    NativeMethods.WaitForMultipleObjects(2, h, false, 50);
                }
                else NativeMethods.WaitForSingleObject(_timer, 50);
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

/// <summary>Where the clicker's mouse presses go. The real one is SendInput; tests plug in a recorder.</summary>
internal interface IClickSink
{
    /// <summary>Sends one button edge. False = Windows refused it.</summary>
    bool Button(ClickButton button, bool down);
}

internal sealed class SendInputSink : IClickSink
{
    public bool Button(ClickButton button, bool down) => InputSender.MouseButton(button, down);
}

/// <summary>The few things the clicker asks the rest of the app. Defaults use the live app; tests replace them.</summary>
internal sealed class ClickerHost
{
    public Func<PrecisionMode> Precision { get; init; } = () => Svc.S.General.Precision;
    /// <summary>False pauses clicking (Nighty itself in front, or Roblox not in front when that option is on).</summary>
    public Func<ClickerSettings, bool> Allowed { get; init; } = s => !RobloxService.IsOwnWindowForeground() && (!s.OnlyWhenRobloxFocused || Svc.Roblox.IsForeground);
    /// <summary>True while another macro owns the mouse (the crossbow shot).</summary>
    public Func<bool> Busy { get; init; } = () => Svc.Bow.IsRunning;
    public Action AfterClick { get; init; } = () => Svc.Bow.TryAuto();
    /// <summary>Process priority class and GC latency mode. Off in tests so they don't change the test runner.</summary>
    public bool ProcessTuning { get; init; } = true;
}

/// <summary>
/// Background auto-clicker. Uses SendInput and an absolute timeline (each click is scheduled from the previous
/// deadline, so timing error never accumulates). Honours the duty cycle, measures the clicks it really sent, and with
/// HitFix runs on a time-critical, core-pinned thread with an exact final spin before every click.
/// </summary>
public sealed class ClickerService
{
    /// <summary>
    /// One run of the clicker. Start() makes a new one, and every Stop (from the UI, the hotkey or the loop itself) names the exact
    /// session it means, so an old loop that is still winding down can never stop the one that replaced it.
    /// </summary>
    private sealed class Session
    {
        public readonly CancellationTokenSource Cts = new();
        public Thread? Thread;
        /// <summary>A button-down was sent and its release has not been (set and cleared by the loop thread).</summary>
        public volatile bool Pressed;
        public ClickButton Button;
        public bool TuningHeld;
    }

    private readonly ClickerSettings _s;
    private readonly Random _rng = new();
    private readonly Queue<long> _stamps = new();
    private readonly object _gate = new();
    private readonly object _life = new();        // guards _cur / _winding / tuning counts
    private Session? _cur;                         // the running session, or null
    private Session? _winding;                     // the last one to stop: Start() waits for it so two loops never overlap

    private System.Runtime.GCLatencyMode _gcMode;
    private ProcessPriorityClass _priorClass = ProcessPriorityClass.Normal;
    private int _tuneHolders;                      // sessions that currently hold the process tuning (timer resolution, priority)

    private readonly IClickSink _sink;
    private readonly ClickerHost _host;

    public ClickerService(ClickerSettings settings) : this(settings, new SendInputSink(), new ClickerHost()) { }
    internal ClickerService(ClickerSettings settings, IClickSink sink, ClickerHost host) { _s = settings; _sink = sink; _host = host; }

    /// <summary>Hold mode: returns true while the activation key is still down. Checked on the clicker thread so a
    /// UI stall can never keep it swinging after release.</summary>
    public Func<bool>? KeepClicking { get; set; }

    public bool IsClicking => Volatile.Read(ref _cur) != null;
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
        Session s;
        Session? previous;
        lock (_life)
        {
            if (_cur != null) return;
            s = new Session { Button = _s.Button };
            _cur = s;
            previous = _winding;
        }
        // A session that stopped itself (hold key released, click limit) may still be finishing its last release; let it end first.
        previous?.Thread?.Join(250);

        Interlocked.Exchange(ref _total, 0);
        bool hitFix = _s.HitFix;
        AcquireTuning(s);
        try
        {
            s.Thread = new Thread(() => Loop(s, hitFix)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Nighty clicker" };
            s.Thread.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Clicker thread could not start", ex);
            StopSession(s);
            return;
        }
        Log.Info("Clicker started" + (hitFix ? " (HitFix)" : ""));
        PlaySound(start: true);
        StateChanged?.Invoke();
    }

    private static void PlaySound(bool start)
    {
        try { Application.Current?.Dispatcher.BeginInvoke(() => { if (start) Controls.Sfx.Start(); else Controls.Sfx.Stop(); }); } catch { }
    }

    /// <summary>
    /// Stops the running session. When this returns the clicker thread has finished: nothing more will be sent and no
    /// button is left held (unless it was stopped from the clicker thread itself, which returns straight away).
    /// </summary>
    public void Stop()
    {
        var s = Volatile.Read(ref _cur);
        if (s != null) StopSession(s);
    }

    private void StopSession(Session s)
    {
        lock (_life)
        {
            if (!ReferenceEquals(_cur, s)) return;   // already stopped, or a newer session owns the clicker now
            _cur = null;
            _winding = s;
        }
        s.Cts.Cancel();
        if (s.Thread != null && s.Thread != Thread.CurrentThread)
        {
            // Waiting here is what makes "Stop() returned" mean "the mouse is quiet". The loop notices the cancel
            // within a fraction of a millisecond; the generous limit only guards against a stuck thread.
            if (!s.Thread.Join(1000)) Log.Warn("Clicker thread did not finish within 1 s of Stop");
        }
        // If the thread died or hung while a button was down, release it here so it can never stay stuck.
        if (s.Pressed)
        {
            s.Pressed = false;
            try { _sink.Button(s.Button, false); } catch { }
        }
        ReleaseTuning(s);
        lock (_gate) _stamps.Clear();
        EngineInfo = "";
        Log.Info("Clicker stopped");
        PlaySound(start: false);
        StateChanged?.Invoke();
    }

    // Process-wide settings are shared by every session, so they are applied by the first and restored by the last.
    private void AcquireTuning(Session s)
    {
        lock (_life)
        {
            s.TuningHeld = true;
            if (_tuneHolders++ > 0) return;
            NativeMethods.timeBeginPeriod(1);
            if (!_host.ProcessTuning) return;
            _gcMode = System.Runtime.GCSettings.LatencyMode;
            try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }   // avoid long GC pauses mid-fight
            try
            {
                using var proc = Process.GetCurrentProcess();
                _priorClass = proc.PriorityClass;
                // High, never Real-time: a time-critical clicker thread inside a High process lands on the same scheduling
                // level without letting the whole app (and its UI thread) outrank Windows' own system work.
                proc.PriorityClass = _s.HitFix ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
            }
            catch { }
        }
    }

    private void ReleaseTuning(Session s)
    {
        lock (_life)
        {
            if (!s.TuningHeld) return;
            s.TuningHeld = false;
            if (--_tuneHolders > 0) return;
            NativeMethods.timeEndPeriod(1);
            Wait.SpinMs = 0.4;
            if (!_host.ProcessTuning) return;
            try { System.Runtime.GCSettings.LatencyMode = _gcMode; } catch { }
            try { using var proc = Process.GetCurrentProcess(); proc.PriorityClass = _priorClass; } catch { }
        }
    }

    private void Loop(Session s, bool hitFix)
    {
        try
        {
            using var boost = ThreadBoost.Apply(hitFix);
            EngineInfo = hitFix
                ? $"HitFix on · {(boost.Core > 0 ? $"core {boost.Core}" : "shared cores")} · time-critical thread"
                : "Standard timing";
            RunLoop(s, hitFix);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the clicker must not be left looking "on" with nothing running.
            Log.Error("Clicker loop failed", ex);
            StopSession(s);
        }
    }

    /// <summary>
    /// Waits for the next click, but keeps looking at the hold key so letting go cancels it right away.
    /// False = do not click (stopped, or the key was released).
    /// </summary>
    private bool WaitForNext(Session s, long next, double spin)
    {
        var ct = s.Cts.Token;
        long spinTicks = Wait.FromMs(spin), chunk = Wait.FromMs(4);
        while (!ct.IsCancellationRequested)
        {
            if (KeepClicking is { } keep && !keep()) return false;
            long now = Wait.Now, left = next - now;
            if (left <= 0) break;
            if (left > spinTicks + Wait.FromMs(0.2)) Wait.Until(now + Math.Min(left - spinTicks, chunk), ct, 0.1);   // sleep in short slices
            else { Wait.Until(next, ct, spin); break; }                                                              // last stretch: exact spin
        }
        return !ct.IsCancellationRequested && !(KeepClicking is { } k2 && !k2());
    }

    private void RunLoop(Session s, bool hitFix)
    {
        var ct = s.Cts.Token;
        bool allowed = true;
        long nextCheck = 0;
        int refused = 0;

        if (_s.StartDelayMs > 0) Wait.Ms(_s.StartDelayMs, ct);
        long next = Wait.Now;
        long deadline = _s.TimeLimitSec > 0 ? Wait.Now + Wait.FromMs(_s.TimeLimitSec * 1000.0) : long.MaxValue;

        while (!ct.IsCancellationRequested)
        {
            if (KeepClicking is { } keep && !keep()) { StopSession(s); return; }

            double spin = SpinFor(_host.Precision(), hitFix);
            Wait.SpinMs = spin;

            // Foreground checks involve process lookups, so refresh them every 40 ms instead of every click.
            if (Wait.Now >= nextCheck)
            {
                allowed = _host.Allowed(_s);
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
            s.Button = button;

            if (!WaitForNext(s, next, spin))
            {
                if (ct.IsCancellationRequested) break;
                StopSession(s);   // the hold key was let go while waiting: no click
                return;
            }
            if (_host.Busy()) { next = Wait.Now + Wait.FromMs(1); continue; }   // don't interleave sword clicks with the crossbow shot
            for (int k = 0; k < perHit && !ct.IsCancellationRequested; k++)
            {
                if (!_sink.Button(button, true))
                {
                    // SendInput returns 0 when Windows blocks injected input into a higher-integrity window.
                    if (++refused >= 3) { StopSession(s); Blocked?.Invoke(); return; }
                    continue;
                }
                refused = 0;
                s.Pressed = true;
                try { Wait.Ms(holdMs, ct); }
                finally { _sink.Button(button, false); s.Pressed = false; }   // never leave the button stuck down
                lock (_gate) _stamps.Enqueue(Environment.TickCount64);
                Interlocked.Increment(ref _total);
                if (k < perHit - 1) Wait.Ms(sliceMs - holdMs, ct);
            }

            _host.AfterClick();   // react right after a click instead of waiting for the UI timer

            if (_s.StopAfterClicks > 0 && Interlocked.Read(ref _total) >= _s.StopAfterClicks) { Finish(s, $"Stopped after {_s.StopAfterClicks:N0} clicks."); return; }
            if (Wait.Now >= deadline) { Finish(s, "Stopped: the time limit is up."); return; }

            // Accumulate so an overshoot never compounds into a slow CPS, and resync after a stall instead of bursting.
            next += Wait.FromMs(periodMs);
            long now = Wait.Now;
            if (next < now) next = now + Wait.FromMs(periodMs);
        }
    }

    private void Finish(Session s, string reason)
    {
        Log.Info("Clicker auto-stop: " + reason);
        StopSession(s);
        AutoStopped?.Invoke(reason);
    }
}

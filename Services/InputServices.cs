using System.Diagnostics;
using System.Windows;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Precise waits. Coarse part uses a high-resolution waitable timer (sub-millisecond on Windows 10 1803+),
/// the last ~0.4 ms is a busy-wait for exact landing. Always returns promptly when cancelled.
/// </summary>
internal static class Wait
{
    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;
    [ThreadStatic] private static IntPtr _timer;
    [ThreadStatic] private static bool _timerFailed;

    public static long Now => Stopwatch.GetTimestamp();
    public static long FromMs(double ms) => (long)(ms * TicksPerMs);

    public static void Ms(double ms, CancellationToken ct) => Until(Now + FromMs(ms), ct);

    public static void Until(long target, CancellationToken ct)
    {
        const double SpinMs = 0.4;
        while (!ct.IsCancellationRequested)
        {
            double left = (target - Now) / TicksPerMs;
            if (left <= 0) return;
            if (left > SpinMs + 0.1) Sleep(Math.Min(left - SpinMs, 15));
            else Thread.SpinWait(20);
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
/// Background auto-clicker. Uses SendInput and an absolute timeline (each click is scheduled from the previous
/// deadline, so timing error never accumulates). Honours the duty cycle and measures the clicks it really sent.
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

    public ClickerService(ClickerSettings settings) { _s = settings; }

    public bool IsClicking => _cts != null;
    public event Action? StateChanged;

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

    public void Start()
    {
        if (_cts != null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        NativeMethods.timeBeginPeriod(1);
        _gcMode = System.Runtime.GCSettings.LatencyMode;
        try { System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency; } catch { }   // avoid long GC pauses mid-fight
        try { _priorClass = System.Diagnostics.Process.GetCurrentProcess().PriorityClass; System.Diagnostics.Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
        var t = new Thread(() => Loop(cts.Token)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Nighty clicker" };
        t.Start();
        Log.Info("Clicker started");
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        cts.Cancel();
        NativeMethods.timeEndPeriod(1);
        try { System.Runtime.GCSettings.LatencyMode = _gcMode; } catch { }
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = _priorClass; } catch { }
        lock (_gate) _stamps.Clear();
        Log.Info("Clicker stopped");
        StateChanged?.Invoke();
    }

    private void Loop(CancellationToken ct)
    {
        // Ask Windows' multimedia scheduler to keep this thread ahead of ordinary work.
        uint taskIndex = 0;
        IntPtr mmcss = NativeMethods.AvSetMmThreadCharacteristicsW("Games", ref taskIndex);
        try { RunLoop(ct); }
        finally { if (mmcss != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(mmcss); }
    }

    private void RunLoop(CancellationToken ct)
    {
        bool allowed = true;
        long nextCheck = 0;
        long next = Wait.Now;

        while (!ct.IsCancellationRequested)
        {
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
            double holdMs = Math.Clamp(periodMs * _s.DutyCycle / 100.0, 0.5, periodMs - 0.3);
            var button = _s.Button;

            Wait.Until(next, ct);
            if (ct.IsCancellationRequested) break;
            long clickStart = Wait.Now;
            InputSender.MouseButton(button, true);
            try { Wait.Until(clickStart + Wait.FromMs(holdMs), ct); }
            finally { InputSender.MouseButton(button, false); }   // never leave the button stuck down

            lock (_gate) _stamps.Enqueue(Environment.TickCount64);

            next += Wait.FromMs(periodMs);

            // After a stall (system or game lag spike) we are behind schedule. Make up the missed clicks, but only
            // by running up to 25% faster than the target so we never spam past what the game accepts, and give up
            // (resync) if the stall was long enough that catching up would mean a visible burst.
            long now = Wait.Now;
            long behind = now - next;
            if (behind > Wait.FromMs(periodMs * 4)) next = now;
            else { long floor = clickStart + Wait.FromMs(periodMs * 0.8); if (next < floor) next = floor; }
        }
    }
}

/// <summary>Plays user-authored macros (key presses, clicks, waits). Cancels cleanly and releases held keys.</summary>
public sealed class MacroPlayer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    public event Action? Changed;

    public bool IsRunning(Guid id) => _running.ContainsKey(id);

    public void Toggle(MacroDef m, int startDelayMs = 0)
    {
        if (IsRunning(m.Id)) Stop(m.Id); else Start(m, startDelayMs);
    }

    public void Start(MacroDef m, int startDelayMs = 0)
    {
        if (IsRunning(m.Id) || m.Steps.Count == 0) return;
        var cts = new CancellationTokenSource();
        _running[m.Id] = cts;
        var steps = m.Steps.Select(s => (s.Type, s.Value)).ToList();
        int repeat = m.Repeat;
        Changed?.Invoke();
        Task.Run(() => Run(m.Id, steps, repeat, startDelayMs, cts.Token));
    }

    public void Stop(Guid id)
    {
        if (_running.TryRemove(id, out var cts)) cts.Cancel();
        Changed?.Invoke();
    }

    public void StopAll()
    {
        foreach (var id in _running.Keys.ToList()) Stop(id);
    }

    private void Run(Guid id, List<(MacroStepType Type, int Value)> steps, int repeat, int startDelay, CancellationToken ct)
    {
        var held = new HashSet<int>();
        try
        {
            NativeMethods.timeBeginPeriod(1);
            if (startDelay > 0) Wait.Ms(startDelay, ct);
            for (int i = 0; (repeat == 0 || i < repeat) && !ct.IsCancellationRequested; i++)
            {
                foreach (var (type, value) in steps)
                {
                    if (ct.IsCancellationRequested) break;
                    switch (type)
                    {
                        case MacroStepType.KeyPress:
                            InputSender.Key(value, true); Wait.Ms(20, ct); InputSender.Key(value, false); break;
                        case MacroStepType.KeyDown:
                            InputSender.Key(value, true); held.Add(value); break;
                        case MacroStepType.KeyUp:
                            InputSender.Key(value, false); held.Remove(value); break;
                        case MacroStepType.Click:
                            var b = (ClickButton)Math.Clamp(value, 0, 2);
                            InputSender.MouseButton(b, true); Wait.Ms(20, ct); InputSender.MouseButton(b, false); break;
                        case MacroStepType.Wait:
                            Wait.Ms(value, ct); break;
                    }
                }
                if (steps.All(s => s.Type != MacroStepType.Wait)) Wait.Ms(5, ct);   // avoid a hot loop with no delays
            }
        }
        catch (Exception ex) { Log.Error("Macro failed", ex); }
        finally
        {
            foreach (var vk in held) InputSender.Key(vk, false);
            NativeMethods.timeEndPeriod(1);
            _running.TryRemove(id, out _);
            Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }
}

/// <summary>
/// Counts real mouse clicks (including synthetic ones) with a low-level mouse hook. The hook is only installed
/// while something needs it and is always removed on <see cref="Stop"/>.
/// </summary>
public sealed class CpsMonitor
{
    private IntPtr _hook;
    private NativeMethods.LowLevelProc? _proc;   // keep the delegate alive
    private readonly Queue<long> _left = new(), _right = new();
    private readonly object _gate = new();

    public bool IsActive => _hook != IntPtr.Zero;

    public void Start()
    {
        if (IsActive) return;
        _proc = Callback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn("Mouse hook could not be installed");
    }

    public void Stop()
    {
        if (!IsActive) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    public (int Left, int Right) Read()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            while (_left.Count > 0 && now - _left.Peek() > 1000) _left.Dequeue();
            while (_right.Count > 0 && now - _right.Peek() > 1000) _right.Dequeue();
            return (_left.Count, _right.Count);
        }
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            lock (_gate)
            {
                if (msg == NativeMethods.WM_LBUTTONDOWN) _left.Enqueue(Environment.TickCount64);
                else if (msg == NativeMethods.WM_RBUTTONDOWN) _right.Enqueue(Environment.TickCount64);
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}

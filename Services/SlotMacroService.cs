using System.Collections.Concurrent;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Hotbar macros: each one swaps between numbered hotbar slots, clicks and (for some) looks down, on its own
/// high-priority thread. Keys are sent as hardware scancodes. Every run can be cancelled at any moment; held keys,
/// buttons and the camera are always restored.
/// </summary>
public sealed class SlotMacroService
{
    // Hold long enough to span a frame at 60 fps, or the game never sees the press.
    private const int KeyHoldMs = 20, ClickHoldMs = 10;
    private const double CrossbowCooldownMs = 1300, WhimCooldownMs = 1100;

    private readonly ConcurrentDictionary<SlotMacroKind, CancellationTokenSource> _running = new();
    public event Action? Changed;

    public bool IsRunning(SlotMacroKind kind) => _running.ContainsKey(kind);
    public bool AnyRunning => !_running.IsEmpty;

    /// <summary>Starts a run (ignored while one is already running).</summary>
    public void Start(SlotMacroConfig c)
    {
        if (!c.Enabled || Svc.Hotkeys.Suspended || RobloxService.IsOwnWindowForeground()) return;
        if (c.OnlyWhenRobloxFocused && !Svc.Roblox.IsForeground) return;
        // Auto Crossbow can be limited to slot 1: a different (or unknown, after scrolling) slot means "not now".
        if (c.Kind == SlotMacroKind.Crossbow && c.OnlyInSlotOne && Svc.Bow.CurrentSlot != 1)
        {
            Svc.Toast.Show("Auto Crossbow", "only works in slot 1 (press 1 first)", false);
            return;
        }
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(c.Kind, cts)) { cts.Dispose(); return; }
        var snap = c.Snapshot();
        Changed?.Invoke();
        new Thread(() => Run(snap, cts)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Nighty " + c.Kind }.Start();
        new Thread(() => Watch(snap, cts)) { IsBackground = true, Name = "Nighty " + c.Kind + " watch" }.Start();
    }

    public void Toggle(SlotMacroConfig c)
    {
        if (IsRunning(c.Kind)) Stop(c.Kind); else Start(c);
    }

    public void Stop(SlotMacroKind kind)
    {
        if (_running.TryGetValue(kind, out var cts)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    public void StopAll()
    {
        foreach (var k in _running.Keys.ToList()) Stop(k);
    }

    /// <summary>Ends a run the moment its hold key is let go or Roblox loses focus, without waiting for the UI timer.</summary>
    private static void Watch(SlotMacroConfig c, CancellationTokenSource cts)
    {
        try
        {
            long nextFocusCheck = 0;
            while (!cts.IsCancellationRequested)
            {
                if (c.Style == SlotMacroStyle.Hold && !HotkeyService.IsDown((c.HotkeyVk, c.HotkeyMods))) { cts.Cancel(); return; }
                if (c.OnlyWhenRobloxFocused && Environment.TickCount64 >= nextFocusCheck)
                {
                    nextFocusCheck = Environment.TickCount64 + 100;
                    if (!Svc.Roblox.IsForeground) { cts.Cancel(); return; }
                }
                Thread.Sleep(1);
            }
        }
        catch (ObjectDisposedException) { }
    }

    private static int SlotVk(double slot) => 0x31 + (int)Math.Clamp(slot, 1, 9) - 1;

    /// <summary>Press and release a hotbar number key.</summary>
    private static void Tap(double slot, CancellationToken ct)
    {
        int vk = SlotVk(slot);
        InputSender.KeyScan(vk, true);
        try { Wait.Ms(KeyHoldMs, ct); }
        finally { InputSender.KeyScan(vk, false); }
    }

    private static void Click(CancellationToken ct, ClickButton b = ClickButton.Left)
    {
        InputSender.MouseButton(b, true);
        try { Wait.Ms(ClickHoldMs, ct); }
        finally { InputSender.MouseButton(b, false); }   // never leave the button stuck down
    }

    /// <summary>Gap between spam clicks: the Auto Clicker's speed, never faster than 20 ms.</summary>
    private static double GapMs()
    {
        var c = Svc.S.Clicker;
        double cps = Math.Clamp(c.UseRange ? c.MaxCps : c.Cps, 1, 50);
        return Math.Max(20, 1000.0 / cps);
    }

    /// <summary>Smooth pitch flick over several steps. Positive looks down.</summary>
    private static void Flick(int totalDy, CancellationToken ct)
    {
        const int steps = 14;
        int per = totalDy / steps, acc = 0;
        for (int i = 0; i < steps; i++)
        {
            int d = i == steps - 1 ? totalDy - acc : per;
            acc += d;
            InputSender.MoveRelative(0, d);
            Wait.Ms(3, ct);
        }
    }

    private void Run(SlotMacroConfig c, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        NativeMethods.timeBeginPeriod(1);
        try
        {
            switch (c.Kind)
            {
                case SlotMacroKind.Crossbow: RunSwapAndSwing(c, CrossbowCooldownMs, ct); break;
                case SlotMacroKind.Whim: RunSwapAndSwing(c, WhimCooldownMs, ct); break;
                case SlotMacroKind.Lasso: RunLasso(c, ct); break;
                case SlotMacroKind.BuildUp: RunBuildUp(c, ct); break;
                case SlotMacroKind.Melody: RunMelody(c, ct); break;
                case SlotMacroKind.GingerBread: RunGingerBread(c, ct); break;
            }
        }
        catch (Exception ex) { Log.Error("Hotbar macro " + c.Kind + " failed", ex); }
        finally
        {
            NativeMethods.timeEndPeriod(1);
            _running.TryRemove(new KeyValuePair<SlotMacroKind, CancellationTokenSource>(c.Kind, cts));
            cts.Dispose();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }

    /// <summary>
    /// Weapon slot, fire, then press the sword key and swing while it is still held (the sword selects on key down, so
    /// there is no swap delay), and keep swinging through the cooldown. Repeats until stopped; Press mode does one cycle.
    /// </summary>
    private static void RunSwapAndSwing(SlotMacroConfig c, double cooldownMs, CancellationToken ct)
    {
        double gap = GapMs();
        int swordVk = SlotVk(c.SlotB);
        do
        {
            // Holding blocks: leave them alone (this also ends the run if you pick the block slot mid-fight).
            if (c.BlockSlot > 0 && Svc.Bow.CurrentSlot == (int)c.BlockSlot) return;

            Tap(c.SlotA, ct);
            Wait.Ms(5, ct);
            Click(ct);
            long fired = Wait.Now;

            InputSender.KeyScan(swordVk, true);
            try { Click(ct); }
            finally { InputSender.KeyScan(swordVk, false); }
            if (c.Style == SlotMacroStyle.Press) return;

            long swapAt = fired + Wait.FromMs(cooldownMs);
            long next = Wait.Now + Wait.FromMs(gap);
            while (!ct.IsCancellationRequested && Wait.Now < swapAt)
            {
                if (Wait.Now >= next)
                {
                    next += Wait.FromMs(gap);
                    if (next <= Wait.Now) next = Wait.Now + Wait.FromMs(gap);
                    Click(ct);
                }
                Wait.Ms(2, ct);
            }
        } while (!ct.IsCancellationRequested);
    }

    /// <summary>Hold the lasso, release, look down, swap to blocks and place five blocks.</summary>
    private static void RunLasso(SlotMacroConfig c, CancellationToken ct)
    {
        double gap = GapMs();
        Tap(c.SlotA, ct);
        Wait.Ms(30, ct);
        InputSender.MouseButton(ClickButton.Left, true);
        try { Wait.Ms(Math.Max(0, c.DelayMs), ct); }
        finally { InputSender.MouseButton(ClickButton.Left, false); }
        if (ct.IsCancellationRequested) return;
        Flick(c.LookDown, ct);
        Wait.Ms(20, ct);
        Tap(c.SlotB, ct);
        Wait.Ms(30, ct);
        for (int i = 0; i < 5 && !ct.IsCancellationRequested; i++) { Click(ct); Wait.Ms(gap, ct); }
    }

    /// <summary>Blocks out, look down and spam clicks while active; on stop restore the exact view and go back to the sword.</summary>
    private static void RunBuildUp(SlotMacroConfig c, CancellationToken ct)
    {
        double gap = GapMs();
        Tap(c.SlotA, ct);
        Wait.Ms(20, ct);
        Flick(c.LookDown, CancellationToken.None);   // always completed so it can be undone exactly
        try
        {
            long next = Wait.Now + Wait.FromMs(gap);
            while (!ct.IsCancellationRequested)
            {
                if (Wait.Now >= next)
                {
                    next += Wait.FromMs(gap);
                    if (next <= Wait.Now) next = Wait.Now + Wait.FromMs(gap);
                    Click(ct);
                }
                Wait.Ms(2, ct);
            }
        }
        finally
        {
            Flick(-c.LookDown, CancellationToken.None);
            Wait.Ms(20, CancellationToken.None);
            Tap(c.SlotB, CancellationToken.None);
        }
    }

    /// <summary>Sword hit, guitar click, sword back, then wait; repeats while active.</summary>
    private static void RunMelody(SlotMacroConfig c, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Tap(c.SlotB, ct); Click(ct); Wait.Ms(60, ct);
            Tap(c.SlotA, ct); Click(ct); Wait.Ms(60, ct);
            Tap(c.SlotB, ct);
            Wait.Ms(Math.Max(0, c.DelayMs), ct);
        }
    }

    /// <summary>Gumdrop click, wait, then pickaxe click.</summary>
    private static void RunGingerBread(SlotMacroConfig c, CancellationToken ct)
    {
        Tap(c.SlotA, ct);
        Wait.Ms(30, ct);
        Click(ct);
        Wait.Ms(Math.Max(0, c.DelayMs), ct);
        if (ct.IsCancellationRequested) return;
        Tap(c.SlotB, ct);
        Wait.Ms(30, ct);
        Click(ct);
    }
}

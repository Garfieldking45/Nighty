using System.Collections.Concurrent;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Hotbar macros: each one swaps between numbered hotbar slots, clicks and (for some) looks down, on its own
/// high-priority thread. Every run can be cancelled at any moment; held buttons and the camera are always restored.
/// </summary>
public sealed class SlotMacroService
{
    private const double WhimCooldownMs = 1100, CrossbowCooldownMs = 1320;
    private readonly ConcurrentDictionary<SlotMacroKind, CancellationTokenSource> _running = new();
    public event Action? Changed;

    public bool IsRunning(SlotMacroKind kind) => _running.ContainsKey(kind);
    public bool AnyRunning => !_running.IsEmpty;

    /// <summary>Press: start a run (ignored while one is running). Toggle: start or stop.</summary>
    public void Start(SlotMacroConfig c)
    {
        if (!c.Enabled || Svc.Hotkeys.Suspended || RobloxService.IsOwnWindowForeground()) return;
        if (c.OnlyWhenRobloxFocused && !Svc.Roblox.IsForeground) return;
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(c.Kind, cts)) { cts.Dispose(); return; }
        var snap = c.Snapshot();
        Changed?.Invoke();
        new Thread(() => Run(snap, cts)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Nighty " + c.Kind }.Start();
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

    private static int SlotVk(double slot) => 0x31 + (int)Math.Clamp(slot, 1, 9) - 1;

    private static void Tap(double slot, CancellationToken ct)
    {
        int vk = SlotVk(slot);
        InputSender.Key(vk, true);
        Wait.Ms(5, ct);
        InputSender.Key(vk, false);
    }

    private static void Click(ClickButton b, CancellationToken ct, double holdMs = 12)
    {
        InputSender.MouseButton(b, true);
        try { Wait.Ms(holdMs, ct); }
        finally { InputSender.MouseButton(b, false); }   // never leave the button stuck down
    }

    /// <summary>Clicks at the auto clicker's speed until <paramref name="untilTicks"/> (or forever when 0).</summary>
    private static void SpamClicks(CancellationToken ct, long untilTicks = 0)
    {
        double cps = Math.Clamp(Svc.S.Clicker.UseRange ? Svc.S.Clicker.MaxCps : Svc.S.Clicker.Cps, 1, 50);
        double period = 1000.0 / cps;
        long next = Wait.Now;
        while (!ct.IsCancellationRequested && (untilTicks == 0 || Wait.Now < untilTicks))
        {
            Wait.Until(next, ct);
            if (ct.IsCancellationRequested) break;
            Click(ClickButton.Left, ct, Math.Min(12, period * 0.5));
            next += Wait.FromMs(period);
            if (Wait.Now - next > Wait.FromMs(period * 4)) next = Wait.Now;
        }
    }

    /// <summary>Moves the mouse in small steps so the game's camera follows instead of dropping one huge delta.</summary>
    private static void Look(int dy, CancellationToken ct)
    {
        int left = Math.Abs(dy), sign = dy < 0 ? -1 : 1;
        while (left > 0)
        {
            int part = Math.Min(left, 150);
            InputSender.MoveRelative(0, sign * part);
            left -= part;
            if (left > 0) Wait.Ms(2, CancellationToken.None);
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
                case SlotMacroKind.Crossbow: RunSwapAndClick(c, CrossbowCooldownMs, ct); break;
                case SlotMacroKind.Whim: RunSwapAndClick(c, WhimCooldownMs, ct); break;
                case SlotMacroKind.Lasso: RunLasso(c, ct); break;
                case SlotMacroKind.BuildUp: RunBuildUp(c, ct); break;
                case SlotMacroKind.Melody: RunMelody(c, ct); break;
                case SlotMacroKind.GingerBread: RunGingerBread(c, ct); break;
            }
        }
        catch (Exception ex) { Log.Error("Quick macro " + c.Kind + " failed", ex); }
        finally
        {
            NativeMethods.timeEndPeriod(1);
            _running.TryRemove(new KeyValuePair<SlotMacroKind, CancellationTokenSource>(c.Kind, cts));
            cts.Dispose();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }

    /// <summary>Weapon out, fire, sword back, then click through the cooldown; repeats until stopped.</summary>
    private static void RunSwapAndClick(SlotMacroConfig c, double cooldownMs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long start = Wait.Now;
            Tap(c.SlotA, ct); Wait.Ms(14, ct);
            Click(ClickButton.Left, ct, 16); Wait.Ms(30, ct);
            Tap(c.SlotB, ct);
            SpamClicks(ct, start + Wait.FromMs(cooldownMs));
        }
    }

    /// <summary>Hold the lasso, look down, swap to blocks and place one under you.</summary>
    private static void RunLasso(SlotMacroConfig c, CancellationToken ct)
    {
        Tap(c.SlotA, ct); Wait.Ms(40, ct);
        InputSender.MouseButton(ClickButton.Left, true);
        try { Wait.Ms(c.DelayMs, ct); }
        finally { InputSender.MouseButton(ClickButton.Left, false); }
        if (ct.IsCancellationRequested) return;
        Look(c.LookDown, ct);
        Tap(c.SlotB, ct); Wait.Ms(40, ct);
        if (!ct.IsCancellationRequested) Click(ClickButton.Left, ct);
    }

    /// <summary>Blocks out, look down and spam clicks while held; on release restore the view and go back to the sword.</summary>
    private static void RunBuildUp(SlotMacroConfig c, CancellationToken ct)
    {
        Tap(c.SlotA, ct); Wait.Ms(30, ct);
        Look(c.LookDown, ct);
        try { SpamClicks(ct); }
        finally
        {
            Look(-c.LookDown, ct);
            Wait.Ms(10, CancellationToken.None);
            Tap(c.SlotB, CancellationToken.None);
        }
    }

    /// <summary>Sword hit, guitar out and click, sword back, then wait; repeats while held.</summary>
    private static void RunMelody(SlotMacroConfig c, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Click(ClickButton.Left, ct); Wait.Ms(20, ct);
            Tap(c.SlotA, ct); Wait.Ms(25, ct);
            Click(ClickButton.Left, ct); Wait.Ms(25, ct);
            Tap(c.SlotB, ct);
            Wait.Ms(c.DelayMs, ct);
        }
    }

    /// <summary>Gumdrop out and click, wait, then pickaxe out and click.</summary>
    private static void RunGingerBread(SlotMacroConfig c, CancellationToken ct)
    {
        Tap(c.SlotA, ct); Wait.Ms(25, ct);
        Click(ClickButton.Left, ct);
        Wait.Ms(c.DelayMs, ct);
        if (ct.IsCancellationRequested) return;
        Tap(c.SlotB, ct); Wait.Ms(25, ct);
        Click(ClickButton.Left, ct);
    }
}

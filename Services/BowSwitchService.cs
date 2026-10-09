using System.Diagnostics;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Bow Switch: selects the bow's hotbar slot, waits the configured delay, optionally fires, then returns to the
/// previous slot. All timings come from settings so they can be tuned. Cancels cleanly and never runs twice at once.
/// </summary>
public sealed class BowSwitchService
{
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts != null;
    public double LastRunMs { get; private set; }
    private long _lastStart;

    /// <summary>Called by the auto loop; starts a switch when fighting and the cooldown has elapsed.</summary>
    public void TryAuto()
    {
        var s = Svc.S.Bow;
        if (_cts != null || Environment.TickCount64 - _lastStart < s.CooldownMs) return;
        if (s.OnlyWhileFighting)
        {
            if (!Svc.Clicker.IsClicking) return;   // only the auto clicker counts, never manual clicks
        }
        Trigger();
    }
    public event Action? Changed;

    public void Trigger(int startDelayMs = 0)
    {
        var s = Svc.S.Bow;
        if (_cts != null) return;
        if (startDelayMs == 0 && (RobloxService.IsOwnWindowForeground() || (s.OnlyWhenRobloxFocused && !Svc.Roblox.IsForeground))) return;
        var cts = _cts = new CancellationTokenSource();
        _lastStart = Environment.TickCount64;
        Changed?.Invoke();
        Task.Run(() => Run(s, startDelayMs, cts.Token));
    }

    public void Stop() { _cts?.Cancel(); }

    private static int SlotVk(double slot) => 0x31 + (int)Math.Clamp(slot, 1, 9) - 1;

    private void Run(BowSwitchSettings s, int startDelay, CancellationToken ct)
    {
        NativeMethods.timeBeginPeriod(1);
        bool buttonDown = false;
        try
        {
            if (startDelay > 0) Wait.Ms(startDelay, ct);
            var sw = Stopwatch.StartNew();
            Tap(SlotVk(s.BowSlot), ct);
            if (s.Shoot)
            {
                Wait.Ms(s.SwitchDelayMs, ct);
                InputSender.MouseButton(s.ShootButton, true); buttonDown = true;
                Wait.Ms(s.HoldMs, ct);
                InputSender.MouseButton(s.ShootButton, false); buttonDown = false;
            }
            if (s.ReturnToSlot)
            {
                Wait.Ms(s.Shoot ? s.ReturnDelayMs : s.SwitchDelayMs, ct);
                Tap(SlotVk(s.ReturnSlot), ct);
            }
            LastRunMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) { Log.Error("Bow switch failed", ex); }
        finally
        {
            if (buttonDown) InputSender.MouseButton(s.ShootButton, false);
            NativeMethods.timeEndPeriod(1);
            _cts = null;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }

    private static void Tap(int vk, CancellationToken ct)
    {
        InputSender.Key(vk, true);
        Wait.Ms(15, ct);
        InputSender.Key(vk, false);
    }
}

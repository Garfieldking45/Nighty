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
    // Fixed, tuned timings (no user setting): crossbow reload is 1.3 s start-to-start; the shot follows the equip by a
    // few ms, the click is held just long enough to register, and the sword comes back right after.
    private const double CooldownMs = 1320, SwitchDelayMs = 14, HoldMs = 16, ReturnDelayMs = 30;

    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts != null;
    public double LastRunMs { get; private set; }
    private long _lastStart;

    /// <summary>Called by the auto loop; starts a switch when fighting and the cooldown has elapsed.</summary>
    public void TryAuto()
    {
        var s = Svc.S.Bow;
        if (!s.Enabled || s.Mode != BowMode.Auto || Svc.Hotkeys.Suspended) return;
        if (s.BlockSlot > 0 && _slot == (int)s.BlockSlot) return;   // holding blocks: leave them alone
        if (_cts != null || Wait.Now - _lastStart < Wait.FromMs(CooldownMs)) return;
        if (s.OnlyWhileFighting)
        {
            if (!Svc.Clicker.IsClicking) return;   // only the auto clicker counts, never manual clicks
        }
        Trigger(0, true);
    }
    public event Action? Changed;

    public void Trigger(int startDelayMs = 0, bool fromClicker = false)
    {
        var s = Svc.S.Bow;
        if (_cts != null) return;
        if (startDelayMs == 0 && !fromClicker && (RobloxService.IsOwnWindowForeground() || (s.OnlyWhenRobloxFocused && !Svc.Roblox.IsForeground))) return;
        CancellationTokenSource cts;
        lock (_gate)   // the clicker thread and the UI timer can both ask at once
        {
            if (_cts != null) return;
            cts = _cts = new CancellationTokenSource();
        }
        _lastStart = Wait.Now;
        Changed?.Invoke();
        // Own high-priority thread: the thread pool can add several ms before the first key is even sent.
        new Thread(() => Run(s, startDelayMs, cts.Token)) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "Nighty bow switch" }.Start();
    }

    private readonly object _gate = new();

    // Last hotbar slot picked with a number key (0 = unknown). Polled on a light background thread; our own
    // injected taps are ignored while a switch runs.
    private volatile int _slot;
    /// <summary>Last hotbar slot picked with a number key (0 = unknown).</summary>
    public int CurrentSlot => _slot;
    public event Action? SlotChanged;
    private Thread? _slotWatch;

    public void StartSlotTracking()
    {
        if (_slotWatch != null) return;
        _slotWatch = new Thread(() =>
        {
            var wasDown = new bool[9];
            while (true)
            {
                for (int i = 0; i < 9; i++)
                {
                    bool d = (NativeMethods.GetAsyncKeyState(0x31 + i) & 0x8000) != 0;
                    if (d && !wasDown[i] && _cts == null && _slot != i + 1) { _slot = i + 1; try { SlotChanged?.Invoke(); } catch { } }
                    wasDown[i] = d;
                }
                Thread.Sleep(4);
            }
        }) { IsBackground = true, Name = "Nighty slot tracker" };
        _slotWatch.Start();
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
            // One absolute timeline: each step is scheduled from the start, so sleep overshoot never adds up.
            int back = s.ReturnToPrevious && _slot != 0 && _slot != (int)s.BowSlot ? _slot : (int)s.ReturnSlot;
            long t0 = Wait.Now, t = t0;
            Tap(SlotVk(s.BowSlot), ct);
            if (s.Shoot)
            {
                t += Wait.FromMs(SwitchDelayMs); Wait.Until(t, ct);
                InputSender.MouseButton(s.ShootButton, true); buttonDown = true;
                t += Wait.FromMs(HoldMs); Wait.Until(t, ct);
                InputSender.MouseButton(s.ShootButton, false); buttonDown = false;
            }
            if (s.ReturnToSlot)
            {
                t += Wait.FromMs(s.Shoot ? ReturnDelayMs : SwitchDelayMs); Wait.Until(t, ct);
                Tap(SlotVk(back), ct); _slot = back;
            }
            LastRunMs = (Wait.Now - t0) / (double)Wait.FromMs(1);
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
        Wait.Ms(4, ct);
        InputSender.Key(vk, false);
    }
}

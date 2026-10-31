using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// TEST engine for the crossbow switch. Instead of spamming clicks and hoping, it sends exactly one sword click per cooldown (a gap a little
/// over the sword's 0.3 s), so every click is a hit and it always knows when the next one is due. The crossbow cycle (weapon key, shot,
/// sword key) then runs right after a click, inside the quiet gap, and is back on the sword before the next click is due. Over 10 s that is
/// about 33 sword hits plus a crossbow hit every 1.3 s.
/// </summary>
internal sealed class SyncedRunner
{
    public sealed record Settings(double SwordGapMs, double SwordHoldMs, double EquipMs, double ShotHoldMs, double SwapMs, double CrossbowCooldownMs, int CrossbowSlot, int SwordSlot, SyncedRunner.Part Part = SyncedRunner.Part.Both, double PostSwapPauseMs = 0);

    public sealed record Result(long SwordClicks, long Shots, long CyclesSkipped, long LateClicks, double MaxLateMs);

    /// <summary>Where the presses go. The real one is SendInput; tests record them.</summary>
    internal interface IOutput
    {
        void Mouse(bool down);
        void Key(int vk, bool down);
        /// <summary>True while input must stay quiet (Nighty itself is in front).</summary>
        bool Quiet();
    }

    /// <summary>Which half to run, for diagnosing where hits go missing.</summary>
    public enum Part { Both, SwordOnly, BowOnly }

    private sealed class RealOutput : IOutput
    {
        public void Mouse(bool down) => InputSender.MouseButton(ClickButton.Left, down);
        public void Key(int vk, bool down) => InputSender.KeyScan(vk, down);
        public bool Quiet() => RobloxService.IsOwnWindowForeground();
    }

    private const double KeyHoldMs = 20, SwordKeyHoldMs = 10;
    public static int SlotVk(int slot) => 0x31 + Math.Clamp(slot, 1, 9) - 1;

    /// <summary>How long one crossbow cycle takes, from the weapon key going down to the sword key coming back up.</summary>
    public static double CycleMs(Settings s) => Math.Max(s.EquipMs, KeyHoldMs + 2) + Math.Max(s.ShotHoldMs, 10) + Math.Max(0, s.SwapMs) + SwordKeyHoldMs;

    /// <summary>Runs for <paramref name="windowMs"/> (or until cancelled) on the calling thread and returns what it sent.</summary>
    public static Result Run(Settings s, double windowMs, CancellationToken ct, IOutput? output = null, bool hitFix = false)
    {
        var o = output ?? new RealOutput();
        long swordClicks = 0, shots = 0, skipped = 0, late = 0, maxLateTicks = 0;
        bool mouseDown = false;
        int keyDown = 0;
        void Mouse(bool d) { o.Mouse(d); mouseDown = d; }
        void Key(int vk, bool d) { o.Key(vk, d); keyDown = d ? vk : 0; }

        int bowVk = SlotVk(s.CrossbowSlot), swordVk = SlotVk(s.SwordSlot);
        double equip = Math.Max(s.EquipMs, KeyHoldMs + 2), shotHold = Math.Max(s.ShotHoldMs, 10), swap = Math.Max(0, s.SwapMs);
        double cycleMs = CycleMs(s);
        double gapMs = Math.Max(50, s.SwordGapMs);
        long gap = Wait.FromMs(gapMs), minGap = Wait.FromMs(Math.Min(301.5, gapMs)), cooldown = Wait.FromMs(Math.Max(800, s.CrossbowCooldownMs));
        long start = Wait.Now, end = start + Wait.FromMs(windowMs);
        long nextSword = start, lastShot = long.MinValue / 2;

        NativeMethods.timeBeginPeriod(1);
        using var boost = ThreadBoost.Apply(hitFix);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Wait.Until(nextSword, ct, 1.0);
                if (ct.IsCancellationRequested || Wait.Now >= end) break;
                if (o.Quiet()) { nextSword = Wait.Now + gap; continue; }   // never click into Nighty's own window

                long due = nextSword, now = Wait.Now;
                long lateTicks = now - due;
                if (lateTicks > maxLateTicks) maxLateTicks = lateTicks;
                if (lateTicks > Wait.FromMs(1)) late++;

                // 1) the sword click: this one is a hit
                if (s.Part != Part.BowOnly)
                {
                    Mouse(true);
                    Wait.Until(now + Wait.FromMs(Math.Max(4, s.SwordHoldMs)), ct, 1.0);
                    Mouse(false);
                    swordClicks++;
                }
                long clickEnd = Wait.Now;
                // Stay on the grid, but never closer than the sword's cooldown to the click just sent: after a late click the next one would
                // otherwise come early and be swallowed.
                nextSword = Math.Max(due + gap, now + minGap);
                if (nextSword < clickEnd) nextSword = clickEnd + gap;   // fell far behind: start over from now instead of bursting

                // 2) the crossbow, inside this gap: the cycle starts so the shot leaves exactly when the cooldown is up (never earlier, or the game
                //    swallows it), and the whole cycle must be finished before the next sword click. If it is not ready within this gap, wait for the next.
                if (s.Part == Part.SwordOnly) continue;
                long readyShot = lastShot + cooldown;
                long equipTicks = Wait.FromMs(equip);
                if (readyShot - equipTicks >= nextSword) continue;                       // not ready yet
                long c0 = Math.Max(clickEnd, readyShot - equipTicks);
                if (c0 + Wait.FromMs(cycleMs + 6 + s.PostSwapPauseMs) > nextSword) { skipped++; continue; }   // ready, but the cycle would run into the next click
                if (c0 > Wait.Now) Wait.Until(c0, ct, 1.0);
                if (ct.IsCancellationRequested) break;
                c0 = Wait.Now;
                Key(bowVk, true);
                Wait.Until(c0 + Wait.FromMs(KeyHoldMs), ct, 1.0);
                Key(bowVk, false);
                Wait.Until(c0 + Wait.FromMs(equip), ct, 1.0);
                long shotAt = Wait.Now;
                Mouse(true);
                Wait.Until(shotAt + Wait.FromMs(shotHold), ct, 1.0);
                Mouse(false);
                lastShot = shotAt;
                shots++;
                Wait.Until(shotAt + Wait.FromMs(shotHold + swap), ct, 1.0);
                Key(swordVk, true);
                Wait.Until(Wait.Now + Wait.FromMs(SwordKeyHoldMs), ct, 1.0);
                Key(swordVk, false);
                if (s.PostSwapPauseMs > 0) Wait.Until(Wait.Now + Wait.FromMs(s.PostSwapPauseMs), ct, 1.0);
            }
        }
        finally
        {
            // Whatever happened, nothing is left held down.
            if (mouseDown) o.Mouse(false);
            if (keyDown != 0) o.Key(keyDown, false);
            NativeMethods.timeEndPeriod(1);
        }
        return new Result(swordClicks, shots, skipped, late, Wait.ToMs(maxLateTicks));
    }
}

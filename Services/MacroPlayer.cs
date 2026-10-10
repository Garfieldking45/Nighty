using System.Diagnostics;
using System.Windows;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

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
        var steps = m.Steps.Select(s => new MacroStep { Type = s.Type, Value = s.Value, Value2 = s.Value2, Value3 = s.Value3, Text = s.Text }).ToList();
        double speed = m.Speed <= 0 ? 1 : m.Speed;
        int repeatDelay = m.RepeatDelayMs;
        int repeat = m.Repeat;
        Changed?.Invoke();
        Task.Run(() => Run(m.Id, steps, repeat, startDelayMs, speed, repeatDelay, cts));
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

    private void Run(Guid id, List<MacroStep> steps, int repeat, int startDelay, double speed, int repeatDelay, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var held = new HashSet<int>();
        try
        {
            NativeMethods.timeBeginPeriod(1);
            if (startDelay > 0) Wait.Ms(startDelay, ct);
            for (int i = 0; (repeat == 0 || i < repeat) && !ct.IsCancellationRequested; i++)
            {
                foreach (var step in steps)
                {
                    var type = step.Type; int value = step.Value;
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
                        case MacroStepType.MouseDown:
                            InputSender.MouseButton((ClickButton)Math.Clamp(value, 0, 2), true); break;
                        case MacroStepType.MouseUp:
                            InputSender.MouseButton((ClickButton)Math.Clamp(value, 0, 2), false); break;
                        case MacroStepType.DoubleClick:
                            var db = (ClickButton)Math.Clamp(value, 0, 2);
                            for (int d = 0; d < 2 && !ct.IsCancellationRequested; d++) { InputSender.MouseButton(db, true); Wait.Ms(15, ct); InputSender.MouseButton(db, false); if (d == 0) Wait.Ms(40, ct); }
                            break;
                        case MacroStepType.AutoClick:
                        {
                            // clicks at the step's own speed for its duration, on the same exact timeline as the clicker
                            var ab = (ClickButton)Math.Clamp(step.Value3, 0, 2);
                            double period = 1000.0 / Math.Clamp(value, 1, 100);
                            long end = Wait.Now + Wait.FromMs(Math.Max(1, step.Value2) / speed), next = Wait.Now;
                            while (!ct.IsCancellationRequested && Wait.Now < end)
                            {
                                InputSender.MouseButton(ab, true);
                                try { Wait.Ms(Math.Max(1, period * 0.45), ct); } finally { InputSender.MouseButton(ab, false); }
                                next += Wait.FromMs(period);
                                if (next < Wait.Now) next = Wait.Now + Wait.FromMs(period);
                                Wait.Until(Math.Min(next, end), ct);
                            }
                            break;
                        }
                        case MacroStepType.KeyCombo:
                        {
                            int mods = step.Value3;
                            var pressed = new List<int>();
                            if ((mods & Hotkeys.Ctrl) != 0) pressed.Add(0x11);
                            if ((mods & Hotkeys.Alt) != 0) pressed.Add(0x12);
                            if ((mods & Hotkeys.Shift) != 0) pressed.Add(0x10);
                            if ((mods & Hotkeys.Win) != 0) pressed.Add(0x5B);
                            try
                            {
                                foreach (var k in pressed) InputSender.Key(k, true);
                                InputSender.Key(value, true); Wait.Ms(30, ct); InputSender.Key(value, false);
                            }
                            finally { foreach (var k in pressed) InputSender.Key(k, false); }
                            break;
                        }
                        case MacroStepType.MoveTo:
                            NativeMethods.SetCursorPos(value, step.Value2); break;
                        case MacroStepType.Wait:
                            Wait.Ms(value / speed, ct); break;
                        case MacroStepType.RandomWait:
                            int lo = Math.Min(value, step.Value2), hi = Math.Max(value, step.Value2);
                            Wait.Ms(Random.Shared.Next(lo, hi + 1) / speed, ct); break;
                        case MacroStepType.Scroll:
                            InputSender.Scroll(value); break;
                        case MacroStepType.MoveMouse:
                            InputSender.MoveRelative(value, step.Value2); break;
                        case MacroStepType.TypeText:
                            InputSender.TypeText(step.Text, ct); break;
                    }
                }
                if (repeatDelay > 0) Wait.Ms(repeatDelay, ct);
                if (repeatDelay == 0 && steps.All(s => s.Type is not (MacroStepType.Wait or MacroStepType.RandomWait))) Wait.Ms(5, ct);   // avoid a hot loop with no delays
            }
        }
        catch (Exception ex) { Log.Error("Macro failed", ex); }
        finally
        {
            foreach (var vk in held) InputSender.Key(vk, false);
            NativeMethods.timeEndPeriod(1);
            _running.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, cts));   // only our own entry, never a newer run
            cts.Dispose();
            Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        }
    }
}

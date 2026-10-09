using System.Runtime.InteropServices;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Keeps opposite movement keys from cancelling each other. A low-level keyboard hook watches A/D and W/S; when both
/// keys of a pair are held, the chosen mode decides which one the game sees. Key events injected by Nighty or any
/// other program are never touched, and the hook only exists while the feature is on.
/// </summary>
public sealed class SocdService
{
    private sealed class Axis
    {
        public required int Neg, Pos;        // virtual-key codes (A/D or S/W)
        public bool NegDown, PosDown;        // what the keyboard is physically doing
        public bool NegOut, PosOut;          // what the game has been told
        public bool NegFirst;                // the negative key went down before the positive one
        public bool Enabled;
    }

    private readonly Axis _h = new() { Neg = 0x41, Pos = 0x44 };   // A, D
    private readonly Axis _v = new() { Neg = 0x53, Pos = 0x57 };   // S, W
    private readonly SocdSettings _s;
    private readonly object _gate = new();
    private IntPtr _hook;
    private NativeMethods.LowLevelProc? _proc;

    public SocdService(SocdSettings settings)
    {
        _s = settings;
        _s.PropertyChanged += (_, _) => Sync();
    }

    public bool IsActive => _hook != IntPtr.Zero;

    /// <summary>Installs or removes the hook to match the settings.</summary>
    public void Sync()
    {
        lock (_gate) { _h.Enabled = _s.LeftRight; _v.Enabled = _s.ForwardBack; }
        if (_s.Enabled && (_s.LeftRight || _s.ForwardBack)) Start(); else Stop();
    }

    private void Start()
    {
        if (IsActive) return;
        lock (_gate)
            foreach (var a in new[] { _h, _v })
            {
                a.NegDown = a.NegOut = (NativeMethods.GetAsyncKeyState(a.Neg) & 0x8000) != 0;
                a.PosDown = a.PosOut = (NativeMethods.GetAsyncKeyState(a.Pos) & 0x8000) != 0;
                a.NegFirst = a.NegDown;
            }
        _proc = Callback;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { Log.Warn("Keyboard hook could not be installed"); _proc = null; }
    }

    public void Stop()
    {
        if (!IsActive) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        // Never leave a key stuck down in the game: release anything we reported down that is physically up.
        lock (_gate)
            foreach (var a in new[] { _h, _v })
            {
                if (a.NegOut && !a.NegDown) InputSender.Key(a.Neg, false);
                if (a.PosOut && !a.PosDown) InputSender.Key(a.Pos, false);
                a.NegOut = a.PosOut = a.NegDown = a.PosDown = false;
            }
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                int vk = Marshal.ReadInt32(lParam, 0);                                  // KBDLLHOOKSTRUCT.vkCode
                bool injected = ((uint)Marshal.ReadInt32(lParam, 8) & NativeMethods.LLKHF_INJECTED) != 0;   // .flags
                int msg = (int)wParam;
                bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
                if (!injected && (down || up) && Handle(vk, down)) return (IntPtr)1;
            }
            catch (Exception ex) { Log.Warn("Movement helper hook error", ex); }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Returns true when the original key event must be swallowed.</summary>
    private bool Handle(int vk, bool down)
    {
        Axis? a = vk == _h.Neg || vk == _h.Pos ? _h : vk == _v.Neg || vk == _v.Pos ? _v : null;
        if (a == null) return false;
        lock (_gate)
        {
            if (!a.Enabled) return false;
            bool isNeg = vk == a.Neg;
            bool wasDown = isNeg ? a.NegDown : a.PosDown;
            if (down && !wasDown) a.NegFirst = isNeg ? !a.PosDown : a.NegDown;   // remember who got there first
            if (isNeg) a.NegDown = down; else a.PosDown = down;

            var (wantNeg, wantPos) = Resolve(a);
            bool wantSelf = isNeg ? wantNeg : wantPos;
            bool outSelf = isNeg ? a.NegOut : a.PosOut;

            // Bring the other key of the pair in line first, then decide what happens to this event.
            if (isNeg) { if (a.PosOut != wantPos) { a.PosOut = wantPos; InputSender.Key(a.Pos, wantPos); } }
            else { if (a.NegOut != wantNeg) { a.NegOut = wantNeg; InputSender.Key(a.Neg, wantNeg); } }

            if (wantSelf == outSelf) return !(down && wantSelf);   // key repeat passes while it counts as held; anything else is a no-op
            if (isNeg) a.NegOut = wantSelf; else a.PosOut = wantSelf;
            if (wantSelf == down) return false;                    // the real event already says the right thing
            InputSender.Key(vk, wantSelf);
            return true;
        }
    }

    private (bool Neg, bool Pos) Resolve(Axis a)
    {
        if (a.NegDown && !a.PosDown) return (true, false);
        if (a.PosDown && !a.NegDown) return (false, true);
        if (!a.NegDown) return (false, false);
        // Both held.
        return _s.Mode switch
        {
            SocdMode.Neutral => (false, false),
            SocdMode.FirstInput => a.NegFirst ? (true, false) : (false, true),
            _ => a.NegFirst ? (false, true) : (true, false),       // LastInput
        };
    }
}

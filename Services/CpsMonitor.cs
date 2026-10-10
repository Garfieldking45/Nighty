using System.Diagnostics;
using System.Windows;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Counts real mouse clicks (including synthetic ones) with a low-level mouse hook. The hook is only installed
/// while something needs it and is always removed on <see cref="Stop"/>.
/// </summary>
public sealed class CpsMonitor
{
    private IntPtr _hook;
    private NativeMethods.LowLevelProc? _proc;   // keep the delegate alive
    private uint _threadId;
    private bool _running;
    private readonly Queue<long> _left = new(), _right = new(), _leftInj = new(), _rightInj = new();
    private readonly object _gate = new();

    public bool IsActive => _running;

    /// <summary>
    /// The hook lives on its own message-pump thread. A low-level hook on the UI thread makes every injected click
    /// (the auto clicker's SendInput) wait for the UI thread, which can freeze the app while clicking.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            _proc = Callback;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            _threadId = NativeMethods.GetCurrentThreadId();
            if (_hook == IntPtr.Zero) Log.Warn("Mouse hook could not be installed");
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "Nighty CPS hook" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait(2000);
        if (_hook == IntPtr.Zero) _running = false;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    public (int Left, int Right) Read() => Read(CpsSource.Both);

    /// <summary>Clicks in the last second. Mine = real mouse presses, Clicker = presses sent by programs (the auto clicker, macros).</summary>
    public (int Left, int Right) Read(CpsSource source)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            foreach (var q in new[] { _left, _right, _leftInj, _rightInj })
                while (q.Count > 0 && now - q.Peek() > 1000) q.Dequeue();
            int l = source == CpsSource.Clicker ? _leftInj.Count : source == CpsSource.Mine ? _left.Count : _left.Count + _leftInj.Count;
            int r = source == CpsSource.Clicker ? _rightInj.Count : source == CpsSource.Mine ? _right.Count : _right.Count + _rightInj.Count;
            return (l, r);
        }
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            bool injected = (System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 12) & 1) != 0;   // MSLLHOOKSTRUCT.flags: LLMHF_INJECTED
            lock (_gate)
            {
                if (msg == NativeMethods.WM_LBUTTONDOWN) (injected ? _leftInj : _left).Enqueue(Environment.TickCount64);
                else if (msg == NativeMethods.WM_RBUTTONDOWN) (injected ? _rightInj : _right).Enqueue(Environment.TickCount64);
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }
}

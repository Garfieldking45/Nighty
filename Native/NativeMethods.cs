using System.Runtime.InteropServices;

namespace Nighty.Native;

internal static class NativeMethods
{
    // ---------- SendInput ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8,
        MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_MOVE = 0x1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    // ---------- Low level hooks ----------
    public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
    public const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const uint LLKHF_INJECTED = 0x10;
    public const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    public const uint WM_QUIT = 0x12;
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    // ---------- System parameters ----------
    public const uint SPI_GETMOUSE = 0x3, SPI_SETMOUSE = 0x4, SPI_GETKEYBOARDSPEED = 0xA, SPI_SETKEYBOARDSPEED = 0xB,
        SPI_GETKEYBOARDDELAY = 0x16, SPI_SETKEYBOARDDELAY = 0x17,
        SPI_GETFILTERKEYS = 0x32, SPI_SETFILTERKEYS = 0x33, SPI_GETTOGGLEKEYS = 0x34, SPI_SETTOGGLEKEYS = 0x35,
        SPI_GETSTICKYKEYS = 0x3A, SPI_SETSTICKYKEYS = 0x3B, SPI_GETMOUSESPEED = 0x70, SPI_SETMOUSESPEED = 0x71;
    public const uint SPIF_UPDATEINIFILE = 0x1, SPIF_SENDCHANGE = 0x2;

    [StructLayout(LayoutKind.Sequential)] public struct STICKYKEYS { public uint cbSize, dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct TOGGLEKEYS { public uint cbSize, dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct FILTERKEYS { public uint cbSize, dwFlags, iWaitMSec, iDelayMSec, iRepeatMSec, iBounceMSec; }

    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint p, out int v, uint w);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint p, IntPtr v, uint w);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint p, int[] v, uint w);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint p, ref STICKYKEYS v, uint w);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint p, ref TOGGLEKEYS v, uint w);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint p, ref FILTERKEYS v, uint w);

    // ---------- High-resolution waitable timer ----------
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr fn, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

    // ---------- Scheduling hints ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    public const int ProcessPowerThrottling = 4;
    public const uint THROTTLE_EXECUTION_SPEED = 0x1, THROTTLE_IGNORE_TIMER_RESOLUTION = 0x4;
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll", SetLastError = true)]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    // ---------- Timing / power ----------
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    // ---------- System usage ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    // ---------- Shell ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO { public uint cbSize; public long i64Size, i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
    public const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;

    // ---------- Window styles / DWM ----------
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    // ---------- Monitors / DDC-CI ----------
    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }
    [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, out uint n);
    [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)] public static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] public static extern bool SetMonitorBrightness(IntPtr h, uint value);
    [DllImport("dxva2.dll", SetLastError = true)] public static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] arr);
}

/// <summary>Thin helpers over SendInput.</summary>
internal static class InputSender
{
    [ThreadStatic] private static NativeMethods.INPUT[]? MouseBuf;
    private static readonly int Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>();

    public static void MouseButton(Models.ClickButton b, bool down)
    {
        uint flag = (b, down) switch
        {
            (Models.ClickButton.Left, true) => NativeMethods.MOUSEEVENTF_LEFTDOWN,
            (Models.ClickButton.Left, false) => NativeMethods.MOUSEEVENTF_LEFTUP,
            (Models.ClickButton.Right, true) => NativeMethods.MOUSEEVENTF_RIGHTDOWN,
            (Models.ClickButton.Right, false) => NativeMethods.MOUSEEVENTF_RIGHTUP,
            (Models.ClickButton.Middle, true) => NativeMethods.MOUSEEVENTF_MIDDLEDOWN,
            _ => NativeMethods.MOUSEEVENTF_MIDDLEUP,
        };
        var buf = MouseBuf ??= new NativeMethods.INPUT[1];
        buf[0] = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE };
        buf[0].u.mi.dwFlags = flag;
        NativeMethods.SendInput(1, buf, Size);
    }

    /// <summary>A zero-distance mouse move: exercises SendInput exactly like a click without clicking anything.</summary>
    public static void Noop()
    {
        var buf = MouseBuf ??= new NativeMethods.INPUT[1];
        buf[0] = new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE };
        buf[0].u.mi.dwFlags = NativeMethods.MOUSEEVENTF_MOVE;
        NativeMethods.SendInput(1, buf, Size);
    }

    public static void Key(int vk, bool down)
    {
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
        input.u.ki.wVk = (ushort)vk;
        input.u.ki.wScan = (ushort)NativeMethods.MapVirtualKey((uint)vk, 0);
        uint flags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP;
        if (IsExtended(vk)) flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;
        input.u.ki.dwFlags = flags;
        NativeMethods.SendInput(1, new[] { input }, Size);
    }

    private static bool IsExtended(int vk) =>
        vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x90 or 0xA3 or 0xA5 or 0x5B or 0x5C;
}

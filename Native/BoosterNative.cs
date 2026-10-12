using System.Runtime.InteropServices;
using System.Text;

namespace Nighty.Native;

/// <summary>Win32 calls used by the hardware, telemetry and Game Mode layers. Kept apart from <see cref="NativeMethods"/> so each can be read on its own.</summary>
internal static class BoosterNative
{
    // ---------- process handles ----------
    public const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_SET_INFORMATION = 0x0200, PROCESS_SET_QUOTA = 0x0100;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint GetPriorityClass(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("psapi.dll", SetLastError = true)] public static extern bool EmptyWorkingSet(IntPtr h);

    public const int ProcessPowerThrottling = 4;
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    // ---------- process list ----------
    private const uint TH32CS_SNAPPROCESS = 0x2;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);

    /// <summary>One snapshot of every process: id, parent id and image name (with ".exe" removed).</summary>
    public static List<(int Pid, string Name, int Parent)> ListProcesses()
    {
        var list = new List<(int, string, int)>(256);
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                string name = e.szExeFile ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                list.Add(((int)e.th32ProcessID, name, (int)e.th32ParentProcessID));
            }
        }
        finally { CloseHandle(snap); }
        return list;
    }

    // ---------- power ----------
    [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
    [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
    [DllImport("powrprof.dll")] public static extern uint PowerGetActualOverlayScheme(out Guid guid);
    [DllImport("powrprof.dll")] public static extern uint PowerGetEffectiveOverlayScheme(out Guid guid);
    [DllImport("powrprof.dll")] public static extern uint PowerSetActiveOverlayScheme(Guid guid);
    [DllImport("powrprof.dll")]
    public static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint flags, uint index, ref Guid buffer, ref uint size);
    [DllImport("powrprof.dll")]
    public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);
    public const uint ACCESS_SCHEME = 16;

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLen, byte[] output, uint outputLen);

    /// <summary>Per logical processor (up to 64): maximum MHz, current MHz and the MHz limit Windows has set. Null when the call fails.</summary>
    public static (uint Max, uint Current, uint Limit)[]? ProcessorPower(int logicalCount)
    {
        int n = Math.Clamp(logicalCount, 1, 64);
        var buf = new byte[n * 24];
        if (CallNtPowerInformation(11 /* ProcessorInformation */, IntPtr.Zero, 0, buf, (uint)buf.Length) != 0) return null;
        var r = new (uint, uint, uint)[n];
        for (int i = 0; i < n; i++)
        {
            int o = i * 24;
            r[i] = (BitConverter.ToUInt32(buf, o + 4), BitConverter.ToUInt32(buf, o + 8), BitConverter.ToUInt32(buf, o + 12));
        }
        return r;
    }

    // ---------- per-processor load ----------
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, byte[] buf, int len, out int returned);

    /// <summary>Idle and busy times of each logical processor in group 0, from the kernel. Null if the call fails.</summary>
    public static (long Idle, long Total)[]? ProcessorTimes(int logicalCount)
    {
        const int SystemProcessorPerformanceInformation = 8, entry = 48;   // IdleTime, KernelTime, UserTime, ... (48 bytes each)
        int n = Math.Clamp(logicalCount, 1, 64);
        var buf = new byte[n * entry];
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, buf.Length, out int got) != 0) return null;
        int have = Math.Min(n, got / entry);
        if (have <= 0) return null;
        var r = new (long, long)[have];
        for (int i = 0; i < have; i++)
        {
            long idle = BitConverter.ToInt64(buf, i * entry), kernel = BitConverter.ToInt64(buf, i * entry + 8), user = BitConverter.ToInt64(buf, i * entry + 16);
            r[i] = (idle, kernel + user);   // kernel time already includes idle time
        }
        return r;
    }

    // ---------- CPU topology (efficiency classes) ----------
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(byte[]? info, uint len, out uint returned, IntPtr process, uint flags);

    /// <summary>The EfficiencyClass of every logical processor (higher is faster). Null if Windows does not report CPU sets.</summary>
    public static int[]? EfficiencyClasses()
    {
        GetSystemCpuSetInformation(null, 0, out uint need, IntPtr.Zero, 0);
        if (need == 0 || need > 1 << 20) return null;
        var buf = new byte[need];
        if (!GetSystemCpuSetInformation(buf, need, out uint got, IntPtr.Zero, 0)) return null;
        var list = new List<int>();
        for (int off = 0; off + 8 <= got;)
        {
            int size = BitConverter.ToInt32(buf, off), type = BitConverter.ToInt32(buf, off + 4);
            if (size <= 0) break;
            // SYSTEM_CPU_SET_INFORMATION: Size, Type, then CpuSet { Id(4), Group(2), LogicalProcessorIndex(1), CoreIndex(1), LastLevelCacheIndex(1), NumaNodeIndex(1), EfficiencyClass(1), ... }
            if (type == 0 && off + 8 + 11 <= got) list.Add(buf[off + 8 + 4 + 2 + 1 + 1 + 1 + 1]);
            off += size;
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    // ---------- windows and displays ----------
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public NativeMethods.RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFOEX info);

    /// <summary>The Windows display device name (for example \\.\DISPLAY2) that shows the main window of a process, or null.</summary>
    public static string? DisplayOfProcessWindow(IEnumerable<int> pids)
    {
        var wanted = new HashSet<int>(pids);
        string? found = null;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!wanted.Contains((int)pid)) return true;
            var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (mon != IntPtr.Zero && GetMonitorInfoW(mon, ref info)) { found = info.szDevice; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);

    /// <summary>The current mode of a display device: width, height and refresh rate in Hz.</summary>
    public static (int Width, int Height, int Hz)? CurrentDisplayMode(string device)
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(device, -1 /* ENUM_CURRENT_SETTINGS */, ref dm)) return null;
        return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
    }
}

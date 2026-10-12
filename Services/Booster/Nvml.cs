using System.Runtime.InteropServices;
using System.Text;

namespace Nighty.Services.Booster;

public sealed record NvmlReading(
    string Name,
    double? TemperatureC,
    int? GraphicsClockMhz,
    int? MemoryClockMhz,
    double? PowerWatts,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    ulong? ThrottleMask)
{
    public static string DescribeThrottle(ulong mask)
    {
        var parts = new List<string>();
        if ((mask & 0x4) != 0) parts.Add("software power cap");
        if ((mask & 0x8) != 0) parts.Add("hardware slowdown");
        if ((mask & 0x20) != 0) parts.Add("software thermal slowdown");
        if ((mask & 0x40) != 0) parts.Add("hardware thermal slowdown");
        if ((mask & 0x80) != 0) parts.Add("power brake slowdown");
        return string.Join(", ", parts);
    }

    /// <summary>True only for the reasons NVIDIA documents as heat-related.</summary>
    public bool ThermalThrottling => ThrottleMask is ulong m && (m & (0x20 | 0x40)) != 0;
    public bool PowerLimited => ThrottleMask is ulong m && (m & (0x4 | 0x80)) != 0;
}

/// <summary>
/// NVIDIA's management library (NVML). It ships with the NVIDIA driver (nvml.dll), so nothing is bundled or installed by Nighty;
/// when the library is missing every call simply reports "unavailable".
/// </summary>
public sealed class NvmlLibrary : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Handle(uint i, out IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameFn(IntPtr h, byte[] name, uint len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UIntFn(IntPtr h, out uint v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TypedUInt(IntPtr h, int type, out uint v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ULongFn(IntPtr h, out ulong v);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilFn(IntPtr h, out Util u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MemFn(IntPtr h, out MemInfo m);

    [StructLayout(LayoutKind.Sequential)] private struct Util { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemInfo { public ulong Total, Free, Used; }

    private IntPtr _lib;
    private Shutdown? _shutdown;
    private Handle? _handle;
    private NameFn? _name;
    private TypedUInt? _temp, _clock;
    private UIntFn? _power;
    private ULongFn? _throttle;
    private UtilFn? _util;
    private MemFn? _mem;
    private readonly List<IntPtr> _devices = new();

    public bool IsAvailable { get; private set; }
    public string Detail { get; private set; } = "Not tried yet";
    public IReadOnlyList<string> DeviceNames { get; private set; } = Array.Empty<string>();

    /// <summary>Tries to load the library that came with the installed NVIDIA driver.</summary>
    public static NvmlLibrary TryCreate()
    {
        var lib = new NvmlLibrary();
        lib.Load();
        return lib;
    }

    private void Load()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[] { Path.Combine(sys, "nvml.dll"), Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll") };
        string? path = candidates.FirstOrDefault(File.Exists);
        if (path == null) { Detail = "nvml.dll is not installed (no NVIDIA driver, or a driver without the management library)"; return; }
        try
        {
            if (!NativeLibrary.TryLoad(path, out _lib)) { Detail = "Could not load " + path; return; }
            T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
            T? Opt<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(_lib, name, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

            if (Fn<Init>("nvmlInit_v2")() != 0) { Detail = "The NVIDIA driver library refused to start"; return; }
            _shutdown = Fn<Shutdown>("nvmlShutdown");
            var count = Fn<Count>("nvmlDeviceGetCount_v2");
            _handle = Fn<Handle>("nvmlDeviceGetHandleByIndex_v2");
            _name = Fn<NameFn>("nvmlDeviceGetName");
            _temp = Opt<TypedUInt>("nvmlDeviceGetTemperature");
            _clock = Opt<TypedUInt>("nvmlDeviceGetClockInfo");
            _power = Opt<UIntFn>("nvmlDeviceGetPowerUsage");
            _throttle = Opt<ULongFn>("nvmlDeviceGetCurrentClocksEventReasons") != null ? Opt<ULongFn>("nvmlDeviceGetCurrentClocksEventReasons") : Opt<ULongFn>("nvmlDeviceGetCurrentClocksThrottleReasons");
            _util = Opt<UtilFn>("nvmlDeviceGetUtilizationRates");
            _mem = Opt<MemFn>("nvmlDeviceGetMemoryInfo");

            count(out uint n);
            var names = new List<string>();
            for (uint i = 0; i < n; i++)
            {
                if (_handle(i, out var h) != 0) continue;
                var buf = new byte[96];
                string nm = _name(h, buf, (uint)buf.Length) == 0 ? Encoding.ASCII.GetString(buf).TrimEnd('\0') : "NVIDIA GPU " + i;
                _devices.Add(h); names.Add(nm);
            }
            DeviceNames = names;
            IsAvailable = _devices.Count > 0;
            Detail = IsAvailable ? $"NVIDIA driver library found ({_devices.Count} device{(_devices.Count == 1 ? "" : "s")})" : "The NVIDIA driver library reports no devices";
        }
        catch (Exception ex) { Detail = "NVIDIA driver library could not be used: " + ex.Message; IsAvailable = false; }
    }

    /// <summary>Reads one device. Any value the driver will not give is left null.</summary>
    public NvmlReading? Read(int deviceIndex)
    {
        if (!IsAvailable || deviceIndex < 0 || deviceIndex >= _devices.Count) return null;
        var h = _devices[deviceIndex];
        try
        {
            double? temp = _temp != null && _temp(h, 0, out uint t) == 0 ? t : null;
            int? gfx = _clock != null && _clock(h, 0, out uint c) == 0 ? (int)c : null;
            int? mem = _clock != null && _clock(h, 2, out uint m) == 0 ? (int)m : null;
            double? pw = _power != null && _power(h, out uint mw) == 0 ? mw / 1000.0 : null;
            ulong? thr = _throttle != null && _throttle(h, out ulong r) == 0 ? r : null;
            double? util = null; long? used = null, total = null;
            if (_util != null && _util(h, out var u) == 0) util = u.Gpu;
            if (_mem != null && _mem(h, out var mi) == 0) { used = (long)mi.Used; total = (long)mi.Total; }
            return new NvmlReading(DeviceNames[deviceIndex], temp, gfx, mem, pw, util, used, total, thr);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        try { _shutdown?.Invoke(); } catch { }
        if (_lib != IntPtr.Zero) { try { NativeLibrary.Free(_lib); } catch { } _lib = IntPtr.Zero; }
        IsAvailable = false;
    }
}

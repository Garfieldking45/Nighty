using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nighty.Native;
using Vortice.DXGI;

namespace Nighty.Services.Booster;

public interface IHardwareSource
{
    /// <summary>Reads the whole PC. Read-only: nothing is changed.</summary>
    HardwareProfile Discover();
    /// <summary>Re-reads only the parts that change while Windows runs (power source, power mode, battery).</summary>
    PowerInfo ReadPower();
}

/// <summary>Classifies graphics adapters from what Windows reports. Pure so it can be tested with made-up adapters.</summary>
public static class GpuClassifier
{
    public const int NvidiaId = 0x10DE, AmdId = 0x1002, IntelId = 0x8086, MicrosoftId = 0x1414;

    private static readonly Regex AmdIntegrated = new(@"(Radeon\(TM\)\s+Graphics|Radeon\s+Graphics|\bVega\s*\d*\b|Radeon\s+\d{3}M\b|Radeon\s+Vega)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmdDiscrete = new(@"(\bRX\b|Radeon\s+Pro\b|\bPRO\s+W\b|Radeon\s+VII|\bFury\b|\bR9\b|\bR7\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GpuVendor VendorOf(int vendorId) => vendorId switch
    {
        NvidiaId => GpuVendor.Nvidia, AmdId or 0x1022 => GpuVendor.Amd, IntelId => GpuVendor.Intel, MicrosoftId => GpuVendor.Microsoft, 0 => GpuVendor.Unknown, _ => GpuVendor.Other,
    };

    public static (GpuKind Kind, string Reason) Classify(int vendorId, string name, long? dedicatedBytes, bool softwareFlag)
    {
        if (softwareFlag || vendorId == MicrosoftId) return (GpuKind.Software, "Windows lists it as a software renderer");
        const long gb15 = 1_610_612_736;
        switch (vendorId)
        {
            case NvidiaId:
                return (GpuKind.Dedicated, "NVIDIA graphics cards for PCs are dedicated GPUs");
            case IntelId:
                if (dedicatedBytes is long ib && ib >= gb15) return (GpuKind.Dedicated, $"Intel adapter with {ib / 1073741824.0:0.#} GB of its own memory (a discrete Arc card)");
                return (GpuKind.Integrated, "Intel adapter that shares system memory (no large dedicated memory)");
            case AmdId:
                if (AmdDiscrete.IsMatch(name)) return (GpuKind.Dedicated, "AMD discrete model name");
                if (AmdIntegrated.IsMatch(name)) return (GpuKind.Integrated, "AMD integrated (APU) model name");
                if (dedicatedBytes is long ab && ab >= 2_147_483_648) return (GpuKind.Dedicated, $"AMD adapter with {ab / 1073741824.0:0.#} GB of dedicated memory");
                return (GpuKind.Unknown, "AMD adapter whose name does not say whether it is integrated or discrete");
            default:
                return (GpuKind.Unknown, "Unknown graphics vendor");
        }
    }
}

/// <summary>Classifies the kind of computer from the chassis type and model strings.</summary>
public static class PlatformClassifier
{
    private static readonly int[] LaptopChassis = { 8, 9, 10, 11, 14, 18, 21, 30, 31, 32 };
    private static readonly int[] DesktopChassis = { 3, 4, 5, 6, 7, 13, 15, 16, 23, 24, 34, 35, 36 };
    private static readonly string[] VmMarkers = { "VMware", "VirtualBox", "Virtual Machine", "KVM", "QEMU", "Xen", "Parallels", "Bochs", "Hyper-V" };

    public static (PlatformKind Kind, string Evidence) Classify(IReadOnlyCollection<int> chassisTypes, string manufacturer, string model, bool batteryPresent)
    {
        string id = manufacturer + " " + model;
        var vm = VmMarkers.FirstOrDefault(m => id.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (vm != null) return (PlatformKind.VirtualMachine, $"System model \"{model}\" looks like a virtual machine ({vm})");
        // Both laptop and desktop chassis can be reported (for example by a docking station); a laptop chassis plus a battery is a laptop.
        bool lap = chassisTypes.Any(c => LaptopChassis.Contains(c)), desk = chassisTypes.Any(c => DesktopChassis.Contains(c));
        if (lap) return (PlatformKind.Laptop, $"Chassis type {string.Join(",", chassisTypes)} is a portable computer");
        if (desk) return (PlatformKind.Desktop, $"Chassis type {string.Join(",", chassisTypes)} is a desktop computer");
        if (batteryPresent) return (PlatformKind.Laptop, "No chassis type reported, but a battery is present");
        return (PlatformKind.Unknown, "Windows reports neither a recognised chassis type nor a battery");
    }
}

public sealed class WindowsHardwareSource : IHardwareSource, IDisposable
{
    private const string HighPerformanceId = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private readonly NvmlLibrary _nvml;
    private readonly bool _ownsNvml;

    public WindowsHardwareSource(NvmlLibrary? nvml = null)
    {
        _ownsNvml = nvml == null;
        _nvml = nvml ?? NvmlLibrary.TryCreate();
    }

    public NvmlLibrary Nvml => _nvml;

    public void Dispose() { if (_ownsNvml) _nvml.Dispose(); }

    public HardwareProfile Discover()
    {
        var sources = new List<DataSourceStatus>();
        T Section<T>(string id, string name, Func<T> read, T fallback, string okDetail = "Read from Windows")
        {
            try { var v = read(); sources.Add(new DataSourceStatus(id, name, true, okDetail)); return v; }
            catch (Exception ex) { Log.Warn($"Hardware discovery: {name} failed", ex); sources.Add(new DataSourceStatus(id, name, false, ex.Message)); return fallback; }
        }

        var cpu = Section("cpu", "Processor", ReadCpu, new CpuInfo("Unknown processor", "Unknown", Environment.ProcessorCount, null, null, null, RuntimeInformation.OSArchitecture.ToString()));
        var (gpus, displays) = Section("gpu", "Graphics adapters (DXGI)", ReadGpus, (new List<GpuInfo>(), new List<DisplayInfo>()), "Read from the DirectX graphics infrastructure");
        var memory = Section("memory", "Memory", ReadMemory, new MemoryInfo(0, null, null));
        var storage = Section("storage", "Storage devices", ReadStorage, new List<StorageInfo>());
        var volumes = Section("volumes", "Disk volumes", ReadVolumes, new List<VolumeInfo>());
        var os = Section("os", "Windows version", ReadOs, new OsInfo("Windows", "", Environment.OSVersion.Version.Build, 0, RuntimeInformation.OSArchitecture.ToString(), Environment.Is64BitOperatingSystem));
        var (platform, evidence, model) = Section("platform", "Computer type", ReadPlatform, (PlatformKind.Unknown, "Windows would not report the chassis type", "Unknown"));
        var power = Section("power", "Power state", ReadPower, new PowerInfo(PowerSource.Unknown, null, null, null, false, null, PowerMode.Unknown, false, false));

        // Vendor telemetry: report honestly which GPUs can be read in detail.
        bool nvmlOk = _nvml.IsAvailable;
        sources.Add(new DataSourceStatus("nvml", "NVIDIA GPU temperature, clocks and power", nvmlOk,
            nvmlOk ? _nvml.Detail : _nvml.Detail + (gpus.Any(g => g.Vendor == GpuVendor.Nvidia) ? "" : " (this PC has no NVIDIA GPU)")));
        if (gpus.Any(g => g.Vendor is GpuVendor.Amd or GpuVendor.Intel))
            sources.Add(new DataSourceStatus("gpu-vendor", "AMD / Intel GPU temperature, clocks and power", false,
                "Nighty has no vendor library for these adapters; utilization and memory use still come from Windows"));
        sources.Add(new DataSourceStatus("cpu-temp", "CPU temperature", false,
            "Windows has no reliable CPU temperature source without installing a kernel driver, which Nighty does not do"));

        bool lowRights = !Elevation.CanTraceEtw;
        var perms = new PermissionInfo(Elevation.IsAdmin, Elevation.CanTraceEtw);
        sources.Add(new DataSourceStatus("frames", "Real frame timing (Present events)", !lowRights,
            lowRights ? "Needs administrator rights or membership of the 'Performance Log Users' group (one-time setup on the Overlays page)" : "Available"));

        return new HardwareProfile(DateTimeOffset.UtcNow, cpu, gpus, displays, memory, storage, volumes, os, platform, evidence, model, power, perms, sources);
    }

    // ---------------- CPU ----------------
    private static CpuInfo ReadCpu()
    {
        string name = "Unknown processor", maker = "Unknown";
        int logical = 0, cores = 0, mhz = 0;
        bool any = false;
        using (var s = new ManagementObjectSearcher("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            foreach (ManagementObject o in s.Get())
            {
                if (!any) { name = (o["Name"] as string)?.Trim() ?? name; maker = (o["Manufacturer"] as string)?.Trim() ?? maker; any = true; }
                logical += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                mhz = Math.Max(mhz, Convert.ToInt32(o["MaxClockSpeed"] ?? 0));
                o.Dispose();
            }
        }
        if (logical <= 0) logical = Environment.ProcessorCount;
        var classes = BoosterNative.EfficiencyClasses();
        bool? hybrid = classes == null ? null : classes.Distinct().Count() > 1;
        return new CpuInfo(Regex.Replace(name, @"\s+", " "), maker, logical, cores > 0 ? cores : null, mhz > 0 ? mhz : null, hybrid, RuntimeInformation.OSArchitecture.ToString());
    }

    // ---------------- GPU + displays ----------------
    // DXGI sizes are pointer-sized; converting through 32 bits overflows on cards with 4 GB or more.
    private static ulong ToU64(SharpGen.Runtime.PointerUSize v) => ((UIntPtr)v).ToUInt64();

    private (List<GpuInfo>, List<DisplayInfo>) ReadGpus()
    {
        var wmi = new List<(string Pnp, string? Driver)>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID, DriverVersion FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get()) { wmi.Add(((o["PNPDeviceID"] as string) ?? "", o["DriverVersion"] as string)); o.Dispose(); }
        }
        catch (Exception ex) { Log.Warn("Win32_VideoController unavailable", ex); }

        var gpus = new List<GpuInfo>();
        var displays = new List<DisplayInfo>();
        var nvidiaSeen = 0;
        DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).CheckError();
        using (factory)
        {
            for (uint i = 0; factory!.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
            {
                using (adapter)
                {
                    var d = adapter!.Description1;
                    bool software = (d.Flags & AdapterFlags.Software) != 0;
                    long? dedicated = software ? null : (long)ToU64(d.DedicatedVideoMemory);
                    long? shared = software ? null : (long)ToU64(d.SharedSystemMemory);
                    var (kind, reason) = GpuClassifier.Classify((int)d.VendorId, d.Description, dedicated, software);
                    string needle = $"VEN_{d.VendorId:X4}&DEV_{d.DeviceId:X4}";
                    string? driver = wmi.FirstOrDefault(w => w.Pnp.Contains(needle, StringComparison.OrdinalIgnoreCase)).Driver;
                    var vendor = GpuClassifier.VendorOf((int)d.VendorId);
                    bool telemetry = false;
                    if (vendor == GpuVendor.Nvidia && _nvml.IsAvailable) { telemetry = nvidiaSeen < _nvml.DeviceNames.Count; nvidiaSeen++; }
                    long luid = ((long)d.Luid.HighPart << 32) | d.Luid.LowPart;
                    var info = new GpuInfo((int)i, d.Description.Trim(), vendor, (int)d.VendorId, (int)d.DeviceId, kind, luid, dedicated, shared, driver, telemetry, reason);
                    gpus.Add(info);

                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                    {
                        using (output)
                        {
                            var od = output!.Description;
                            if (!od.AttachedToDesktop) continue;
                            var mode = BoosterNative.CurrentDisplayMode(od.DeviceName);
                            if (mode is not { } m) continue;
                            bool primary = od.DesktopCoordinates.Left == 0 && od.DesktopCoordinates.Top == 0;
                            displays.Add(new DisplayInfo(od.DeviceName, (int)i, m.Width, m.Height, m.Hz, primary));
                        }
                    }
                }
            }
        }
        return (gpus, displays);
    }

    // ---------------- memory / storage ----------------
    private static MemoryInfo ReadMemory()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref m);
        int? count = null, speed = null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            int n = 0, sp = 0;
            foreach (ManagementObject o in s.Get())
            {
                n++;
                sp = Math.Max(sp, Convert.ToInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0));
                o.Dispose();
            }
            if (n > 0) { count = n; speed = sp > 0 ? sp : null; }
        }
        catch (Exception ex) { Log.Warn("Win32_PhysicalMemory unavailable", ex); }
        return new MemoryInfo((long)m.ullTotalPhys, count, speed);
    }

    private static List<StorageInfo> ReadStorage()
    {
        var list = new List<StorageInfo>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject o in s.Get())
            {
                string media = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unspecified" };
                string bus = Convert.ToInt32(o["BusType"] ?? 0) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 3 => "ATA", 10 => "SAS", _ => "Other" };
                list.Add(new StorageInfo((o["FriendlyName"] as string)?.Trim() ?? "Disk", media, bus, Convert.ToInt64(o["Size"] ?? 0)));
                o.Dispose();
            }
            if (list.Count > 0) return list;
        }
        catch (Exception ex) { Log.Warn("MSFT_PhysicalDisk unavailable; using Win32_DiskDrive", ex); }
        using var fallback = new ManagementObjectSearcher("SELECT Model, MediaType, InterfaceType, Size FROM Win32_DiskDrive");
        foreach (ManagementObject o in fallback.Get())
        {
            list.Add(new StorageInfo((o["Model"] as string)?.Trim() ?? "Disk", (o["MediaType"] as string) ?? "Unspecified", (o["InterfaceType"] as string) ?? "Other", Convert.ToInt64(o["Size"] ?? 0)));
            o.Dispose();
        }
        return list;
    }

    private static List<VolumeInfo> ReadVolumes()
    {
        string sys = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new VolumeInfo(d.Name, d.TotalSize, d.AvailableFreeSpace, string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase)));
            }
            catch { }
        }
        return list;
    }

    // ---------------- Windows / platform / power ----------------
    private static OsInfo ReadOs()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = k?.GetValue("ProductName") as string ?? "Windows";
        string display = (k?.GetValue("DisplayVersion") ?? k?.GetValue("ReleaseId")) as string ?? "";
        int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out int build);
        if (build == 0) build = Environment.OSVersion.Version.Build;
        int ubr = k?.GetValue("UBR") is int u ? u : 0;
        // Windows 11 still says "Windows 10" in ProductName.
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11");
        return new OsInfo(product, display, build, ubr, RuntimeInformation.OSArchitecture.ToString(), Environment.Is64BitOperatingSystem);
    }

    private static (PlatformKind, string, string) ReadPlatform()
    {
        string maker = "", model = "";
        using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in s.Get()) { maker = (o["Manufacturer"] as string)?.Trim() ?? ""; model = (o["Model"] as string)?.Trim() ?? ""; o.Dispose(); }
        var chassis = new List<int>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject o in s.Get()) { if (o["ChassisTypes"] is ushort[] t) chassis.AddRange(t.Select(x => (int)x)); o.Dispose(); }
        }
        catch (Exception ex) { Log.Warn("Win32_SystemEnclosure unavailable", ex); }
        bool battery = false;
        try { using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery"); battery = s.Get().Count > 0; } catch { }
        var (kind, evidence) = PlatformClassifier.Classify(chassis, maker, model, battery);
        return (kind, evidence, $"{maker} {model}".Trim());
    }

    public PowerInfo ReadPower()
    {
        var src = PowerSource.Unknown; int? pct = null; bool saver = false;
        if (BoosterNative.GetSystemPowerStatus(out var st))
        {
            src = st.ACLineStatus == 1 ? PowerSource.Ac : st.ACLineStatus == 0 ? PowerSource.Battery : PowerSource.Unknown;
            if (st.BatteryLifePercent <= 100 && (st.BatteryFlag & 128) == 0) pct = st.BatteryLifePercent;
            saver = (st.SystemStatusFlag & 1) != 0;
        }
        string? active = PowerSchemeTarget.Active();
        var plans = PowerSchemeTarget.List();
        string? activeName = plans.FirstOrDefault(p => string.Equals(p.Id, active, StringComparison.OrdinalIgnoreCase)).Name;
        bool hasHigh = plans.Any(p => string.Equals(p.Id, HighPerformanceId, StringComparison.OrdinalIgnoreCase));
        string? overlay = PowerOverlayTarget.Actual();
        return new PowerInfo(src, pct, active, activeName, hasHigh, hasHigh ? HighPerformanceId : null, PowerOverlayTarget.ToMode(overlay), overlay != null, saver);
    }
}

/// <summary>Keeps the latest <see cref="HardwareProfile"/>, rescans on demand and tells listeners when the machine changed.</summary>
public sealed class HardwareService : IDisposable
{
    private readonly IHardwareSource _source;
    private readonly object _gate = new();
    private HardwareProfile? _current;
    private bool _listening;

    public HardwareService(IHardwareSource source) => _source = source;

    public HardwareProfile? Current { get { lock (_gate) return _current; } }

    /// <summary>Raised after every scan: (previous profile, new profile, whether hardware/drivers/OS identity changed).</summary>
    public event Action<HardwareProfile?, HardwareProfile, bool>? Updated;

    /// <summary>Scans the whole PC again (slow: run it off the UI thread).</summary>
    public HardwareProfile Rescan()
    {
        var fresh = _source.Discover();
        HardwareProfile? old;
        lock (_gate) { old = _current; _current = fresh; }
        Updated?.Invoke(old, fresh, old == null || old.Fingerprint != fresh.Fingerprint);
        return fresh;
    }

    /// <summary>Re-reads only the power state (cheap). Returns true when something relevant changed.</summary>
    public bool RefreshPower()
    {
        var power = _source.ReadPower();
        HardwareProfile? old, fresh;
        lock (_gate)
        {
            old = _current;
            if (old == null) return false;
            if (old.Power == power) return false;
            fresh = old with { Power = power, CapturedAt = DateTimeOffset.UtcNow };
            _current = fresh;
        }
        Updated?.Invoke(old, fresh, false);
        return true;
    }

    /// <summary>Refreshes the power state when Windows reports a power event (plugging in, battery saver, resume).</summary>
    public void ListenForPowerChanges()
    {
        if (_listening) return;
        _listening = true;
        SystemEvents.PowerModeChanged += OnPowerEvent;
    }

    private void OnPowerEvent(object? sender, PowerModeChangedEventArgs e) => Task.Run(() => { try { RefreshPower(); } catch (Exception ex) { Log.Warn("Power refresh failed", ex); } });

    public void Dispose()
    {
        if (_listening) { SystemEvents.PowerModeChanged -= OnPowerEvent; _listening = false; }
    }
}

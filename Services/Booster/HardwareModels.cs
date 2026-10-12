using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Nighty.Services.Booster;

[JsonConverter(typeof(JsonStringEnumConverter))] public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Other }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum GpuKind { Unknown, Integrated, Dedicated, Software }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum PlatformKind { Unknown, Desktop, Laptop, VirtualMachine }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum PowerSource { Unknown, Ac, Battery }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum PowerMode { Unknown, BestPowerEfficiency, Balanced, BetterPerformance, BestPerformance }

/// <summary>
/// Nullable members mean "Nighty could not find this out". Nothing here is ever filled in with a guess; the reasons a value is
/// missing are listed in <see cref="HardwareProfile.Limitations"/>.
/// </summary>
public sealed record CpuInfo(
    string Name,
    string Manufacturer,
    int LogicalProcessors,
    int? PhysicalCores,
    int? BaseClockMhz,
    /// <summary>True when Windows reports processors with different efficiency classes (performance and efficiency cores); null when it does not report them.</summary>
    bool? HybridTopology,
    string Architecture);

public sealed record GpuInfo(
    int Index,
    string Name,
    GpuVendor Vendor,
    int VendorId,
    int DeviceId,
    GpuKind Kind,
    long Luid,
    long? DedicatedBytes,
    long? SharedBytes,
    string? DriverVersion,
    /// <summary>True when Nighty can read temperature, clocks and power for this adapter (NVIDIA only, through the driver's own library).</summary>
    bool HasVendorTelemetry,
    /// <summary>Why <see cref="Kind"/> was chosen; the classification is a judgement from vendor, name and memory, not something Windows states.</summary>
    string KindReason);

public sealed record DisplayInfo(string DeviceName, int? AdapterIndex, int Width, int Height, int RefreshHz, bool Primary);

public sealed record MemoryInfo(long TotalBytes, int? ModuleCount, int? SpeedMhz);

public sealed record StorageInfo(string Name, string MediaType, string BusType, long SizeBytes);

public sealed record VolumeInfo(string Root, long TotalBytes, long FreeBytes, bool IsSystem);

public sealed record OsInfo(string ProductName, string DisplayVersion, int Build, int Revision, string Architecture, bool Is64Bit)
{
    public override string ToString() => $"{ProductName} {DisplayVersion} (build {Build}.{Revision})".Replace("  ", " ");
}

public sealed record PowerInfo(
    PowerSource Source,
    int? BatteryPercent,
    string? ActiveSchemeId,
    string? ActiveSchemeName,
    bool HasHighPerformancePlan,
    string? HighPerformancePlanId,
    /// <summary>Windows 10/11 "Power mode"; Unknown when this PC does not report one.</summary>
    PowerMode Mode,
    bool PowerModeSupported,
    bool BatterySaverOn);

public sealed record PermissionInfo(bool IsAdmin, bool CanTraceEtw);

public sealed record DataSourceStatus(string Id, string Name, bool Available, string Detail);

public sealed record HardwareProfile(
    DateTimeOffset CapturedAt,
    CpuInfo Cpu,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<DisplayInfo> Displays,
    MemoryInfo Memory,
    IReadOnlyList<StorageInfo> Storage,
    IReadOnlyList<VolumeInfo> Volumes,
    OsInfo Os,
    PlatformKind Platform,
    string PlatformEvidence,
    string SystemModel,
    PowerInfo Power,
    PermissionInfo Permissions,
    IReadOnlyList<DataSourceStatus> Sources)
{
    [JsonIgnore] public IEnumerable<GpuInfo> DedicatedGpus => Gpus.Where(g => g.Kind == GpuKind.Dedicated);
    [JsonIgnore] public IEnumerable<GpuInfo> IntegratedGpus => Gpus.Where(g => g.Kind == GpuKind.Integrated);
    [JsonIgnore] public bool HasHybridGraphics => IntegratedGpus.Any() && DedicatedGpus.Any();
    [JsonIgnore] public bool IsLaptop => Platform == PlatformKind.Laptop;
    [JsonIgnore] public double TotalMemoryGb => Memory.TotalBytes / 1073741824.0;

    /// <summary>Things Nighty could not determine on this PC, for display next to the numbers they would have been.</summary>
    [JsonIgnore] public IEnumerable<DataSourceStatus> Limitations => Sources.Where(s => !s.Available);

    /// <summary>
    /// Changes when the hardware, drivers or operating system change, not when only the power source or free space does, so a
    /// saved plan or benchmark can be matched to the machine it was made on.
    /// </summary>
    [JsonIgnore]
    public string Fingerprint
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Cpu.Name).Append('|').Append(Cpu.LogicalProcessors).Append('|').Append((Memory.TotalBytes + 536870912) / 1073741824);
            foreach (var g in Gpus.OrderBy(g => g.Name, StringComparer.Ordinal)) sb.Append('|').Append(g.Name).Append(':').Append(g.DriverVersion);
            foreach (var s in Storage.OrderBy(s => s.Name, StringComparer.Ordinal)) sb.Append('|').Append(s.Name);
            sb.Append('|').Append(Os.Build).Append('|').Append(Platform).Append('|').Append(SystemModel);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
        }
    }

    /// <summary>A short text a person can read: "Intel Core i5 · 12 threads · 16 GB · NVIDIA GeForce GTX 1650 + Intel UHD Graphics".</summary>
    public string OneLine()
        => $"{Cpu.Name} · {Cpu.LogicalProcessors} threads · {TotalMemoryGb:0} GB · {string.Join(" + ", Gpus.Where(g => g.Kind != GpuKind.Software).Select(g => g.Name))}";
}

using System.Text.Json;
using Nighty.Services.Booster;

namespace Nighty.Tests;

/// <summary>Keeps the journal in memory and can be told to fail, so crashes and full disks can be simulated.</summary>
internal sealed class MemoryJournalStore : IJournalStore
{
    public string? Json;
    public bool FailSave;
    public LoadState ForcedLoad = LoadState.Ok;
    public int Saves;

    public JournalLoad Load()
    {
        if (ForcedLoad == LoadState.Unreadable) return new JournalLoad(new JournalFile(), LoadState.Unreadable, "corrupt");
        return Json == null ? new JournalLoad(new JournalFile(), LoadState.Missing)
            : new JournalLoad(JsonSerializer.Deserialize<JournalFile>(Json)!, LoadState.Ok);
    }

    public bool Save(JournalFile file, out string? error)
    {
        if (FailSave) { error = "disk full"; return false; }
        Json = JsonSerializer.Serialize(file);
        Saves++;
        error = null;
        return true;
    }

    public JournalFile Snapshot() => Json == null ? new JournalFile() : JsonSerializer.Deserialize<JournalFile>(Json)!;
}

internal sealed class FakeTarget : ISettingTarget
{
    public FakeTarget(string key, string? value) { Key = key; Value = value; }
    public string Key { get; }
    public string Description => "fake " + Key;
    public string? Value;
    public ReadStatus ReadAs = ReadStatus.Ok;
    public bool FailWrite;
    /// <summary>Reports success but does not change anything (a setting Windows silently ignores).</summary>
    public bool IgnoreWrite;
    public int Writes;
    public Action<string?>? BeforeWrite;

    public SettingRead Read() => ReadAs == ReadStatus.Ok ? SettingRead.Of(Value) : new SettingRead(ReadAs, null, ReadAs.ToString());

    public SettingWrite Write(string? value)
    {
        Writes++;
        BeforeWrite?.Invoke(value);
        if (FailWrite) return SettingWrite.Fail("refused");
        if (!IgnoreWrite) Value = value;
        return SettingWrite.Success;
    }
}

internal sealed class DictResolver : ITargetResolver
{
    public readonly Dictionary<string, ISettingTarget> Targets = new();
    public ISettingTarget? Resolve(string key) => Targets.GetValueOrDefault(key);
    public FakeTarget Add(FakeTarget t) { Targets[t.Key] = t; return t; }
}

internal sealed class ManualTime : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(_ticks);
    public void Advance(TimeSpan t) => _ticks += (long)t.TotalMilliseconds;
}

/// <summary>A small pretend Windows: a process list whose priorities and efficiency switches can be read, set and changed behind Nighty's back.</summary>
internal sealed class FakeWorld : IProcessSource, IProcessControl
{
    public sealed class P
    {
        public int Pid; public string Name = ""; public string? Path; public long Start;
        public string Priority = PriorityNames.Normal; public string Throttle = ThrottleState.Encode(0, 0);
        public bool Alive = true, DenyWrite; public long WorkingSet = 100 << 20; public long Trimmed;
    }

    public readonly List<P> Procs = new();
    private long _nextStart = 1000;
    private int _nextPid = 100;
    public int PriorityWrites, ThrottleWrites;

    public P Add(string name, string? path = null, int? pid = null)
    {
        var p = new P { Pid = pid ?? _nextPid++, Name = name, Path = path, Start = _nextStart++ };
        Procs.Add(p);
        return p;
    }

    public P Roblox(string folder = "Froststrap", int? pid = null)
        => Add("RobloxPlayerBeta", System.IO.Path.Combine(Local, folder, "Versions", "version-abc", "RobloxPlayerBeta.exe"), pid);

    public const string Local = @"C:\Users\Test\AppData\Local";

    public void Kill(P p) => p.Alive = false;
    public static ProcIdentity Id(P p) => new(p.Pid, p.Start);

    public IReadOnlyList<ProcEntry> List() => Procs.Where(p => p.Alive).Select(p => new ProcEntry(p.Pid, p.Name, 0)).ToList();
    public ProcDetails? Describe(int pid) => Procs.FirstOrDefault(p => p.Alive && p.Pid == pid) is { } p ? new ProcDetails(Id(p), p.Path) : null;

    private P? Find(ProcIdentity id) => Procs.FirstOrDefault(p => p.Alive && p.Pid == id.Pid && p.Start == id.StartFileTime);

    public SettingRead GetPriority(ProcIdentity id) => Find(id) is { } p ? SettingRead.Of(p.Priority) : SettingRead.Gone("gone");
    public SettingWrite SetPriority(ProcIdentity id, string priority)
    {
        PriorityWrites++;
        var p = Find(id);
        if (p == null) return SettingWrite.Fail("gone");
        if (p.DenyWrite) return SettingWrite.Fail("Access denied");
        p.Priority = priority; return SettingWrite.Success;
    }
    public SettingRead GetThrottle(ProcIdentity id) => Find(id) is { } p ? SettingRead.Of(p.Throttle) : SettingRead.Gone("gone");
    public SettingWrite SetThrottle(ProcIdentity id, string state)
    {
        ThrottleWrites++;
        var p = Find(id);
        if (p == null) return SettingWrite.Fail("gone");
        if (p.DenyWrite) return SettingWrite.Fail("Access denied");
        p.Throttle = state; return SettingWrite.Success;
    }
    public long? TrimWorkingSet(ProcIdentity id) { var p = Find(id); if (p == null || p.DenyWrite) return null; p.Trimmed += 50 << 20; return 50 << 20; }
}

internal sealed class FakeTargetFactory : ITargetFactory
{
    public readonly FakeTarget GameBar = new("reg|HKCU|Software\\Microsoft\\GameBar|AutoGameModeEnabled", null);
    public readonly FakeTarget Scheme = new("powerscheme", "381b4222-f694-41f0-9685-ff5bb260df2e");
    public readonly FakeTarget Overlay = new("poweroverlay", Guid.Empty.ToString());
    private readonly FakeWorld _w;
    public FakeTargetFactory(FakeWorld w) => _w = w;
    public ISettingTarget GameBarAutoMode() => GameBar;
    public ISettingTarget PowerScheme() => Scheme;
    public ISettingTarget PowerOverlay() => Overlay;
    public ISettingTarget Priority(ProcIdentity id, string name) => new ProcessPriorityTarget(_w, id, name);
    public ISettingTarget Throttle(ProcIdentity id, string name) => new ProcessThrottleTarget(_w, id, name);

    public ITargetResolver Resolver() => new FactoryResolver(this, _w);

    private sealed class FactoryResolver : ITargetResolver
    {
        private readonly FakeTargetFactory _f; private readonly FakeWorld _w;
        public FactoryResolver(FakeTargetFactory f, FakeWorld w) { _f = f; _w = w; }
        public ISettingTarget? Resolve(string key)
        {
            var parts = key.Split('|');
            return parts[0] switch
            {
                "reg" => _f.GameBar,
                "powerscheme" => _f.Scheme,
                "poweroverlay" => _f.Overlay,
                "procprio" when ProcIdentity.TryParse(parts[1], out var i) => new ProcessPriorityTarget(_w, i),
                "procthrottle" when ProcIdentity.TryParse(parts[1], out var j) => new ProcessThrottleTarget(_w, j),
                _ => null,
            };
        }
    }
}

internal sealed class FakeResources : ISessionResources
{
    public bool TimerOn, AwakeOn; public int TimerBegins, TimerEnds;
    public bool BeginTimerResolution() { TimerOn = true; TimerBegins++; return true; }
    public void EndTimerResolution() { TimerOn = false; TimerEnds++; }
    public bool BeginKeepAwake() { AwakeOn = true; return true; }
    public void EndKeepAwake() => AwakeOn = false;
}

/// <summary>Builds made-up PCs so the rules can be tried against laptops, desktops, integrated-only systems and so on.</summary>
internal static class TestHw
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e", HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public static GpuInfo Nvidia(int index = 0, long luid = 100) => new(index, "NVIDIA GeForce GTX 1650", GpuVendor.Nvidia, 0x10DE, 1, GpuKind.Dedicated, luid, 4L << 30, 8L << 30, "32.0", true, "test");
    public static GpuInfo IntelIgpu(int index = 1, long luid = 200) => new(index, "Intel(R) UHD Graphics", GpuVendor.Intel, 0x8086, 2, GpuKind.Integrated, luid, 128L << 20, 8L << 30, "30.0", false, "test");
    public static GpuInfo AmdDiscrete(int index = 0, long luid = 300) => new(index, "AMD Radeon RX 6600", GpuVendor.Amd, 0x1002, 3, GpuKind.Dedicated, luid, 8L << 30, 8L << 30, "31.0", false, "test");

    public static PowerInfo Power(PowerSource src = PowerSource.Ac, string active = Balanced, bool highPlan = true, PowerMode mode = PowerMode.Balanced, bool modeSupported = true, bool saver = false)
        => new(src, src == PowerSource.Battery ? 60 : 100, active, active == HighPerf ? "High performance" : "Balanced", highPlan, highPlan ? HighPerf : null, mode, modeSupported, saver);

    public static HardwareProfile Make(PlatformKind platform = PlatformKind.Laptop, IEnumerable<GpuInfo>? gpus = null, double ramGb = 16, PowerInfo? power = null,
        bool hybridCpu = false, int build = 26200, bool etw = true)
        => new(DateTimeOffset.UtcNow,
            new CpuInfo("Test CPU", "GenuineIntel", 12, 6, 2700, hybridCpu, "X64"),
            (gpus ?? new[] { Nvidia(), IntelIgpu() }).ToList(),
            new List<DisplayInfo> { new(@"\\.\DISPLAY1", 0, 1920, 1080, 144, true) },
            new MemoryInfo((long)(ramGb * (1L << 30)), 2, 3200),
            new List<StorageInfo> { new("Test SSD", "SSD", "NVMe", 512L << 30) },
            new List<VolumeInfo> { new("C:\\", 512L << 30, 200L << 30, true) },
            new OsInfo("Windows 11 Home", "25H2", build, 1, "X64", true),
            platform, "test", "Test PC", power ?? Power(), new PermissionInfo(false, etw), new List<DataSourceStatus>());

    public static SystemSnapshot Snapshot(RobloxSession? game = null, bool? dvr = true, bool? gameMode = true, string? gpuPref = null, FrameRateConfig? cfg = null,
        string? exe = @"C:\Users\Test\AppData\Local\Roblox\Versions\version-x\RobloxPlayerBeta.exe")
        => new(dvr, gameMode, exe, gpuPref, cfg ?? Config(), new HashSet<string>(), new HashSet<string>(), game ?? RobloxSession.None);

    public static FrameRateConfig Config(int? cap = null, int? flag = null, bool managed = false, LauncherKind launcher = LauncherKind.Official, bool launcherHasFps = false, string? version = @"C:\v")
    {
        var flags = new Dictionary<string, string>();
        if (flag is int f) flags[FrameRateConfig.TargetFpsFlag] = f.ToString();
        var lflags = new Dictionary<string, string>();
        if (launcherHasFps) lflags[FrameRateConfig.TargetFpsFlag] = "9999";
        return new FrameRateConfig(launcher, version, null, flags, null, lflags, managed, cap, null, null, Array.Empty<string>(), Array.Empty<string>());
    }

    public static EvaluationContext Ctx(HardwareProfile hw, WindowStats? w = null, SystemSnapshot? s = null)
        => new(hw, w ?? WindowStats.Empty, s ?? Snapshot(), DateTimeOffset.UtcNow);

    /// <summary>A telemetry sample with a running game. Percentages are those of the busiest engine on each adapter.</summary>
    public static TelemetrySample Sample(DateTimeOffset at, HardwareProfile hw, double cpu = 30, double busiestCore = 40, double gpuGame = 50, int renderAdapter = 0,
        double availPct = 60, double? fps = null, bool game = true, double otherCpu = 2, double? faults = 5, double? perfLimit = 100,
        bool? thermal = null, double? temp = null)
    {
        var gpus = hw.Gpus.Where(g => g.Kind != GpuKind.Software).Select(g => new GpuSample(g.Index, g.Luid,
            g.Index == renderAdapter ? gpuGame : 1, 1e9, 1e8, temp, null, null, null, thermal, thermal == null ? null : false, null,
            game ? (g.Index == renderAdapter ? gpuGame : 0) : null, game ? (g.Index == renderAdapter ? 1.2e9 : 5e7) : null)).ToList();
        long total = hw.Memory.TotalBytes;
        var procs = new List<ProcessLoad> { new("chrome", 500, otherCpu, 500 << 20, false, true) };
        return new TelemetrySample(at, new CpuSample(cpu, Enumerable.Repeat(busiestCore, 12).ToList(), 3500, 95, (int?)perfLimit), gpus,
            new MemorySample(total, (long)(total * availPct / 100), 50, faults), new DiskSample(2, 0), procs,
            game ? new GameState(true, new[] { 900 }, LauncherKind.Froststrap, 8, 2L << 30, @"\\.\DISPLAY1", fps) : GameState.NotRunning, PowerSource.Ac);
    }

    public static IReadOnlyList<TelemetrySample> Samples(HardwareProfile hw, int n, Func<int, DateTimeOffset, TelemetrySample>? make = null)
    {
        var now = DateTimeOffset.UtcNow;
        return Enumerable.Range(0, n).Select(i => (make ?? ((_, at) => Sample(at, hw)))(i, now.AddSeconds(i - n))).ToList();
    }

    public static WindowStats Stats(IReadOnlyList<TelemetrySample> s) => WindowStats.From(s, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
}

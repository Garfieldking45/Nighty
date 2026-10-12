using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Nighty.Native;

namespace Nighty.Services.Booster;

// Every number is nullable: null means "Windows or the driver did not give this value", never zero and never a guess.

public sealed record CpuSample(
    double? TotalPercent,
    IReadOnlyList<double>? PerLogicalPercent,
    /// <summary>What Windows reports as the processors' actual frequency (including turbo), in MHz.</summary>
    int? ActualMhz,
    int? PercentOfMaxFrequency,
    /// <summary>100 means Windows is not limiting the processors; lower means it is holding them back (power or heat policy; the counter does not say which).</summary>
    int? PerformanceLimitPercent)
{
    public double? BusiestLogicalPercent => PerLogicalPercent is { Count: > 0 } p ? p.Max() : null;
}

public sealed record GpuSample(
    int AdapterIndex,
    long Luid,
    /// <summary>Utilization of the busiest engine on this adapter, as Task Manager shows it.</summary>
    double? EnginePercent,
    double? DedicatedUsedBytes,
    double? SharedUsedBytes,
    double? TemperatureC,
    int? CoreClockMhz,
    int? MemoryClockMhz,
    double? PowerWatts,
    bool? ThermalThrottling,
    bool? PowerLimited,
    string? ThrottleText,
    /// <summary>The same measures counting only the game's processes.</summary>
    double? GameEnginePercent,
    double? GameDedicatedBytes);

public sealed record MemorySample(long TotalBytes, long AvailableBytes, double CommitPercent, double? HardFaultsPerSec)
{
    public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * (TotalBytes - AvailableBytes) / TotalBytes;
    public double AvailablePercent => TotalBytes <= 0 ? 0 : 100.0 * AvailableBytes / TotalBytes;
}

public sealed record DiskSample(double? ActivePercent, double? QueueLength);

public sealed record ProcessLoad(string Name, int Pid, double CpuPercentOfTotal, long WorkingSetBytes, bool IsGame, bool IsBackgroundCandidate);

public sealed record GameState(
    bool Running,
    IReadOnlyList<int> Pids,
    LauncherKind Launcher,
    double? CpuPercentOfTotal,
    long WorkingSetBytes,
    string? DisplayDevice,
    /// <summary>Frames per second counted from Present events, only while that tracing is running.</summary>
    double? PresentFps)
{
    public static readonly GameState NotRunning = new(false, Array.Empty<int>(), LauncherKind.Unknown, null, 0, null, null);
}

public sealed record TelemetrySample(
    DateTimeOffset At,
    CpuSample Cpu,
    IReadOnlyList<GpuSample> Gpus,
    MemorySample Memory,
    DiskSample Disk,
    IReadOnlyList<ProcessLoad> TopProcesses,
    GameState Game,
    PowerSource PowerSource);

public interface ITelemetrySource
{
    TelemetrySample Sample();
    /// <summary>What can and cannot be measured right now, with the reason for anything missing.</summary>
    IReadOnlyList<DataSourceStatus> Sources { get; }
}

/// <summary>Finds the running Roblox client processes. Isolated so the telemetry can be tested without Roblox.</summary>
public interface IGameLocator
{
    RobloxSession Find();
}

/// <summary>
/// Reads the live state of the PC from Windows' own counters. Each probe fails on its own: a missing counter makes that value
/// null and adds an explanation to <see cref="Sources"/>, and the rest keep working.
/// </summary>
public sealed class WindowsTelemetrySource : ITelemetrySource
{
    private static readonly Regex EngineName = new(@"^pid_(?<pid>\d+)_luid_0x(?<hi>[0-9a-f]{8})_0x(?<lo>[0-9a-f]{8})_phys_(?<phys>\d+)_eng_(?<eng>\d+)_engtype_(?<type>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LuidName = new(@"luid_0x(?<hi>[0-9a-f]{8})_0x(?<lo>[0-9a-f]{8})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PidName = new(@"^pid_(?<pid>\d+)_", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<HardwareProfile?> _hardware;
    private readonly IGameLocator _game;
    private readonly IProcessSource _processes;
    private readonly NvmlLibrary _nvml;
    private readonly Func<double?> _presentFps;
    private readonly int _logical;
    private readonly Dictionary<string, DataSourceStatus> _status = new();

    // CPU load
    private (long Idle, long Total)[]? _lastCpu;
    // counters
    private PerformanceCounter? _freq, _pctMax, _perfLimit, _pagesIn, _diskIdle, _diskQueue;
    private bool _countersTried;
    private PerformanceCounterCategory? _gpuEngineCat, _gpuAdapterCat, _gpuProcCat, _procCat;
    private Dictionary<string, CounterSample> _lastEngine = new(), _lastProc = new();
    private string? _displayDevice;
    private int _displayAge = 99;
    private readonly Dictionary<int, int> _nvmlIndexByAdapter = new();
    private bool _nvmlMapped;

    public WindowsTelemetrySource(Func<HardwareProfile?> hardware, IGameLocator game, IProcessSource processes, NvmlLibrary nvml, Func<double?> presentFps)
    {
        _hardware = hardware; _game = game; _processes = processes; _nvml = nvml; _presentFps = presentFps;
        _logical = Math.Max(1, Environment.ProcessorCount);
    }

    public IReadOnlyList<DataSourceStatus> Sources { get { lock (_status) return _status.Values.ToList(); } }

    private void Status(string id, string name, bool ok, string detail)
    {
        lock (_status) _status[id] = new DataSourceStatus(id, name, ok, detail);
    }

    public TelemetrySample Sample()
    {
        var hw = _hardware();
        var game = _game.Find();
        var gamePids = game.Clients.Select(c => c.Id.Pid).ToHashSet();

        var cpu = ReadCpu();
        var procs = ReadProcesses(gamePids, out double? gameCpu, out long gameWs);
        var gpus = ReadGpus(hw, gamePids);
        var mem = ReadMemory();
        var disk = ReadDisk();

        if (gamePids.Count > 0 && ++_displayAge >= 5)
        {
            _displayAge = 0;
            try { _displayDevice = BoosterNative.DisplayOfProcessWindow(gamePids); } catch { _displayDevice = null; }
        }
        if (gamePids.Count == 0) _displayDevice = null;

        var state = gamePids.Count == 0 ? GameState.NotRunning
            : new GameState(true, gamePids.OrderBy(p => p).ToList(), game.Launcher, gameCpu, gameWs, _displayDevice, _presentFps());
        return new TelemetrySample(DateTimeOffset.UtcNow, cpu, gpus, mem, disk, procs, state, hw?.Power.Source ?? PowerSource.Unknown);
    }

    // ---------------- CPU ----------------
    private CpuSample ReadCpu()
    {
        double? total = null; double[]? per = null;
        var now = BoosterNative.ProcessorTimes(_logical);
        if (now == null) Status("cpu-load", "Processor load", false, "The kernel would not report per-processor times");
        else
        {
            Status("cpu-load", "Processor load", true, "Per-processor busy time from the Windows kernel");
            if (_lastCpu != null && _lastCpu.Length == now.Length)
            {
                per = new double[now.Length];
                long idleSum = 0, totalSum = 0;
                for (int i = 0; i < now.Length; i++)
                {
                    long di = now[i].Idle - _lastCpu[i].Idle, dt = now[i].Total - _lastCpu[i].Total;
                    per[i] = dt > 0 ? Math.Clamp(100.0 * (dt - di) / dt, 0, 100) : 0;
                    idleSum += di; totalSum += dt;
                }
                total = totalSum > 0 ? Math.Clamp(100.0 * (totalSum - idleSum) / totalSum, 0, 100) : 0;
            }
            _lastCpu = now;
        }

        EnsureCounters();
        int? actual = Read(_freq) is double f ? (int)f : null;
        int? pctMax = Read(_pctMax) is double pm ? (int)pm : null;
        int? limit = Read(_perfLimit) is double pl ? (int)pl : null;
        // "Actual frequency" is reported per processor group of the same name; if it is not there fall back to the power API (which does not include turbo).
        if (actual == null && BoosterNative.ProcessorPower(_logical) is { Length: > 0 } pw)
        {
            actual = (int)pw.Average(x => (double)x.Current);
            Status("cpu-clock", "Processor clock", true, "From the power API: the speed Windows asked for, which can be lower than the real turbo speed");
        }
        return new CpuSample(total, per, actual, pctMax, limit);
    }

    private void EnsureCounters()
    {
        if (_countersTried) return;
        _countersTried = true;
        PerformanceCounter? Make(string id, string name, string cat, string counter, string? instance)
        {
            try
            {
                var c = instance == null ? new PerformanceCounter(cat, counter, readOnly: true) : new PerformanceCounter(cat, counter, instance, readOnly: true);
                c.NextValue();   // rate counters need one earlier reading
                Status(id, name, true, $"Windows counter {cat}\\{counter}");
                return c;
            }
            catch (Exception ex) { Status(id, name, false, $"Windows counter {cat}\\{counter} is not available: {ex.Message}"); return null; }
        }
        _freq = Make("cpu-clock", "Processor clock", "Processor Information", "Actual Frequency", "_Total");
        _pctMax = Make("cpu-pct-max", "Processor speed versus maximum", "Processor Information", "% of Maximum Frequency", "_Total");
        _perfLimit = Make("cpu-limit", "Processor performance limit", "Processor Information", "% Performance Limit", "_Total");
        _pagesIn = Make("mem-faults", "Memory hard faults", "Memory", "Pages Input/sec", null);
        _diskIdle = Make("disk-active", "Disk activity", "PhysicalDisk", "% Idle Time", "_Total");
        _diskQueue = Make("disk-queue", "Disk queue", "PhysicalDisk", "Current Disk Queue Length", "_Total");
    }

    private static double? Read(PerformanceCounter? c)
    {
        if (c == null) return null;
        try { var v = c.NextValue(); return float.IsNaN(v) || float.IsInfinity(v) ? null : v; } catch { return null; }
    }

    // ---------------- memory / disk ----------------
    private MemorySample ReadMemory()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref m);
        double commit = m.ullTotalPageFile > 0 ? 100.0 * (m.ullTotalPageFile - m.ullAvailPageFile) / m.ullTotalPageFile : 0;
        Status("memory", "Memory use", true, "From Windows (GlobalMemoryStatusEx)");
        return new MemorySample((long)m.ullTotalPhys, (long)m.ullAvailPhys, commit, Read(_pagesIn));
    }

    private DiskSample ReadDisk()
    {
        double? idle = Read(_diskIdle);
        return new DiskSample(idle == null ? null : Math.Clamp(100 - idle.Value, 0, 100), Read(_diskQueue));
    }

    // ---------------- processes ----------------
    private List<ProcessLoad> ReadProcesses(HashSet<int> gamePids, out double? gameCpu, out long gameWs)
    {
        gameCpu = null; gameWs = 0;
        var result = new List<ProcessLoad>();
        try
        {
            _procCat ??= new PerformanceCounterCategory("Process");
            var data = _procCat.ReadCategory();
            var cpuInst = data["% Processor Time"];
            var idInst = data["ID Process"];
            var wsInst = data["Working Set"];
            var names = _processes.List().GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Name);
            var next = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
            double gameSum = 0; bool gameSeen = false;
            foreach (InstanceData inst in cpuInst.Values)
            {
                string key = inst.InstanceName;
                next[key] = inst.Sample;
                if (key is "_Total" or "Idle" || !idInst.Contains(key)) continue;
                int pid = (int)idInst[key].RawValue;
                if (pid <= 0 || !_lastProc.TryGetValue(key, out var prev) || prev.RawValue == 0) continue;
                double pct = CounterSample.Calculate(prev, inst.Sample) / _logical;   // counter is "percent of one processor"
                if (double.IsNaN(pct) || double.IsInfinity(pct) || pct < 0) continue;
                pct = Math.Min(pct, 100);
                long ws = wsInst.Contains(key) ? wsInst[key].RawValue : 0;
                bool isGame = gamePids.Contains(pid);
                if (isGame) { gameSum += pct; gameWs += ws; gameSeen = true; }
                names.TryGetValue(pid, out var nm);
                nm ??= key;
                result.Add(new ProcessLoad(nm, pid, pct, ws, isGame, BackgroundPolicy.IsCandidateName(nm)));
            }
            _lastProc = next;
            if (gameSeen) gameCpu = gameSum;
            Status("processes", "Per-process load", true, "Windows counter Process\\% Processor Time");
        }
        catch (Exception ex) { Status("processes", "Per-process load", false, "Windows counter Process\\% Processor Time is not available: " + ex.Message); }
        return result.OrderByDescending(p => p.CpuPercentOfTotal).Take(12).ToList();
    }

    // ---------------- GPUs ----------------
    private List<GpuSample> ReadGpus(HardwareProfile? hw, HashSet<int> gamePids)
    {
        var list = new List<GpuSample>();
        if (hw == null) return list;
        var gpus = hw.Gpus.Where(g => g.Kind != GpuKind.Software).ToList();
        if (!_nvmlMapped) { MapNvml(hw); _nvmlMapped = true; }

        var engineTotal = new Dictionary<(long Luid, string Eng), double>();
        var gameEngineTotal = new Dictionary<(long Luid, string Eng), double>();
        var dedicated = new Dictionary<long, double>();
        var shared = new Dictionary<long, double>();
        var gameDedicated = new Dictionary<long, double>();
        bool engines = false, memory = false;

        try
        {
            _gpuEngineCat ??= new PerformanceCounterCategory("GPU Engine");
            var data = _gpuEngineCat.ReadCategory();
            var util = data["Utilization Percentage"];
            var next = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
            foreach (InstanceData inst in util.Values)
            {
                next[inst.InstanceName] = inst.Sample;
                if (!_lastEngine.TryGetValue(inst.InstanceName, out var prev)) continue;
                var m = EngineName.Match(inst.InstanceName);
                if (!m.Success) continue;
                double v = CounterSample.Calculate(prev, inst.Sample);
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) continue;
                long luid = (Convert.ToInt64(m.Groups["hi"].Value, 16) << 32) | Convert.ToInt64(m.Groups["lo"].Value, 16);
                var key = (luid, $"{m.Groups["phys"].Value}/{m.Groups["eng"].Value}/{m.Groups["type"].Value}");
                engineTotal[key] = engineTotal.GetValueOrDefault(key) + v;
                if (gamePids.Contains(int.Parse(m.Groups["pid"].Value))) gameEngineTotal[key] = gameEngineTotal.GetValueOrDefault(key) + v;
                engines = true;
            }
            _lastEngine = next;
            Status("gpu-engine", "GPU utilization", true, "Windows counter GPU Engine\\Utilization Percentage (works for NVIDIA, AMD and Intel)");
        }
        catch (Exception ex) { Status("gpu-engine", "GPU utilization", false, "Windows counter GPU Engine is not available (needs Windows 10 1709 or later with a WDDM 2.0 driver): " + ex.Message); }

        try
        {
            _gpuAdapterCat ??= new PerformanceCounterCategory("GPU Adapter Memory");
            var ad = _gpuAdapterCat.ReadCategory();
            foreach (InstanceData i in ad["Dedicated Usage"].Values) if (LuidOf(i.InstanceName) is long l) dedicated[l] = i.RawValue;
            foreach (InstanceData i in ad["Shared Usage"].Values) if (LuidOf(i.InstanceName) is long l) shared[l] = i.RawValue;

            _gpuProcCat ??= new PerformanceCounterCategory("GPU Process Memory");
            var pm = _gpuProcCat.ReadCategory();
            foreach (InstanceData i in pm["Dedicated Usage"].Values)
            {
                var pm2 = PidName.Match(i.InstanceName);
                if (pm2.Success && gamePids.Contains(int.Parse(pm2.Groups["pid"].Value)) && LuidOf(i.InstanceName) is long l)
                    gameDedicated[l] = gameDedicated.GetValueOrDefault(l) + i.RawValue;
            }
            memory = true;
            Status("gpu-memory", "GPU memory use", true, "Windows counters GPU Adapter Memory and GPU Process Memory");
        }
        catch (Exception ex) { Status("gpu-memory", "GPU memory use", false, "Windows GPU memory counters are not available: " + ex.Message); }

        foreach (var g in gpus)
        {
            double? busy = engines ? engineTotal.Where(e => e.Key.Luid == g.Luid).Select(e => e.Value).DefaultIfEmpty(0).Max() : null;
            double? gameBusy = engines && gamePids.Count > 0 ? gameEngineTotal.Where(e => e.Key.Luid == g.Luid).Select(e => e.Value).DefaultIfEmpty(0).Max() : null;
            NvmlReading? nv = _nvmlIndexByAdapter.TryGetValue(g.Index, out int di) ? _nvml.Read(di) : null;
            list.Add(new GpuSample(
                g.Index, g.Luid,
                busy is double b ? Math.Min(100, b) : nv?.UtilizationPercent,
                memory && dedicated.TryGetValue(g.Luid, out var d) ? d : nv?.MemoryUsedBytes,
                memory && shared.TryGetValue(g.Luid, out var s) ? s : null,
                nv?.TemperatureC, nv?.GraphicsClockMhz, nv?.MemoryClockMhz, nv?.PowerWatts,
                nv == null ? null : nv.ThrottleMask == null ? null : nv.ThermalThrottling,
                nv == null ? null : nv.ThrottleMask == null ? null : nv.PowerLimited,
                nv?.ThrottleMask is ulong tm ? NvmlReading.DescribeThrottle(tm) : null,
                gameBusy is double gb ? Math.Min(100, gb) : null,
                memory && gamePids.Count > 0 ? gameDedicated.GetValueOrDefault(g.Luid) : null));
        }
        return list;
    }

    private static long? LuidOf(string instance)
    {
        var m = LuidName.Match(instance);
        return m.Success ? (Convert.ToInt64(m.Groups["hi"].Value, 16) << 32) | Convert.ToInt64(m.Groups["lo"].Value, 16) : null;
    }

    /// <summary>Pairs each NVIDIA adapter from DXGI with its NVML device (by name; falls back to order).</summary>
    private void MapNvml(HardwareProfile hw)
    {
        if (!_nvml.IsAvailable) return;
        var used = new HashSet<int>();
        foreach (var g in hw.Gpus.Where(g => g.Vendor == GpuVendor.Nvidia))
        {
            int idx = -1;
            for (int i = 0; i < _nvml.DeviceNames.Count; i++)
                if (!used.Contains(i) && string.Equals(_nvml.DeviceNames[i], g.Name, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx < 0) for (int i = 0; i < _nvml.DeviceNames.Count; i++) if (!used.Contains(i)) { idx = i; break; }
            if (idx >= 0) { used.Add(idx); _nvmlIndexByAdapter[g.Index] = idx; }
        }
    }
}

/// <summary>A bounded history of samples that runs only while something is looking at it.</summary>
public sealed class TelemetryService : IDisposable
{
    private readonly ITelemetrySource _source;
    private readonly TimeSpan _interval;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<TelemetrySample> _history = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _subscribers;
    private bool _disposed;

    public TelemetryService(ITelemetrySource source, TimeSpan? interval = null, int capacity = 300)
    {
        _source = source; _interval = interval ?? TimeSpan.FromSeconds(1); _capacity = Math.Max(10, capacity);
    }

    public TelemetrySample? Latest { get { lock (_gate) return _history.Count == 0 ? null : _history.Last(); } }
    public IReadOnlyList<DataSourceStatus> Sources => _source.Sources;
    public bool IsRunning { get { lock (_gate) return _loop != null; } }
    public int SubscriberCount { get { lock (_gate) return _subscribers; } }
    public int Capacity => _capacity;

    public event Action<TelemetrySample>? Sampled;
    /// <summary>Raised when a sample could not be taken (the loop keeps going).</summary>
    public event Action<Exception>? SampleFailed;

    public IReadOnlyList<TelemetrySample> Snapshot(TimeSpan? window = null, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (window == null) return _history.ToList();
            var cutoff = (now ?? DateTimeOffset.UtcNow) - window.Value;
            return _history.Where(s => s.At >= cutoff).ToList();
        }
    }

    /// <summary>Starts sampling while at least one subscriber exists. Dispose the result when you stop looking.</summary>
    public IDisposable Subscribe()
    {
        lock (_gate)
        {
            if (_disposed) return new Release(null);
            _subscribers++;
            if (_loop == null)
            {
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => Loop(token));
            }
        }
        return new Release(this);
    }

    private sealed class Release : IDisposable
    {
        private TelemetryService? _owner;
        public Release(TelemetryService? owner) => _owner = owner;
        public void Dispose() { var o = Interlocked.Exchange(ref _owner, null); o?.Unsubscribe(); }
    }

    private void Unsubscribe()
    {
        lock (_gate)
        {
            if (_subscribers > 0) _subscribers--;
            if (_subscribers == 0 && _loop != null) { _cts?.Cancel(); _loop = null; _cts = null; }
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do { SampleOnce(); } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Takes one sample now (used by the loop and by tests).</summary>
    public TelemetrySample? SampleOnce()
    {
        try
        {
            var s = _source.Sample();
            lock (_gate)
            {
                _history.Enqueue(s);
                while (_history.Count > _capacity) _history.Dequeue();
            }
            Sampled?.Invoke(s);
            return s;
        }
        catch (Exception ex)
        {
            Log.Warn("Telemetry sample failed", ex);
            SampleFailed?.Invoke(ex);
            return null;
        }
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate) { _disposed = true; _subscribers = 0; _cts?.Cancel(); loop = _loop; _loop = null; _cts = null; }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}

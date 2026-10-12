using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nighty.Services.Booster;

public sealed record FrameSourceAvailability(bool Available, string Detail);

public interface IFrameRecording : IDisposable
{
    int Count { get; }
    /// <summary>Stops recording and returns the interval between consecutive frames, in milliseconds.</summary>
    List<double> Stop();
}

public interface IFrameSource
{
    FrameSourceAvailability Availability();
    IFrameRecording Begin(IReadOnlyCollection<int> pids);
}

/// <summary>Real frame timing from Roblox's DXGI Present calls, the same events PresentMon uses. Needs the event-tracing permission.</summary>
public sealed class PresentFrameSource : IFrameSource
{
    private readonly FpsService _fps;
    public PresentFrameSource(FpsService fps) => _fps = fps;

    public FrameSourceAvailability Availability()
        => Elevation.CanTraceEtw
            ? new(true, "Present events from Windows event tracing (the method PresentMon uses)")
            : new(false, "Frame capture needs administrator rights or membership of the 'Performance Log Users' group (one-time setup on the Overlays page). You can enter figures from another tool or import a PresentMon CSV instead.");

    public IFrameRecording Begin(IReadOnlyCollection<int> pids)
    {
        bool startedHere = !_fps.IsRunning;
        if (startedHere) _fps.Start();
        return new Recording(_fps, pids.ToHashSet(), startedHere);
    }

    private sealed class Recording : IFrameRecording
    {
        private readonly FpsService _fps; private readonly HashSet<int> _pids; private readonly bool _startedHere;
        private readonly Dictionary<int, List<double>> _stamps = new();
        private readonly object _gate = new();
        private bool _stopped;

        public Recording(FpsService fps, HashSet<int> pids, bool startedHere)
        {
            _fps = fps; _pids = pids; _startedHere = startedHere;
            foreach (var p in pids) _stamps[p] = new List<double>(1 << 16);
            _fps.FramePresented += OnFrame;
        }

        private void OnFrame(int pid, double ms)
        {
            if (!_pids.Contains(pid)) return;
            lock (_gate) { if (!_stopped) _stamps[pid].Add(ms); }
        }

        public int Count { get { lock (_gate) return _stamps.Values.Sum(l => l.Count); } }

        public List<double> Stop()
        {
            _fps.FramePresented -= OnFrame;
            List<double> best;
            lock (_gate) { _stopped = true; best = _stamps.Values.OrderByDescending(l => l.Count).FirstOrDefault() ?? new(); }
            if (_startedHere) _fps.Stop();
            return FrameMath.Intervals(best);
        }

        public void Dispose() { if (!_stopped) Stop(); }
    }
}

public interface IBenchmarkStore
{
    List<BenchmarkRun> Load();
    void Save(IReadOnlyList<BenchmarkRun> runs);
}

public sealed class FileBenchmarkStore : IBenchmarkStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _path;
    private readonly int _max;
    public FileBenchmarkStore(string path, int maxRuns = 200) { _path = path; _max = maxRuns; }

    public List<BenchmarkRun> Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<BenchmarkRun>>(File.ReadAllText(_path), Json) ?? new() : new(); }
        catch (Exception ex)
        {
            Log.Warn("Benchmark history unreadable; starting empty", ex);
            try { File.Move(_path, _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
            return new();
        }
    }

    public void Save(IReadOnlyList<BenchmarkRun> runs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var keep = runs.OrderByDescending(r => r.StartedUtc).Take(_max).OrderBy(r => r.StartedUtc).ToList();
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(keep, Json));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) { Log.Warn("Saving benchmark history failed", ex); }
    }
}

public sealed record BenchmarkRequest(string Label, RunGroup Group, TimeSpan Duration, TimeSpan Warmup, string SceneNote, string SettingsNote);

public sealed class BenchmarkDeps
{
    public required IGameLocator Game { get; init; }
    public required IFrameSource Frames { get; init; }
    public required TelemetryService Telemetry { get; init; }
    public required Func<HardwareProfile?> Hardware { get; init; }
    public Func<FrameRateConfig?> Config { get; init; } = () => null;
    public Func<bool> GameModeActive { get; init; } = () => false;
    public Func<IReadOnlyList<string>> AppliedActions { get; init; } = () => Array.Empty<string>();
    public Func<bool> RobloxForeground { get; init; } = () => true;
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed record BenchmarkOutcome(BenchmarkRun? Run, string? Error, bool Cancelled)
{
    public bool Ok => Run != null;
}

/// <summary>Runs one measured test: warm-up, then a fixed time of real frame capture with system sampling alongside. Nothing is injected into or automated in the game.</summary>
public sealed class BenchmarkRunner
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(10), MaxDuration = TimeSpan.FromMinutes(10);
    private readonly BenchmarkDeps _d;
    public BenchmarkRunner(BenchmarkDeps deps) => _d = deps;

    public async Task<BenchmarkOutcome> RunAsync(BenchmarkRequest req, IProgress<(string Stage, double Fraction)>? progress, CancellationToken ct, bool allowShort = false)
    {
        if (!allowShort && (req.Duration < MinDuration || req.Duration > MaxDuration))
            return new(null, $"Choose a test length between {MinDuration.TotalSeconds:0} seconds and {MaxDuration.TotalMinutes:0} minutes.", false);
        var avail = _d.Frames.Availability();
        if (!avail.Available) return new(null, avail.Detail, false);
        var session = _d.Game.Find();
        if (!session.IsRunning) return new(null, "Roblox is not running. Start it, stand in the scene you want to test, then start the test.", false);

        var pids = session.Clients.Select(c => c.Id.Pid).ToList();
        var startedUtc = _d.Time.GetUtcNow().UtcDateTime;
        using var sub = _d.Telemetry.Subscribe();
        IFrameRecording? rec = null;
        int fgYes = 0, fgTotal = 0;
        try
        {
            progress?.Report(("Warming up", 0));
            await Wait(req.Warmup, "Warming up", 0, req.Warmup + req.Duration, pids, null, ct, progress);

            rec = _d.Frames.Begin(pids);
            var measureStart = _d.Time.GetUtcNow();
            var err = await Wait(req.Duration, "Measuring", req.Warmup.TotalSeconds, req.Warmup + req.Duration, pids, (yes) => { fgTotal++; if (yes) fgYes++; }, ct, progress);
            var intervals = rec.Stop();
            if (err != null) return new(null, err, false);

            var stats = FrameMath.Compute(intervals);
            if (stats == null) return new(null, $"Only {intervals.Count} frames were captured, which is too few to measure. Make sure Roblox is in front and rendering during the test.", false);

            var samples = _d.Telemetry.Snapshot().Where(s => s.At >= measureStart).ToList();
            var hw = _d.Hardware();
            var render = hw == null ? null : RenderGpu(hw, samples);
            var run = new BenchmarkRun
            {
                Label = req.Label, Group = req.Group, StartedUtc = startedUtc, PlannedSeconds = req.Duration.TotalSeconds, WarmupSeconds = req.Warmup.TotalSeconds,
                Frames = stats, System = RunSystemMetrics.From(samples, hw, render),
                Context = BuildContext(hw, session, samples, render, req, FrameSourceKind.PresentEvents, fgTotal == 0 ? 1.0 : fgYes / (double)fgTotal),
            };
            if (stats.DurationSec < req.Duration.TotalSeconds * 0.8) run.Warnings.Add($"Frames covered only {stats.DurationSec:0} of {req.Duration.TotalSeconds:0} seconds (Roblox may have been minimised or stalled).");
            if (stats.DiscardedFrames > 0) run.Warnings.Add($"{stats.DiscardedFrames} very long pauses (over {FrameMath.MaxCountedFrameMs / 1000:0} s) were left out.");
            if (run.Context.RobloxForegroundFraction < 0.9) run.Warnings.Add($"Roblox was in front only {run.Context.RobloxForegroundFraction * 100:0}% of the time.");
            if (samples.Count < req.Duration.TotalSeconds * 0.5 && samples.Count < 10) run.Warnings.Add("Few system measurements were taken during this run.");
            return new(run, null, false);
        }
        catch (OperationCanceledException) { return new(null, null, true); }
        finally { rec?.Dispose(); }
    }

    /// <summary>Waits while checking that Roblox is still there. Returns an error text if it closed.</summary>
    private async Task<string?> Wait(TimeSpan span, string stage, double offset, TimeSpan total, List<int> pids, Action<bool>? fg, CancellationToken ct, IProgress<(string, double)>? progress)
    {
        var step = TimeSpan.FromMilliseconds(Math.Min(500, Math.Max(5, span.TotalMilliseconds / 4)));
        var left = span;
        while (left > TimeSpan.Zero)
        {
            ct.ThrowIfCancellationRequested();
            var now = left < step ? left : step;
            await Task.Delay(now, _d.Time, ct);
            left -= now;
            if (!_d.Game.Find().Clients.Any(c => pids.Contains(c.Id.Pid))) return "Roblox closed during the test, so the run was discarded.";
            fg?.Invoke(_d.RobloxForeground());
            progress?.Report((stage, Math.Clamp((offset + (span - left).TotalSeconds) / Math.Max(0.001, total.TotalSeconds), 0, 1)));
        }
        return null;
    }

    public static GpuInfo? RenderGpu(HardwareProfile hw, IReadOnlyList<TelemetrySample> samples)
    {
        GpuInfo? best = null; double bestScore = 0;
        foreach (var g in hw.Gpus.Where(g => g.Kind != GpuKind.Software))
        {
            var m = samples.Where(s => s.Game.Running).Select(s => s.Gpus.FirstOrDefault(x => x.Luid == g.Luid)).Where(x => x != null).Select(x => x!).ToList();
            if (m.Count == 0) continue;
            double score = m.Average(x => (x.GameEnginePercent ?? 0) * 1e8 + (x.GameDedicatedBytes ?? 0));
            if (score > bestScore) { best = g; bestScore = score; }
        }
        return best;
    }

    public BenchmarkContext BuildContext(HardwareProfile? hw, RobloxSession session, IReadOnlyList<TelemetrySample> samples, GpuInfo? render, BenchmarkRequest req, FrameSourceKind source, double fgFraction)
    {
        var cfg = _d.Config();
        string? display = null;
        var dev = samples.LastOrDefault(s => s.Game.DisplayDevice != null)?.Game.DisplayDevice;
        if (hw != null && dev != null && hw.Displays.FirstOrDefault(x => string.Equals(x.DeviceName, dev, StringComparison.OrdinalIgnoreCase)) is { } dd) display = $"{dd.Width}x{dd.Height}@{dd.RefreshHz}Hz";
        string? version = cfg?.VersionFolder == null ? session.Clients.Select(c => c.Path).Where(p => p != null).Select(p => Path.GetFileName(Path.GetDirectoryName(p))).FirstOrDefault() : Path.GetFileName(cfg.VersionFolder);
        return new BenchmarkContext(
            hw?.Fingerprint ?? "unknown", hw?.OneLine() ?? "hardware not scanned", session.Launcher, version,
            hw?.Power.Source ?? PowerSource.Unknown, hw?.Power.ActiveSchemeName, hw?.Power.Mode ?? PowerMode.Unknown,
            _d.GameModeActive(), _d.AppliedActions().OrderBy(x => x).ToList(),
            cfg?.ConfiguredLimit?.ToString(), cfg?.FramerateCapSetting?.ToString(), cfg?.GraphicsQualityLevel, FlagSignature(cfg),
            display, render?.Name, req.SceneNote.Trim(), req.SettingsNote.Trim(), source, fgFraction);
    }

    /// <summary>A short fingerprint of every client flag, so runs made with different flags are recognised as different.</summary>
    public static string FlagSignature(FrameRateConfig? cfg)
    {
        if (cfg == null || cfg.ClientFlags.Count == 0) return "";
        var text = string.Join(";", cfg.ClientFlags.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
    }

    // ---- results typed in or imported from other tools ----
    public BenchmarkRun FromManual(string label, RunGroup group, double avgFps, double? onePercentLow, double seconds, string scene, string settings, string notes)
    {
        var hw = _d.Hardware();
        var session = _d.Game.Find();
        var req = new BenchmarkRequest(label, group, TimeSpan.FromSeconds(seconds), TimeSpan.Zero, scene, settings);
        return new BenchmarkRun
        {
            Label = label, Group = group, StartedUtc = _d.Time.GetUtcNow().UtcDateTime, PlannedSeconds = seconds, ManualAvgFps = avgFps, ManualOnePercentLowFps = onePercentLow,
            Context = BuildContext(hw, session, Array.Empty<TelemetrySample>(), null, req, FrameSourceKind.ManualEntry, 1.0), Notes = notes,
            Warnings = { "Entered by hand: Nighty could not verify these figures or measure the system during the test." },
        };
    }

    public BenchmarkOutcome FromCsv(string label, RunGroup group, string csvText, string scene, string settings)
    {
        var (frames, error) = CsvFrameImport.Parse(csvText);
        if (error != null) return new(null, error, false);
        var stats = FrameMath.Compute(frames);
        if (stats == null) return new(null, $"The file has only {frames.Count} usable frames; at least {FrameMath.MinFrames} are needed.", false);
        var hw = _d.Hardware();
        var req = new BenchmarkRequest(label, group, TimeSpan.FromSeconds(stats.DurationSec), TimeSpan.Zero, scene, settings);
        var run = new BenchmarkRun
        {
            Label = label, Group = group, StartedUtc = _d.Time.GetUtcNow().UtcDateTime, PlannedSeconds = stats.DurationSec, Frames = stats,
            Context = BuildContext(hw, _d.Game.Find(), Array.Empty<TelemetrySample>(), null, req, FrameSourceKind.ImportedCsv, 1.0),
            Warnings = { "Imported from a CSV file: the system was not measured, and the capture settings of the other tool are not known." },
        };
        return new(run, null, false);
    }
}

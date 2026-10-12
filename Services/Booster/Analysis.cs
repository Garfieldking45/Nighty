using System.Globalization;

namespace Nighty.Services.Booster;

public enum FindingSubject { Telemetry, Adapter, Gpu, Cpu, FrameRate, Memory, Power, Thermal, Storage, Background, Display, Config }

/// <summary>
/// One statement about this PC with the evidence behind it. <see cref="EvidenceKind.Observation"/> is something that was measured
/// or read; <see cref="EvidenceKind.Hypothesis"/> is an inference that other causes could also explain;
/// <see cref="EvidenceKind.Limitation"/> says what could not be checked.
/// </summary>
public sealed record Finding(FindingSubject Subject, EvidenceKind Level, string Statement, IReadOnlyList<string> Evidence, Confidence Confidence, string? HowToVerify = null);

/// <summary>Summary numbers over a window of recent samples. Stale or unreadable samples are left out, never counted as zero.</summary>
public sealed class WindowStats
{
    public int Count { get; private init; }
    public int GameCount { get; private init; }
    public double? CpuMean { get; private init; }
    public double? BusiestLogicalMedian { get; private init; }
    public double? GameCpuMean { get; private init; }
    public double? OtherCpuMean { get; private init; }
    public double? CandidateCpuMean { get; private init; }
    public double? MemAvailableMeanPercent { get; private init; }
    public double? MemAvailableMinPercent { get; private init; }
    public double? HardFaultsMean { get; private init; }
    public double? DiskActiveMean { get; private init; }
    public double? DiskQueueMean { get; private init; }
    public double? PerformanceLimitMin { get; private init; }
    public double? ActualMhzMean { get; private init; }
    public double? PresentFpsMean { get; private init; }
    public double? PresentFpsSpread { get; private init; }
    public int FpsCount { get; private init; }
    public IReadOnlyList<(string Name, double Cpu, bool Candidate)> TopOthers { get; private init; } = Array.Empty<(string, double, bool)>();
    public long CandidateWorkingSet { get; private init; }
    public IReadOnlyList<TelemetrySample> Samples { get; private init; } = Array.Empty<TelemetrySample>();

    public static readonly WindowStats Empty = new();

    public static WindowStats From(IReadOnlyList<TelemetrySample> window, DateTimeOffset now, TimeSpan maxAge)
    {
        var s = window.Where(x => now - x.At <= maxAge && x.At <= now + TimeSpan.FromSeconds(5)).ToList();
        if (s.Count == 0) return Empty;
        var game = s.Where(x => x.Game.Running).ToList();

        double? Mean(IEnumerable<double?> v) { var l = v.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Average(); }
        double? Median(IEnumerable<double?> v)
        {
            var l = v.Where(x => x.HasValue && !double.IsNaN(x.Value)).Select(x => x!.Value).OrderBy(x => x).ToList();
            return l.Count == 0 ? null : l.Count % 2 == 1 ? l[l.Count / 2] : (l[l.Count / 2 - 1] + l[l.Count / 2]) / 2;
        }

        var othersByName = new Dictionary<string, (double Cpu, bool Cand)>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in game.Count > 0 ? game : s)
            foreach (var p in x.TopProcesses.Where(p => !p.IsGame && p.Pid > 4))
                othersByName[p.Name] = (othersByName.GetValueOrDefault(p.Name).Cpu + p.CpuPercentOfTotal / Math.Max(1, (game.Count > 0 ? game : s).Count), p.IsBackgroundCandidate);
        double others = othersByName.Values.Sum(v => v.Cpu);
        double cands = othersByName.Values.Where(v => v.Cand).Sum(v => v.Cpu);

        var fps = game.Select(x => x.Game.PresentFps).Where(f => f is > 0).Select(f => f!.Value).ToList();
        double? fpsMean = fps.Count == 0 ? null : fps.Average();
        double? fpsSpread = fps.Count < 2 ? null : Math.Sqrt(fps.Sum(f => (f - fpsMean!.Value) * (f - fpsMean.Value)) / (fps.Count - 1));
        long candWs = s.Count == 0 ? 0 : (long)s.Average(x => x.TopProcesses.Where(p => p.IsBackgroundCandidate).Sum(p => (double)p.WorkingSetBytes));

        return new WindowStats
        {
            Count = s.Count, GameCount = game.Count,
            CpuMean = Mean(s.Select(x => x.Cpu.TotalPercent)),
            BusiestLogicalMedian = Median(s.Select(x => x.Cpu.BusiestLogicalPercent)),
            GameCpuMean = Mean(game.Select(x => x.Game.CpuPercentOfTotal)),
            OtherCpuMean = game.Count > 0 || s.Count > 0 ? others : null,
            CandidateCpuMean = cands,
            MemAvailableMeanPercent = Mean(s.Select(x => (double?)x.Memory.AvailablePercent)),
            MemAvailableMinPercent = s.Min(x => x.Memory.AvailablePercent),
            HardFaultsMean = Mean(s.Select(x => x.Memory.HardFaultsPerSec)),
            DiskActiveMean = Mean(s.Select(x => x.Disk.ActivePercent)),
            DiskQueueMean = Mean(s.Select(x => x.Disk.QueueLength)),
            PerformanceLimitMin = s.Select(x => x.Cpu.PerformanceLimitPercent).Where(v => v.HasValue).Select(v => (double?)v!.Value).DefaultIfEmpty(null).Min(),
            ActualMhzMean = Mean(s.Select(x => x.Cpu.ActualMhz.HasValue ? (double?)x.Cpu.ActualMhz.Value : null)),
            PresentFpsMean = fpsMean, PresentFpsSpread = fpsSpread, FpsCount = fps.Count,
            TopOthers = othersByName.OrderByDescending(kv => kv.Value.Cpu).Take(5).Select(kv => (kv.Key, kv.Value.Cpu, kv.Value.Cand)).ToList(),
            CandidateWorkingSet = candWs,
            Samples = s,
        };
    }
}

/// <summary>Turns the hardware profile, the recent measurements and the frame-rate configuration into explained findings.</summary>
public static class BottleneckAnalyzer
{
    public const int MinSamples = 8;
    private static string N(double v, string f = "0") => v.ToString(f, CultureInfo.InvariantCulture);

    public static IReadOnlyList<Finding> Analyze(HardwareProfile hw, WindowStats w, FrameRateConfig? cfg)
    {
        var f = new List<Finding>();
        bool enough = w.Count >= MinSamples;
        bool gameEnough = w.GameCount >= MinSamples;

        if (!enough)
            f.Add(new(FindingSubject.Telemetry, EvidenceKind.Limitation, w.Count == 0 ? "No live measurements yet" : $"Only {w.Count} live measurement{(w.Count == 1 ? "" : "s")} so far; conclusions need at least {MinSamples}",
                new[] { "Keep this page open for a few seconds while Roblox is running." }, Confidence.High));
        else if (!gameEnough)
            f.Add(new(FindingSubject.Telemetry, EvidenceKind.Limitation, "Roblox was not running during these measurements, so nothing can be said about how it performs",
                new[] { "Start Roblox, play for about 30 seconds, then look at this page again." }, Confidence.High));

        // ------- which GPU Roblox renders on -------
        GpuInfo? render = null;
        if (gameEnough)
        {
            var scored = new List<(GpuInfo Gpu, double? Engine, double? Mem)>();
            foreach (var g in hw.Gpus.Where(g => g.Kind != GpuKind.Software))
            {
                var m = w.Samples.Where(x => x.Game.Running).Select(x => x.Gpus.FirstOrDefault(s => s.Luid == g.Luid)).Where(s => s != null).Select(s => s!).ToList();
                double? engine = null, mem = null;
                var eng = m.Where(s => s.GameEnginePercent.HasValue).Select(s => s.GameEnginePercent!.Value).ToList();
                var mm = m.Where(s => s.GameDedicatedBytes.HasValue).Select(s => s.GameDedicatedBytes!.Value).ToList();
                if (eng.Count > 0) engine = eng.Average();
                if (mm.Count > 0) mem = mm.Average();
                scored.Add((g, engine, mem));
            }
            var byEngine = scored.Where(x => x.Engine is >= 2).OrderByDescending(x => x.Engine).FirstOrDefault();
            var byMem = scored.Where(x => x.Mem is >= 100 * 1048576.0).OrderByDescending(x => x.Mem).FirstOrDefault();
            var pick = byEngine.Gpu != null ? byEngine : byMem;
            if (pick.Gpu != null)
            {
                render = pick.Gpu;
                var ev = new List<string>();
                foreach (var x in scored) ev.Add($"{x.Gpu.Name}: Roblox GPU load {(x.Engine.HasValue ? N(x.Engine.Value) + "%" : "n/a")}, Roblox GPU memory {(x.Mem.HasValue ? N(x.Mem.Value / 1048576) + " MB" : "n/a")}");
                bool wrong = hw.HasHybridGraphics && render.Kind == GpuKind.Integrated;
                f.Add(new(FindingSubject.Adapter, EvidenceKind.Observation,
                    wrong ? $"Roblox is rendering on the integrated {render.Name}, although {hw.DedicatedGpus.First().Name} is available"
                          : $"Roblox is rendering on {render.Name}",
                    ev, Confidence.High, wrong ? "Use the 'Run Roblox on the high-performance graphics card' tweak, restart Roblox, and look here again." : null));
            }
            else if (hw.HasHybridGraphics)
                f.Add(new(FindingSubject.Adapter, EvidenceKind.Limitation, "Could not tell which graphics card Roblox is rendering on",
                    new[] { "Windows' per-process GPU counters showed no meaningful Roblox activity on any adapter." }, Confidence.High));
        }

        // ------- display Roblox is on -------
        var disp = w.Samples.LastOrDefault(x => x.Game.Running && x.Game.DisplayDevice != null)?.Game.DisplayDevice;
        DisplayInfo? display = disp == null ? null : hw.Displays.FirstOrDefault(d => string.Equals(d.DeviceName, disp, StringComparison.OrdinalIgnoreCase));
        if (display != null)
        {
            double mp = display.Width * (double)display.Height / 1e6;
            string gpuName = display.AdapterIndex is int ai ? hw.Gpus.FirstOrDefault(g => g.Index == ai)?.Name ?? "an adapter" : "an adapter";
            f.Add(new(FindingSubject.Display, EvidenceKind.Observation,
                $"Roblox is on a {display.Width}×{display.Height} display at {display.RefreshHz} Hz ({N(mp, "0.0")} megapixels per frame)",
                new[] { $"Windows lists this display ({display.DeviceName}) under {gpuName}.",
                        display.Width * display.Height > 2_500_000 ? $"That is {N(mp / (1920 * 1080 / 1e6), "0.0")}× the pixels of a 1920×1080 display, so the graphics card has that much more to draw each frame." : "This is close to or below 1920×1080 in pixel count." },
                Confidence.High, "Compare runs on the same display and resolution; a different display changes the result by itself."));
        }

        // ------- GPU load -------
        if (gameEnough)
        {
            var rg = render ?? hw.DedicatedGpus.FirstOrDefault() ?? hw.Gpus.FirstOrDefault(g => g.Kind != GpuKind.Software);
            if (rg != null)
            {
                var loads = w.Samples.Where(x => x.Game.Running).Select(x => x.Gpus.FirstOrDefault(g => g.Luid == rg.Luid)).Where(g => g?.EnginePercent != null).Select(g => g!.EnginePercent!.Value).OrderBy(v => v).ToList();
                if (loads.Count >= MinSamples)
                {
                    double med = loads[loads.Count / 2];
                    if (med >= 95)
                        f.Add(new(FindingSubject.Gpu, EvidenceKind.Hypothesis, $"{rg.Name} looks like the limit right now",
                            new[] { $"Its busiest engine ran at a median of {N(med)}% over {loads.Count} samples.", "A fully busy graphics card means more GPU speed is needed for more frames. Not every scene is like this one." },
                            Confidence.Medium, "Lower the resolution or graphics quality for a minute. If the frame rate rises a lot, the graphics card was the limit."));
                    else
                        f.Add(new(FindingSubject.Gpu, EvidenceKind.Observation, $"{rg.Name} was not fully used (median {N(med)}% busy)",
                            new[] { $"Measured over {loads.Count} samples while Roblox ran.", "Low GPU use on its own does not prove the processor is the limit: a frame-rate cap or V-Sync gives the same picture." }, Confidence.High));
                }
            }
        }

        // ------- frame limit / CPU -------
        if (gameEnough)
        {
            int limit = cfg?.ConfiguredLimit ?? 0;
            double? refresh = display?.RefreshHz;
            if (w.PresentFpsMean is double fps && w.FpsCount >= MinSamples)
            {
                var ev = new List<string> { $"Measured {N(fps)} frames per second on average over {w.FpsCount} seconds (counted from Present events)." };
                if (limit > 0 && Math.Abs(fps - limit) <= Math.Max(2, limit * 0.03) && (w.PresentFpsSpread ?? 99) <= Math.Max(2, limit * 0.03))
                    f.Add(new(FindingSubject.FrameRate, EvidenceKind.Observation, $"The frame rate is sitting at the configured limit of {limit}", ev.Append($"Roblox's files set a limit of {limit}.").ToList(), Confidence.High, "Raise or remove the limit and measure again."));
                else if (refresh is double r && Math.Abs(fps - r) <= Math.Max(1.5, r * 0.02) && (w.PresentFpsSpread ?? 99) <= 3)
                    f.Add(new(FindingSubject.FrameRate, EvidenceKind.Observation, $"The frame rate is locked to the display's {N(r)} Hz", ev.Append("A steady rate exactly at the refresh rate is typical of V-Sync or a display-synchronised mode.").ToList(), Confidence.Medium, "Turn V-Sync off or use a mode without it, then measure again."));
                else
                    f.Add(new(FindingSubject.FrameRate, EvidenceKind.Observation, $"Roblox averaged {N(fps)} FPS in this window", ev, Confidence.High));
            }
            else
            {
                f.Add(new(FindingSubject.FrameRate, EvidenceKind.Limitation, "Real frame timing is not being captured, so a frame cap cannot be told apart from a processor limit",
                    new[] { hw.Permissions.CanTraceEtw ? "Start the FPS overlay or a benchmark run to capture Present events." : "Frame capture needs administrator rights or the 'Performance Log Users' group (one-time setup on the Overlays page)." }, Confidence.High));
            }

            var gpuMed = w.Samples.Where(x => x.Game.Running).Select(x => x.Gpus.Where(g => g.EnginePercent != null).Select(g => g.EnginePercent!.Value).DefaultIfEmpty(0).Max()).OrderBy(v => v).ToList();
            double gmed = gpuMed.Count == 0 ? 0 : gpuMed[gpuMed.Count / 2];
            bool oneCoreHot = (w.BusiestLogicalMedian ?? 0) >= 90;
            if (gmed < 80 && oneCoreHot)
                f.Add(new(FindingSubject.Cpu, EvidenceKind.Hypothesis, "Roblox may be limited by a single busy processor thread",
                    new[] { $"The busiest logical processor had a median load of {N(w.BusiestLogicalMedian ?? 0)}% while the graphics card was at {N(gmed)}%.",
                            "A frame-rate cap, V-Sync or a game-side wait would look the same, so this is a possibility, not a finding." },
                    Confidence.Low, "Capture real frame timing, then lower graphics quality: a processor limit does not improve much."));
        }

        // ------- memory -------
        if (enough && w.MemAvailableMeanPercent is double avail)
        {
            bool pressure = avail < 12 || (avail < 20 && (w.HardFaultsMean ?? 0) > 150);
            if (pressure)
                f.Add(new(FindingSubject.Memory, EvidenceKind.Observation, $"Memory is tight: {N(avail)}% of RAM is free",
                    new[] { $"Lowest in the window: {N(w.MemAvailableMinPercent ?? avail)}%.", w.HardFaultsMean is double hf ? $"Windows is reading {N(hf)} pages per second from disk on average." : "Page-read rate is unavailable." }, Confidence.High,
                    "Close other programs and compare; if stutter drops, memory was involved."));
            else
                f.Add(new(FindingSubject.Memory, EvidenceKind.Observation, $"Memory is fine: {N(avail)}% of RAM is free", new[] { $"{N(hw.TotalMemoryGb, "0.#")} GB installed." }, Confidence.High));
        }

        // ------- power -------
        if (hw.IsLaptop && hw.Power.Source == PowerSource.Battery)
            f.Add(new(FindingSubject.Power, EvidenceKind.Observation, "The laptop is running on battery", new[] { "Windows lowers processor and graphics performance on battery to save power, whatever the power plan." }, Confidence.High, "Plug in and measure again."));
        if (enough && w.PerformanceLimitMin is double lim && lim < 100 && (w.CpuMean ?? 0) > 40)
            f.Add(new(FindingSubject.Power, EvidenceKind.Observation, $"Windows is holding the processor back (performance limit down to {N(lim)}%)",
                new[] { "This counter drops below 100% when Windows limits the processor for power or heat reasons; it does not say which.", w.ActualMhzMean is double mhz ? $"Average processor clock {N(mhz)} MHz." : "Clock speed unavailable." },
                Confidence.Medium, "Plug in, raise the power mode, clean the vents, and measure again."));
        if (hw.Power.BatterySaverOn)
            f.Add(new(FindingSubject.Power, EvidenceKind.Observation, "Battery saver is on", new[] { "Battery saver reduces performance." }, Confidence.High));

        // ------- thermal (only from sources that really report it) -------
        var hot = w.Samples.SelectMany(x => x.Gpus).Where(g => g.ThermalThrottling == true).ToList();
        var anyThermalData = w.Samples.SelectMany(x => x.Gpus).Any(g => g.ThermalThrottling != null);
        if (hot.Count > 0)
        {
            int total = w.Samples.Count;
            f.Add(new(FindingSubject.Thermal, EvidenceKind.Observation, "The NVIDIA driver reports thermal slowdown",
                new[] { $"Reported in {hot.Count} of {total} samples; temperature up to {N(hot.Max(g => g.TemperatureC ?? 0))} °C.", "This comes from the driver's own throttle-reason flags." }, Confidence.High, "Improve cooling and compare."));
        }
        else if (anyThermalData)
        {
            double? maxT = w.Samples.SelectMany(x => x.Gpus).Where(g => g.TemperatureC.HasValue).Select(g => g.TemperatureC).Max();
            f.Add(new(FindingSubject.Thermal, EvidenceKind.Observation, "The NVIDIA driver reports no thermal slowdown",
                new[] { maxT is double t ? $"Highest GPU temperature seen: {N(t)} °C." : "GPU temperature unavailable." }, Confidence.High));
        }
        f.Add(new(FindingSubject.Thermal, EvidenceKind.Limitation, "Processor temperature and processor heat-throttling cannot be read on this PC",
            new[] { "Windows has no reliable processor temperature source without installing a kernel driver, which Nighty does not do.", "The 'performance limit' counter above is the closest Windows-native sign." }, Confidence.High));

        // ------- storage -------
        if (enough && (w.DiskActiveMean ?? 0) >= 80 && (w.DiskQueueMean ?? 0) >= 2)
            f.Add(new(FindingSubject.Storage, EvidenceKind.Observation, $"The disk is busy ({N(w.DiskActiveMean!.Value)}% active, queue {N(w.DiskQueueMean!.Value, "0.0")})",
                new[] { "A busy disk can cause hitches while assets stream in." }, Confidence.Medium));

        // ------- background load -------
        if (enough && (w.OtherCpuMean ?? 0) >= 10)
        {
            var names = w.TopOthers.Take(4).Select(t => $"{t.Name} {N(t.Cpu, "0.0")}%{(t.Candidate ? " (can be asked to wait)" : "")}").ToList();
            f.Add(new(FindingSubject.Background, EvidenceKind.Observation, $"Other programs are using about {N(w.OtherCpuMean!.Value)}% of the processor", names, Confidence.Medium,
                "Close or pause the busiest program and compare."));
        }

        // ------- configuration -------
        if (cfg != null)
        {
            if (cfg.ConfiguredLimit is int cl)
                f.Add(new(FindingSubject.Config, EvidenceKind.Observation, $"Roblox is configured to limit the frame rate to {cl}", ConfigEvidence(cfg), Confidence.High));
            else
                f.Add(new(FindingSubject.Config, EvidenceKind.Observation, "No frame-rate limit is configured in Roblox's files", ConfigEvidence(cfg), Confidence.Medium));
            if (cfg.LowDetailFlags.Count > 0)
                f.Add(new(FindingSubject.Config, EvidenceKind.Observation, $"{cfg.LowDetailFlags.Count} flag{(cfg.LowDetailFlags.Count == 1 ? "" : "s")} that lower rendering detail {(cfg.LowDetailFlags.Count == 1 ? "is" : "are")} set",
                    cfg.LowDetailFlags.Prepend("These lower the work per frame, so they raise the frame rate most in scenes limited by the graphics card:").ToList(), Confidence.Medium,
                    "Record these in your notes: a run measured with different flags is not comparable."));
        }
        return f;
    }

    private static IReadOnlyList<string> ConfigEvidence(FrameRateConfig c)
    {
        var e = new List<string>();
        e.Add(c.FramerateCapSetting is int s ? $"Roblox's own 'maximum frame rate' setting: {s}." : "Roblox's own 'maximum frame rate' setting was not found.");
        e.Add(c.TargetFpsFromFlags is int t ? $"Client flag {FrameRateConfig.TargetFpsFlag}: {t}." : $"Client flag {FrameRateConfig.TargetFpsFlag} is not set.");
        e.Add($"Launcher: {RobloxClientDetector.Display(c.Launcher)}" + (c.LauncherManagesFlags ? " (it keeps its own flag list and rewrites the client's file at launch)." : "."));
        if (c.GraphicsQualityLevel is int q) e.Add($"Graphics quality level in Roblox's settings: {q}.");
        return e;
    }
}

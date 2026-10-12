using System.Globalization;
using System.Text.Json.Serialization;

namespace Nighty.Services.Booster;

[JsonConverter(typeof(JsonStringEnumConverter))] public enum FrameSourceKind { PresentEvents, ImportedCsv, ManualEntry }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum RunGroup { Baseline, After, Other }

/// <summary>
/// Frame-time statistics from real per-frame intervals. Definitions (so results can be compared with other tools):
/// average FPS = frames / total time; median and percentile frame times use linear interpolation between ranks;
/// "1% low" = the average FPS of the slowest 1% of frames (needs at least 100 frames).
/// </summary>
public sealed record FrameStats(int FrameCount, int DiscardedFrames, double DurationSec, double AvgFps, double MedianMs, double P95Ms, double P99Ms, double MaxMs, double? OnePercentLowFps);

public static class FrameMath
{
    /// <summary>Frame intervals longer than this are alt-tabs, loading screens or stalls rather than rendering, and are left out (and counted).</summary>
    public const double MaxCountedFrameMs = 2000;
    public const int MinFrames = 30;

    public static FrameStats? Compute(IReadOnlyList<double> frameMs)
    {
        var good = frameMs.Where(v => double.IsFinite(v) && v > 0 && v <= MaxCountedFrameMs).ToList();
        int discarded = frameMs.Count - good.Count;
        if (good.Count < MinFrames) return null;
        double totalMs = good.Sum();
        var sorted = good.OrderBy(v => v).ToList();
        double? low = null;
        if (good.Count >= 100)
        {
            int k = (int)Math.Ceiling(good.Count * 0.01);
            double worstMean = sorted.Skip(sorted.Count - k).Average();
            low = 1000.0 / worstMean;
        }
        return new FrameStats(good.Count, discarded, totalMs / 1000.0, good.Count * 1000.0 / totalMs,
            Percentile(sorted, 50), Percentile(sorted, 95), Percentile(sorted, 99), sorted[^1], low);
    }

    /// <summary>Linear interpolation between closest ranks (the common "type 7" definition).</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        if (sorted.Count == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    /// <summary>Turns Present-call timestamps (in milliseconds) into frame intervals.</summary>
    public static List<double> Intervals(IEnumerable<double> timestampsMs)
    {
        var t = timestampsMs.Where(double.IsFinite).OrderBy(v => v).ToList();
        var d = new List<double>(Math.Max(0, t.Count - 1));
        for (int i = 1; i < t.Count; i++) d.Add(t[i] - t[i - 1]);
        return d;
    }
}

/// <summary>What the rest of the PC was doing during a run. Null means it could not be measured.</summary>
public sealed record RunSystemMetrics(
    int Samples,
    double? CpuAvg, double? CpuMax, double? BusiestCoreAvg,
    double? GpuAvg, double? GpuMax,
    double? GpuTempMax, double? GpuClockAvgMhz, double? GpuPowerAvgW, double? GpuThermalThrottleFraction,
    double? MemoryUsedAvgPercent, double? HardFaultsAvg, double? DiskActiveAvg,
    double? OtherProcessesCpuAvg, double? CpuPerformanceLimitMin, double? CpuClockAvgMhz,
    IReadOnlyList<string> TopBackground,
    IReadOnlyList<string> Unavailable)
{
    public static RunSystemMetrics From(IReadOnlyList<TelemetrySample> s, HardwareProfile? hw, GpuInfo? renderGpu)
    {
        var unavailable = new List<string>();
        double? Avg(IEnumerable<double?> v) { var l = v.Where(x => x.HasValue && double.IsFinite(x.Value)).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Average(); }
        double? Max(IEnumerable<double?> v) { var l = v.Where(x => x.HasValue && double.IsFinite(x.Value)).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : l.Max(); }
        IEnumerable<GpuSample> Gpu() => s.SelectMany(x => x.Gpus.Where(g => renderGpu == null || g.Luid == renderGpu.Luid));

        var gpuThermal = Gpu().Where(g => g.ThermalThrottling.HasValue).ToList();
        var r = new RunSystemMetrics(s.Count,
            Avg(s.Select(x => x.Cpu.TotalPercent)), Max(s.Select(x => x.Cpu.TotalPercent)), Avg(s.Select(x => x.Cpu.BusiestLogicalPercent)),
            Avg(Gpu().Select(g => g.EnginePercent)), Max(Gpu().Select(g => g.EnginePercent)),
            Max(Gpu().Select(g => g.TemperatureC)), Avg(Gpu().Select(g => g.CoreClockMhz.HasValue ? (double?)g.CoreClockMhz.Value : null)), Avg(Gpu().Select(g => g.PowerWatts)),
            gpuThermal.Count == 0 ? null : gpuThermal.Count(g => g.ThermalThrottling == true) / (double)gpuThermal.Count,
            Avg(s.Select(x => (double?)x.Memory.UsedPercent)), Avg(s.Select(x => x.Memory.HardFaultsPerSec)), Avg(s.Select(x => x.Disk.ActivePercent)),
            Avg(s.Select(x => (double?)x.TopProcesses.Where(p => !p.IsGame && p.Pid > 4).Sum(p => p.CpuPercentOfTotal))),
            s.Select(x => x.Cpu.PerformanceLimitPercent).Where(v => v.HasValue).Select(v => (double?)v!.Value).DefaultIfEmpty(null).Min(),
            Avg(s.Select(x => x.Cpu.ActualMhz.HasValue ? (double?)x.Cpu.ActualMhz.Value : null)),
            s.SelectMany(x => x.TopProcesses.Where(p => !p.IsGame && p.Pid > 4 && p.CpuPercentOfTotal >= 1)).GroupBy(p => p.Name).OrderByDescending(g => g.Sum(p => p.CpuPercentOfTotal)).Take(3).Select(g => g.Key).ToList(),
            unavailable);
        if (r.GpuTempMax == null) unavailable.Add("GPU temperature, clock and power (only NVIDIA exposes them)");
        if (r.GpuThermalThrottleFraction == null) unavailable.Add("GPU heat-throttling flags");
        unavailable.Add("CPU temperature (Windows has no reliable source)");
        if (r.CpuPerformanceLimitMin == null) unavailable.Add("CPU performance limit");
        return r;
    }
}

/// <summary>Everything that has to match for two runs to be fairly compared, recorded with every run.</summary>
public sealed record BenchmarkContext(
    string HardwareFingerprint,
    string HardwareSummary,
    LauncherKind Launcher,
    string? RobloxVersion,
    PowerSource PowerSource,
    string? PowerPlan,
    PowerMode PowerMode,
    bool GameModeActive,
    IReadOnlyList<string> AppliedActions,
    string? ConfiguredFrameLimit,
    string? FramerateCapSetting,
    int? GraphicsQualityLevel,
    string LowDetailSignature,
    string? DisplayDescription,
    string? RenderAdapter,
    string SceneNote,
    string SettingsNote,
    FrameSourceKind Source,
    double RobloxForegroundFraction);

public sealed class BenchmarkRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public RunGroup Group { get; set; } = RunGroup.Other;
    public DateTime StartedUtc { get; set; }
    public double PlannedSeconds { get; set; }
    public double WarmupSeconds { get; set; }
    public FrameStats? Frames { get; set; }
    /// <summary>For manual entries: the figures the user read from another tool.</summary>
    public double? ManualAvgFps { get; set; }
    public double? ManualOnePercentLowFps { get; set; }
    public RunSystemMetrics? System { get; set; }
    public BenchmarkContext Context { get; set; } = null!;
    public List<string> Warnings { get; set; } = new();
    public string Notes { get; set; } = "";

    [JsonIgnore] public double? AvgFps => Frames?.AvgFps ?? ManualAvgFps;
    [JsonIgnore] public double? OnePercentLow => Frames?.OnePercentLowFps ?? ManualOnePercentLowFps;
    [JsonIgnore] public bool HasFrameData => Frames != null;
}

[JsonConverter(typeof(JsonStringEnumConverter))] public enum Verdict { Improved, Regressed, Mixed, Inconclusive, Incomparable }

public sealed record MetricComparison(string Name, string Unit, bool HigherIsBetter, double BaselineMean, double AfterMean, double ChangePercent,
    double? BaselineCvPercent, double? AfterCvPercent, string Outcome, double? TStatistic, string Explanation);

public sealed record ComparisonResult(Verdict Verdict, string Headline, int BaselineRuns, int AfterRuns, IReadOnlyList<string> IncomparableReasons,
    IReadOnlyList<string> Warnings, IReadOnlyList<MetricComparison> Metrics);

/// <summary>
/// Compares a group of "before" runs with a group of "after" runs. A change counts only when it is larger than ordinary
/// run-to-run variation (Welch's t-test at 95%) and at least 2%; with fewer than two runs on a side the answer is always
/// "inconclusive". All runs are used; nothing is cherry-picked.
/// </summary>
public static class BenchmarkComparer
{
    public const double PracticalThresholdPercent = 2.0;
    public const double NoisyCvPercent = 8.0;

    private static readonly (double Df, double T)[] Crit =
    {
        (1, 12.706), (2, 4.303), (3, 3.182), (4, 2.776), (5, 2.571), (6, 2.447), (7, 2.365), (8, 2.306), (9, 2.262), (10, 2.228),
        (12, 2.179), (15, 2.131), (20, 2.086), (30, 2.042), (60, 2.000), (120, 1.980),
    };

    public static double TCritical95(double df)
    {
        if (df <= 1) return Crit[0].T;
        if (df >= 120) return 1.96;
        for (int i = 1; i < Crit.Length; i++)
            if (df <= Crit[i].Df)
            {
                var (d0, t0) = Crit[i - 1]; var (d1, t1) = Crit[i];
                return t0 + (t1 - t0) * (df - d0) / (d1 - d0);
            }
        return 1.96;
    }

    public static ComparisonResult Compare(IReadOnlyList<BenchmarkRun> baseline, IReadOnlyList<BenchmarkRun> after, bool allowDifferentScenes = false)
    {
        var reasons = new List<string>();
        var warnings = new List<string>();
        if (baseline.Count == 0 || after.Count == 0)
            return new ComparisonResult(Verdict.Inconclusive, "Pick at least one run on each side to compare.", baseline.Count, after.Count, reasons, warnings, Array.Empty<MetricComparison>());

        CheckComparable(baseline, after, reasons, warnings, allowDifferentScenes);
        if (reasons.Count > 0)
            return new ComparisonResult(Verdict.Incomparable, "These runs cannot be compared fairly: " + reasons[0], baseline.Count, after.Count, reasons, warnings, Array.Empty<MetricComparison>());

        var metrics = new List<MetricComparison>();
        void Add(string name, string unit, bool higher, Func<BenchmarkRun, double?> pick, string fmt = "0.0")
        {
            var b = baseline.Select(pick).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToList();
            var a = after.Select(pick).Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToList();
            if (b.Count != baseline.Count || a.Count != after.Count) return;       // not measured in every run, so not comparable
            metrics.Add(Evaluate(name, unit, higher, b, a));
        }
        Add("Average FPS", "FPS", true, r => r.AvgFps);
        Add("1% low FPS", "FPS", true, r => r.OnePercentLow);
        Add("Median frame time", "ms", false, r => r.Frames?.MedianMs);
        Add("95th-percentile frame time", "ms", false, r => r.Frames?.P95Ms);

        foreach (var g in new[] { ("before", baseline), ("after", after) })
        {
            var cv = Cv(g.Item2.Select(r => r.AvgFps).Where(v => v.HasValue).Select(v => v!.Value).ToList());
            if (cv > NoisyCvPercent) warnings.Add($"The {g.Item1} runs differ from each other by {cv:0.#}% (average FPS), which is a lot; treat small differences as noise.");
        }
        if (baseline.Count < 2 || after.Count < 2)
            warnings.Add("Only one run on at least one side: Nighty cannot tell a real change from normal run-to-run variation. Run each at least twice (three is better).");

        bool improved = metrics.Any(m => m.Outcome == "Improved"), regressed = metrics.Any(m => m.Outcome == "Worse");
        Verdict v = improved && regressed ? Verdict.Mixed : improved ? Verdict.Improved : regressed ? Verdict.Regressed : Verdict.Inconclusive;
        string headline = v switch
        {
            Verdict.Improved => "Improved: " + string.Join("; ", metrics.Where(m => m.Outcome == "Improved").Select(m => $"{m.Name} {m.ChangePercent:+0.#;-0.#}%")),
            Verdict.Regressed => "Worse: " + string.Join("; ", metrics.Where(m => m.Outcome == "Worse").Select(m => $"{m.Name} {m.ChangePercent:+0.#;-0.#}%")),
            Verdict.Mixed => "Mixed: some measures improved and others got worse (see the list).",
            _ => baseline.Count < 2 || after.Count < 2 ? "Inconclusive: not enough runs to separate a real change from normal variation."
                                                       : "Inconclusive: any difference is within normal run-to-run variation.",
        };
        if (metrics.Count == 0) headline = "Inconclusive: the runs do not share a measure that can be compared.";
        return new ComparisonResult(v, headline, baseline.Count, after.Count, reasons, warnings, metrics);
    }

    public static MetricComparison Evaluate(string name, string unit, bool higherIsBetter, IReadOnlyList<double> before, IReadOnlyList<double> after)
    {
        double mb = before.Average(), ma = after.Average();
        double change = mb == 0 ? 0 : (ma - mb) / mb * 100.0;
        double? cvb = before.Count >= 2 ? Cv(before) : null, cva = after.Count >= 2 ? Cv(after) : null;
        if (before.Count < 2 || after.Count < 2)
            return new MetricComparison(name, unit, higherIsBetter, mb, ma, change, cvb, cva, "Not enough runs", null,
                $"{change:+0.#;-0.#}% in the one run available; with a single run on a side, normal variation cannot be measured.");

        double vb = Var(before), va = Var(after);
        double se = Math.Sqrt(vb / before.Count + va / after.Count);
        double diff = ma - mb;
        double t; double df;
        if (se == 0) { t = diff == 0 ? 0 : double.PositiveInfinity * Math.Sign(diff); df = before.Count + after.Count - 2; }
        else
        {
            t = diff / se;
            double num = Math.Pow(vb / before.Count + va / after.Count, 2);
            double den = Math.Pow(vb / before.Count, 2) / (before.Count - 1) + Math.Pow(va / after.Count, 2) / (after.Count - 1);
            df = den == 0 ? before.Count + after.Count - 2 : num / den;
        }
        double crit = TCritical95(df);
        bool significant = Math.Abs(t) >= crit && Math.Abs(change) >= PracticalThresholdPercent;
        string outcome = !significant ? "Within noise" : (diff > 0) == higherIsBetter ? "Improved" : "Worse";
        string expl = significant
            ? $"{change:+0.#;-0.#}% (t = {t:0.0}, needed {crit:0.0}); larger than the variation between runs."
            : $"{change:+0.#;-0.#}%, but the runs vary by about {Math.Max(cvb ?? 0, cva ?? 0):0.#}% between themselves" + (Math.Abs(change) < PracticalThresholdPercent ? ", and the change is under 2%." : ", so this cannot be told apart from noise.");
        return new MetricComparison(name, unit, higherIsBetter, mb, ma, change, cvb, cva, outcome, double.IsFinite(t) ? t : null, expl);
    }

    private static double Var(IReadOnlyList<double> v)
    {
        double m = v.Average();
        return v.Sum(x => (x - m) * (x - m)) / (v.Count - 1);
    }

    public static double Cv(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        double m = v.Average();
        return m == 0 ? 0 : Math.Sqrt(Var(v)) / Math.Abs(m) * 100.0;
    }

    private static string Norm(string s) => string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static void CheckComparable(IReadOnlyList<BenchmarkRun> b, IReadOnlyList<BenchmarkRun> a, List<string> reasons, List<string> warnings, bool allowScenes)
    {
        var all = b.Concat(a).ToList();
        void Same<T>(string what, Func<BenchmarkContext, T> pick, bool warnOnly = false, Func<T, string>? show = null)
        {
            var vals = all.Select(r => pick(r.Context)).Distinct().ToList();
            if (vals.Count <= 1) return;
            string text = $"{what} differs between runs ({string.Join(" vs ", vals.Take(3).Select(x => show?.Invoke(x) ?? x?.ToString() ?? "unset"))})";
            (warnOnly ? warnings : reasons).Add(text);
        }
        Same("The computer or its drivers", c => c.HardwareFingerprint, show: _ => "different hardware or drivers");
        Same("The way frames were measured", c => c.Source);
        Same("The display or resolution", c => c.DisplayDescription ?? "unknown");
        Same("The power source", c => c.PowerSource);
        Same("The frame-rate limit", c => c.ConfiguredFrameLimit ?? "none");
        Same("The graphics detail flags", c => c.LowDetailSignature, show: s => s.Length == 0 ? "none" : "different flags");
        Same("Roblox's graphics quality level", c => c.GraphicsQualityLevel?.ToString() ?? "unknown");
        Same("The graphics card used by Roblox", c => c.RenderAdapter ?? "unknown");
        if (!allowScenes) Same("The scene", c => Norm(c.SceneNote), show: s => s.Length == 0 ? "no note" : "\"" + s + "\"");
        Same("The graphics settings notes", c => Norm(c.SettingsNote), warnOnly: true, show: s => s.Length == 0 ? "no note" : "\"" + s + "\"");
        Same("The launcher", c => c.Launcher, warnOnly: true);
        Same("The Roblox version", c => c.RobloxVersion ?? "unknown", warnOnly: true);
        Same("The power mode", c => c.PowerMode, warnOnly: true);

        double dMin = all.Min(r => r.PlannedSeconds), dMax = all.Max(r => r.PlannedSeconds);
        if (dMin > 0 && dMax / dMin > 1.25) reasons.Add($"Test lengths differ too much ({dMin:0}–{dMax:0} s)");
        foreach (var r in all.Where(r => r.Context.RobloxForegroundFraction < 0.9 && r.Context.Source == FrameSourceKind.PresentEvents))
            warnings.Add($"\"{r.Label}\": Roblox was in front only {r.Context.RobloxForegroundFraction * 100:0}% of the time.");
        foreach (var r in all) foreach (var w in r.Warnings) warnings.Add($"\"{r.Label}\": {w}");
        if (all.All(r => r.Context.AppliedActions.Count == 0 && !r.Context.GameModeActive))
            warnings.Add("No optimization was applied in any of these runs, so there is nothing for this comparison to attribute a difference to.");
        else if (a.Select(r => string.Join(",", r.Context.AppliedActions.OrderBy(x => x))).Distinct().Count() > 1 || b.Select(r => string.Join(",", r.Context.AppliedActions.OrderBy(x => x))).Distinct().Count() > 1)
            warnings.Add("The set of applied optimizations differs between runs on the same side.");
    }
}

public static class CsvFrameImport
{
    /// <summary>
    /// Reads frame intervals from a PresentMon / CapFrameX / FrameView CSV export (the MsBetweenPresents or frame-time column).
    /// If the file mixes several programs, only rows for Roblox are used.
    /// </summary>
    public static (List<double> Frames, string? Error) Parse(string csv)
    {
        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return (new(), "The file has no data rows.");
        char sep = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var header = Split(lines[0], sep);
        int Col(params string[] names) => header.FindIndex(h => names.Any(n => string.Equals(h.Trim().Trim('"'), n, StringComparison.OrdinalIgnoreCase)));
        int ft = Col("MsBetweenPresents", "msBetweenPresents", "FrameTime", "Frame Time", "Frametime");
        if (ft < 0) return (new(), "No frame-time column found (expected MsBetweenPresents or FrameTime).");
        int app = Col("Application", "ProcessName", "Process Name", "Process");
        var rows = lines.Skip(1).Select(l => Split(l, sep)).Where(r => r.Count > ft).ToList();
        if (app >= 0)
        {
            var roblox = rows.Where(r => r.Count > app && r[app].Contains("Roblox", StringComparison.OrdinalIgnoreCase)).ToList();
            if (roblox.Count > 0) rows = roblox;
            else if (rows.Select(r => r[app]).Distinct().Count() > 1) return (new(), "The file contains several programs but none is Roblox.");
        }
        var frames = new List<double>(rows.Count);
        foreach (var r in rows)
            if (double.TryParse(r[ft].Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) frames.Add(ms);
        return frames.Count == 0 ? (frames, "No readable frame times in the file.") : (frames, null);
    }

    private static List<string> Split(string line, char sep)
    {
        var res = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        foreach (char c in line)
        {
            if (c == '"') q = !q;
            else if (c == sep && !q) { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }
}

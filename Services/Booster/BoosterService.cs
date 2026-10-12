using System.Runtime.InteropServices;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services.Booster;

/// <summary>Brings an older settings file forward without changing what the user had chosen.</summary>
public static class BoosterMigration
{
    public const int CurrentVersion = 1;

    /// <param name="loadedFromFile">True when the settings were read from an existing file (not created fresh).</param>
    public static void Apply(AppSettings s, bool loadedFromFile)
    {
        if (s.Booster.SchemaVersion >= CurrentVersion) return;
        s.Booster.SchemaVersion = CurrentVersion;
        if (!loadedFromFile) return;
        // This person already had Game Mode set up. Their choices stay exactly as they were; only the new safety checks apply on top.
        s.Booster.PlanReviewed = true;
        s.Booster.Profile = BoosterProfile.Custom;
        s.Booster.KeptFromOlderSetup = true;
    }
}

/// <summary>How the plan's choices map onto the settings that Game Mode reads.</summary>
public static class GameChoices
{
    public static IReadOnlySet<string> Enabled(GameSettings g)
    {
        var set = new HashSet<string>();
        if (g.HighPerformancePower) set.Add(ActionIds.Power);
        if (g.RobloxHighPriority) set.Add(ActionIds.Priority);
        if (g.RobloxNoThrottle) set.Add(ActionIds.Throttle);
        if (g.CalmBackgroundApps) set.Add(ActionIds.Calm);
        if (g.FreeMemory) set.Add(ActionIds.Memory);
        if (g.TimerResolution) set.Add(ActionIds.Timer);
        if (g.WindowsGameMode) set.Add(ActionIds.GameMode);
        if (g.KeepDisplayAwake) set.Add(ActionIds.Awake);
        return set;
    }

    /// <summary>Makes <paramref name="g"/> hold exactly the given session actions (all others switched off).</summary>
    public static void Set(GameSettings g, IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        g.HighPerformancePower = set.Contains(ActionIds.Power);
        g.RobloxHighPriority = set.Contains(ActionIds.Priority);
        g.RobloxNoThrottle = set.Contains(ActionIds.Throttle);
        g.CalmBackgroundApps = set.Contains(ActionIds.Calm);
        g.FreeMemory = set.Contains(ActionIds.Memory);
        g.TimerResolution = set.Contains(ActionIds.Timer);
        g.WindowsGameMode = set.Contains(ActionIds.GameMode);
        g.KeepDisplayAwake = set.Contains(ActionIds.Awake);
    }
}

public sealed record ApprovalResult(IReadOnlyList<string> SessionEnabled, IReadOnlyList<(string Id, bool Ok, string Text)> Tweaks);

/// <summary>
/// The booster's front door for the UI: scans the PC, builds the plan, and (only on the user's say-so) turns the approved
/// choices into settings. Everything it reads is read-only.
/// </summary>
public sealed class BoosterService : IDisposable
{
    private readonly WindowsHardwareSource _hwSource;
    private readonly object _scanGate = new();
    private Task<HardwareProfile>? _scan;

    public HardwareService Hardware { get; }
    public TelemetryService Telemetry { get; }
    public IProcessSource Processes { get; }
    public IProcessControl Control { get; }
    public IGameLocator Game { get; }
    public RecommendationEngine Engine { get; } = new();
    public ISystemStateReader State { get; } = new WindowsSystemStateReader();
    public NvmlLibrary Nvml => _hwSource.Nvml;

    public IBenchmarkStore BenchmarkStore { get; }
    public BenchmarkRunner Benchmarks { get; }

    public OptimizationPlan? LastPlan { get; private set; }
    public event Action<OptimizationPlan>? PlanBuilt;

    public BoosterService()
    {
        _hwSource = new WindowsHardwareSource();
        Hardware = new HardwareService(_hwSource);
        Processes = new WindowsProcessSource();
        Control = new NativeProcessControl();
        Game = new RobloxGameLocator(Processes);
        Telemetry = new TelemetryService(new WindowsTelemetrySource(() => Hardware.Current, Game, Processes, _hwSource.Nvml, () => Svc.Fps.IsRunning ? Svc.Fps.Fps : null));
        BenchmarkStore = new FileBenchmarkStore(Path.Combine(AppPaths.Root, "benchmarks.json"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Benchmarks = new BenchmarkRunner(new BenchmarkDeps
        {
            Game = Game, Frames = new PresentFrameSource(Svc.Fps), Telemetry = Telemetry, Hardware = () => Hardware.Current,
            Config = () => FrameRateInspector.Inspect(local, null, Game.Find().Launcher),
            GameModeActive = () => Svc.GameMode.IsActive,
            AppliedActions = () => Svc.GameMode.Engine.Reports.Where(r => r.Good).Select(r => r.ActionId).Concat(Svc.S.Backups.TweaksApplied).Distinct().ToList(),
            RobloxForeground = () => Svc.Roblox.IsForeground,
        });
    }

    /// <summary>A redacted diagnostics report as JSON text.</summary>
    public string BuildDiagnostics()
    {
        var plan = LastPlan ?? BuildPlan();
        var input = new DiagnosticsInput(
            AppVersion.Text(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version), Hardware.Current, Telemetry.Sources, Telemetry.Latest, plan,
            Svc.GameMode.Journal.Entries, Svc.GameMode.RestoreSummary, BenchmarkStore.Load(), plan == null ? null : State.Read(Game.Find(), new HashSet<string>(), new HashSet<string>()).FrameRate,
            DiagnosticsExporter.ReadLogTail(AppPaths.Logs), Array.Empty<string>());
        return DiagnosticsExporter.Build(input, Redactor.ForThisPc(), DateTimeOffset.Now);
    }

    /// <summary>Starts the first hardware scan in the background (once) and returns the task.</summary>
    public Task<HardwareProfile> EnsureScannedAsync()
    {
        lock (_scanGate)
        {
            if (Hardware.Current != null) return Task.FromResult(Hardware.Current);
            return _scan ??= Task.Run(() => Hardware.Rescan());
        }
    }

    /// <summary>Scans the PC again (hardware, drivers, power) and returns the new profile.</summary>
    public Task<HardwareProfile> RescanAsync()
    {
        lock (_scanGate) { _scan = Task.Run(() => Hardware.Rescan()); return _scan; }
    }

    /// <summary>Builds the plan from the latest hardware profile and the last minute of measurements. Read-only.</summary>
    public OptimizationPlan? BuildPlan()
    {
        var hw = Hardware.Current;
        if (hw == null) return null;
        var now = DateTimeOffset.UtcNow;
        var window = WindowStats.From(Telemetry.Snapshot(TimeSpan.FromSeconds(90), now), now, TimeSpan.FromSeconds(90));
        var game = Game.Find();
        var snapshot = State.Read(game, Svc.S.Backups.TweaksApplied.ToHashSet(), GameChoices.Enabled(Svc.S.Game));
        var plan = Engine.Evaluate(new EvaluationContext(hw, window, snapshot, now));
        LastPlan = plan;
        PlanBuilt?.Invoke(plan);
        return plan;
    }

    /// <summary>
    /// Turns the user's approved choices into settings: session actions become the Game Mode options, tweaks are applied through
    /// the tweak service (which saves their originals). Only ids the plan says are selectable are accepted.
    /// </summary>
    public ApprovalResult Approve(OptimizationPlan plan, BoosterProfile profile, IEnumerable<string> chosen)
    {
        var ids = plan.Sanitize(chosen);
        var session = ids.Where(ActionIds.IsSession).ToList();
        GameChoices.Set(Svc.S.Game, session);

        var tweakResults = new List<(string, bool, string)>();
        foreach (var id in ids.Where(i => plan.Find(i)?.Kind == ActionKind.Tweak))
        {
            if (Svc.Tweaks.IsApplied(id)) { tweakResults.Add((id, true, "Already on")); continue; }
            var r = Svc.Tweaks.Set(id, true);
            tweakResults.Add((id, r.Kind is Mvvm.StatusKind.Success, r.Text));
        }

        var b = Svc.S.Booster;
        b.Profile = profile; b.PlanReviewed = true; b.KeptFromOlderSetup = false;
        b.ApprovedFingerprint = plan.HardwareFingerprint; b.ApprovedUtc = DateTime.UtcNow;
        Svc.Settings.Save();
        Log.Info($"Plan approved: profile={profile}, session=[{string.Join(",", session)}], tweaks=[{string.Join(",", tweakResults.Select(t => t.Item1 + (t.Item2 ? "" : "!")))}]");
        return new ApprovalResult(session, tweakResults);
    }

    public bool HardwareChangedSinceApproval
        => Svc.S.Booster.ApprovedFingerprint is { } fp && Hardware.Current is { } hw && fp != hw.Fingerprint;

    public void Dispose()
    {
        Telemetry.Dispose();
        Hardware.Dispose();
        _hwSource.Dispose();
    }

    public static (long Total, long Available)? MemoryNow()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref m) ? ((long)m.ullTotalPhys, (long)m.ullAvailPhys) : null;
    }
}

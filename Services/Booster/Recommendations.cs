using System.Globalization;
using System.Text.Json.Serialization;

namespace Nighty.Services.Booster;

[JsonConverter(typeof(JsonStringEnumConverter))] public enum ActionKind { Session, Tweak, Advice }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum ActionCategory { Power, Graphics, Scheduling, Memory, Background, FrameRate, Windows }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum BenefitKind { None, FrameRate, FramePacing, InputLatency, Responsiveness }
[JsonConverter(typeof(JsonStringEnumConverter))] public enum PermissionNeed { None, Administrator }

/// <summary>How a proposed action relates to this PC right now.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Applicability
{
    /// <summary>Supported here, and there is a reason (a measurement or a clear mechanism) to expect it to help.</summary>
    Recommended,
    /// <summary>Supported here, but nothing seen suggests it would help. Offered for people who want it.</summary>
    Optional,
    /// <summary>Supported, but its benefit is unproven or doubtful on this system. Never selected by a profile.</summary>
    Discouraged,
    /// <summary>Does not apply to this PC (or is already in effect); the reason says why.</summary>
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter))] public enum BoosterProfile { Balanced, Performance, Custom }

public sealed record Recommendation(
    string Id,
    ActionKind Kind,
    ActionCategory Category,
    string Title,
    Applicability Applicability,
    /// <summary>Why this is (or is not) relevant to this PC, in plain words.</summary>
    string Reason,
    string CurrentValue,
    string ProposedValue,
    IReadOnlyList<EvidenceLine> Evidence,
    BenefitKind Benefit,
    string ExpectedBenefit,
    string TradeOffs,
    Confidence Confidence,
    RiskLevel Risk,
    PermissionNeed Permission,
    bool Reversible,
    string RestoreMethod,
    string VerifyMethod,
    bool InBalanced,
    bool InPerformance)
{
    [JsonIgnore] public bool Selectable => Applicability != Applicability.NotApplicable;
    [JsonIgnore] public bool IsPresetCandidate => Applicability == Applicability.Recommended;
}

public sealed record OptimizationPlan(
    DateTimeOffset CreatedAt,
    string HardwareFingerprint,
    IReadOnlyList<Recommendation> Items,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Notes)
{
    public Recommendation? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>The actions a profile selects. A profile only ever picks recommendations that apply to this PC; it never adds anything optional, discouraged or unsupported.</summary>
    public IReadOnlySet<string> Select(BoosterProfile profile) => profile switch
    {
        BoosterProfile.Balanced => Items.Where(i => i.IsPresetCandidate && i.InBalanced).Select(i => i.Id).ToHashSet(),
        BoosterProfile.Performance => Items.Where(i => i.IsPresetCandidate && i.InPerformance).Select(i => i.Id).ToHashSet(),
        _ => new HashSet<string>(),
    };

    /// <summary>Drops ids that cannot be applied here, so a saved or edited selection cannot switch on something unsupported.</summary>
    public IReadOnlySet<string> Sanitize(IEnumerable<string> ids) => ids.Where(id => Find(id) is { Selectable: true }).ToHashSet();
}

public readonly record struct Eligibility(bool Allowed, string Reason)
{
    public static readonly Eligibility Yes = new(true, "");
    public static Eligibility No(string why) => new(false, why);
}

/// <summary>Hardware-level checks shared by the plan and by Game Mode at the moment it starts, so both agree.</summary>
public static class ActionEligibility
{
    public static Eligibility Power(HardwareProfile hw)
    {
        var p = hw.Power;
        if (hw.Platform == PlatformKind.VirtualMachine) return Eligibility.No("This looks like a virtual machine; the host decides performance, so the power setting was left alone");
        if (p.BatterySaverOn) return Eligibility.No("Battery saver is on, so the power setting was left alone");
        if (hw.IsLaptop && p.Source == PowerSource.Battery) return Eligibility.No("The laptop is on battery, so the power setting was left alone to protect battery life and heat");
        if (!p.HasHighPerformancePlan && !p.PowerModeSupported) return Eligibility.No("This PC offers neither a High performance plan nor a power mode to switch to");
        return Eligibility.Yes;
    }

    public static Eligibility GpuPreference(HardwareProfile hw)
    {
        if (!hw.HasHybridGraphics) return Eligibility.No(hw.Gpus.Count(g => g.Kind != GpuKind.Software) <= 1
            ? "This PC has a single graphics adapter, so there is nothing to choose between"
            : "This PC does not have both an integrated and a dedicated graphics adapter");
        return Eligibility.Yes;
    }
}

/// <summary>What the rules need to know about the current state of Windows and Roblox (read-only).</summary>
public sealed record SystemSnapshot(
    bool? GameDvrOn,
    bool? WindowsGameModeOn,
    string? RobloxExePath,
    string? RobloxGpuPreference,
    FrameRateConfig? FrameRate,
    IReadOnlySet<string> TweaksApplied,
    IReadOnlySet<string> SessionEnabled,
    RobloxSession Game);

public sealed record EvaluationContext(HardwareProfile Hardware, WindowStats Window, SystemSnapshot State, DateTimeOffset Now);

public interface IRecommendationRule
{
    IEnumerable<Recommendation> Evaluate(EvaluationContext ctx);
}

internal static class R
{
    public static string N(double v, string f = "0") => v.ToString(f, CultureInfo.InvariantCulture);
    public static EvidenceLine O(string t) => EvidenceLine.Observed(t);
    public static EvidenceLine H(string t) => EvidenceLine.Inferred(t);
    public static EvidenceLine L(string t) => EvidenceLine.Missing(t);

    public static Recommendation NotApplicable(string id, ActionKind kind, ActionCategory cat, string title, string why, string current = "") => new(
        id, kind, cat, title, Applicability.NotApplicable, why, current, "", new[] { O(why) }, BenefitKind.None, "None here.", "", Confidence.High, RiskLevel.Low,
        PermissionNeed.None, true, "", "", false, false);

    public static string Gpu(HardwareProfile hw) => string.Join(" + ", hw.Gpus.Where(g => g.Kind != GpuKind.Software).Select(g => g.Name));
    public static bool HasLiveGame(EvaluationContext c) => c.Window.GameCount >= BottleneckAnalyzer.MinSamples;
}

// ---------------------------------------------------------------------------------------------------------------------
// Rules. Each looks at this PC and returns one recommendation for one action; the engine adds them up.
// ---------------------------------------------------------------------------------------------------------------------

public sealed class PowerRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        var hw = c.Hardware; var p = hw.Power;
        string current = $"{p.ActiveSchemeName ?? "unknown plan"}" + (p.PowerModeSupported ? $", power mode {p.Mode}" : "") + $", {(p.Source == PowerSource.Ac ? "plugged in" : p.Source == PowerSource.Battery ? "on battery" : "power source unknown")}";
        const string title = "Maximum-performance power setting";

        bool alreadyHigh = p.HighPerformancePlanId != null && string.Equals(p.ActiveSchemeId, p.HighPerformancePlanId, StringComparison.OrdinalIgnoreCase);
        if (alreadyHigh || p.Mode == PowerMode.BestPerformance)
        { yield return R.NotApplicable(ActionIds.Power, ActionKind.Session, ActionCategory.Power, title, "Already in effect: " + current, current); yield break; }

        var gate = ActionEligibility.Power(hw);
        if (!gate.Allowed) { yield return R.NotApplicable(ActionIds.Power, ActionKind.Session, ActionCategory.Power, title, gate.Reason + ".", current); yield break; }

        bool useOverlay = p.PowerModeSupported && string.Equals(p.ActiveSchemeId, "381b4222-f694-41f0-9685-ff5bb260df2e", StringComparison.OrdinalIgnoreCase);
        string proposed = useOverlay ? "Power mode: Best performance" : "Power plan: High performance";

        var ev = new List<EvidenceLine> { R.O("Current: " + current + ".") };
        bool limited = c.Window.PerformanceLimitMin is double lim && lim < 100 && (c.Window.CpuMean ?? 0) > 40;
        if (limited) ev.Add(R.O($"Windows reported a processor performance limit of {R.N(c.Window.PerformanceLimitMin!.Value)}% while the processor was busy."));
        else if (c.Window.Count >= BottleneckAnalyzer.MinSamples) ev.Add(R.O("Windows reported no processor performance limit during the recent measurements."));
        else ev.Add(R.L("No recent measurements to show whether the processor is being held back."));
        ev.Add(R.H(useOverlay ? "Windows' 'Balanced' mode can keep the processor and graphics card below full speed to save power; 'Best performance' stops that while it is selected."
                              : "The Balanced plan can keep the processor below full speed to save power; the High performance plan keeps it ready."));

        bool laptop = hw.IsLaptop;
        yield return new Recommendation(ActionIds.Power, ActionKind.Session, ActionCategory.Power, title,
            limited ? Applicability.Recommended : (laptop || c.Window.Count < BottleneckAnalyzer.MinSamples ? Applicability.Recommended : Applicability.Optional),
            "Power saving settings can hold back processor speed in games.", current, proposed, ev, BenefitKind.FramePacing,
            "Steadier frame rate when the processor was being held back; no change if it already runs at full speed.",
            "Uses more power and makes more heat and fan noise while Game Mode is on" + (laptop ? "; only applied while plugged in." : "."),
            limited ? Confidence.High : Confidence.Medium, RiskLevel.Low, PermissionNeed.None, true,
            "Nighty remembers the exact plan or mode you had and puts it back when Game Mode ends (it leaves it alone if you changed it yourself in the meantime).",
            "Look at the 'performance limit' reading on the Overview page and compare a benchmark run with and without.",
            InBalanced: limited, InPerformance: true);
    }
}

public sealed class WindowsGameModeRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Make sure Windows Game Mode is on";
        if (c.State.WindowsGameModeOn == true) { yield return R.NotApplicable(ActionIds.GameMode, ActionKind.Session, ActionCategory.Windows, title, "Windows Game Mode is already on.", "on"); yield break; }
        if (c.Hardware.Os.Build < 15063) { yield return R.NotApplicable(ActionIds.GameMode, ActionKind.Session, ActionCategory.Windows, title, "This Windows version has no Game Mode."); yield break; }
        yield return new Recommendation(ActionIds.GameMode, ActionKind.Session, ActionCategory.Windows, title,
            c.State.WindowsGameModeOn == false ? Applicability.Recommended : Applicability.Optional,
            "Windows Game Mode asks Windows to avoid background work such as driver installs while you play.",
            c.State.WindowsGameModeOn == null ? "not set (Windows default)" : "off", "on",
            new[] { c.State.WindowsGameModeOn == false ? R.O("Windows Game Mode is switched off for your account.") : R.L("The setting is not stored, so its state is unknown.") },
            BenefitKind.FramePacing, "Small; mostly fewer interruptions from Windows background work.", "None known.",
            Confidence.Low, RiskLevel.Low, PermissionNeed.None, true, "Your previous value is restored when Game Mode ends.", "Hard to measure on its own; judge by a benchmark comparison.",
            InBalanced: c.State.WindowsGameModeOn == false, InPerformance: c.State.WindowsGameModeOn == false);
    }
}

public sealed class ThrottleRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Stop Windows throttling Roblox";
        if (c.Hardware.Os.Build < 16299) { yield return R.NotApplicable(ActionIds.Throttle, ActionKind.Session, ActionCategory.Scheduling, title, "This Windows version has no per-program efficiency mode."); yield break; }
        bool hybrid = c.Hardware.Cpu.HybridTopology == true;
        yield return new Recommendation(ActionIds.Throttle, ActionKind.Session, ActionCategory.Scheduling, title, Applicability.Recommended,
            "Windows 11 can put a program it considers 'in the background' into efficiency mode, which lowers its speed and coarsens its timers.",
            "Windows decides", "Throttling switched off for the Roblox processes",
            new[]
            {
                hybrid ? R.O("This processor has performance and efficiency cores; efficiency mode moves a program onto the slow ones.") : R.O("This processor has one kind of core, so efficiency mode lowers clock speed rather than moving the program."),
                R.H("Roblox in full-screen is normally treated as the foreground, so the benefit is mainly when it is windowed, minimised, or briefly behind another window."),
            },
            BenefitKind.FramePacing, "Steadier frame times if Windows would otherwise throttle Roblox; nothing if it never does.",
            "Slightly more power use while Roblox is behind other windows.", hybrid ? Confidence.Medium : Confidence.Low, RiskLevel.Low, PermissionNeed.None, true,
            "Nighty saves each Roblox process' original throttling switches and puts them back (the processes also lose the setting when they close).",
            "Compare frame-time consistency (95th percentile frame time, 1% low) in a benchmark run.",
            InBalanced: hybrid, InPerformance: true);
    }
}

public sealed class PriorityRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Roblox High CPU priority";
        var w = c.Window;
        bool live = R.HasLiveGame(c);
        bool contention = live && ((w.OtherCpuMean ?? 0) >= 10 || (w.CpuMean ?? 0) >= 85);
        var ev = new List<EvidenceLine>();
        if (!live) ev.Add(R.L("Roblox was not running during the recent measurements, so there is no evidence about competition for the processor."));
        else if (contention) ev.Add(R.O($"Other programs used about {R.N(w.OtherCpuMean ?? 0)}% of the processor and total load averaged {R.N(w.CpuMean ?? 0)}% while Roblox ran."));
        else ev.Add(R.O($"The processor was not under heavy competition while Roblox ran (total {R.N(w.CpuMean ?? 0)}%, others {R.N(w.OtherCpuMean ?? 0)}%)."));
        ev.Add(R.H("Higher priority only matters when programs compete for processor time. With free cores it changes nothing."));
        yield return new Recommendation(ActionIds.Priority, ActionKind.Session, ActionCategory.Scheduling, title, contention ? Applicability.Recommended : Applicability.Optional,
            "Priority decides who gets processor time first when the processor is busy.", "Normal (the Windows default for a new program)", "High", ev, BenefitKind.FramePacing,
            contention ? "Fewer stalls when other programs compete with Roblox." : "None expected without competition.",
            "A misbehaving game thread can starve other programs, and Windows documents High as something to use sparingly.",
            contention ? Confidence.Medium : Confidence.Low, RiskLevel.Medium, PermissionNeed.None, true,
            "Each Roblox process' original priority is saved and restored; the setting also ends when the process closes.",
            "Compare 1% lows in a benchmark run while the same background programs are running.", InBalanced: false, InPerformance: contention);
    }
}

public sealed class CalmRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Calm background apps";
        var w = c.Window; bool live = R.HasLiveGame(c);
        bool busy = live && ((w.CandidateCpuMean ?? 0) >= 3 || (w.CandidateWorkingSet > 2L << 30 && (w.MemAvailableMeanPercent ?? 100) < 35));
        var ev = new List<EvidenceLine>();
        if (!live) ev.Add(R.L("Roblox was not running during the recent measurements, so background load while playing is unknown."));
        else if (busy)
        {
            var names = w.TopOthers.Where(t => t.Candidate).Select(t => $"{t.Name} ({R.N(t.Cpu, "0.0")}%)").ToList();
            ev.Add(R.O($"Programs Nighty can ask to wait used about {R.N(w.CandidateCpuMean ?? 0, "0.0")}% of the processor" + (names.Count > 0 ? ": " + string.Join(", ", names) : "") + "."));
        }
        else ev.Add(R.O("Programs Nighty can ask to wait were nearly idle while Roblox ran."));
        ev.Add(R.O("Only browsers, launchers, updaters and sync tools on Nighty's short list are touched; security software, Windows, drivers and accessibility tools never are."));
        yield return new Recommendation(ActionIds.Calm, ActionKind.Session, ActionCategory.Background, title, busy ? Applicability.Recommended : Applicability.Optional,
            "Background programs compete with the game for processor time.", "Normal priority (the Windows default)", "Below normal priority and efficiency mode (nothing is closed)", ev,
            BenefitKind.FramePacing, busy ? "Less competition for the processor." : "None expected: they were not using much.",
            "A slowed browser may update a page a little later; video calls could be less smooth.", busy ? Confidence.Medium : Confidence.Low, RiskLevel.Low, PermissionNeed.None, true,
            "Each program's priority and efficiency mode are restored when Roblox closes or Game Mode ends; programs that exited are skipped.",
            "Compare a benchmark run with the same programs open.", InBalanced: false, InPerformance: busy);
    }
}

public sealed class MemoryRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Free up memory from background apps";
        var w = c.Window; double gb = c.Hardware.TotalMemoryGb;
        bool known = w.Count >= BottleneckAnalyzer.MinSamples && w.MemAvailableMeanPercent != null;
        double avail = w.MemAvailableMeanPercent ?? 100;
        bool pressure = known && (avail < 15 || (avail < 25 && (w.HardFaultsMean ?? 0) > 150));
        var ev = new List<EvidenceLine>
        {
            known ? R.O($"{R.N(avail)}% of {R.N(gb, "0.#")} GB is free (lowest {R.N(w.MemAvailableMinPercent ?? avail)}%)" + (w.HardFaultsMean is double hf ? $", with {R.N(hf)} page reads per second." : "."))
                  : R.L("No recent memory measurements."),
            R.H("Trimming makes a program give back memory it is not using, but Windows hands the pages back the moment the program needs them, so it only helps when memory is genuinely short."),
        };
        yield return new Recommendation(ActionIds.Memory, ActionKind.Session, ActionCategory.Memory, title,
            pressure ? Applicability.Recommended : known ? Applicability.Discouraged : Applicability.Optional,
            pressure ? "Memory is short, so Roblox may be waiting on the disk." : known ? "Plenty of memory is free, so trimming would not help." : "Nothing measured about memory yet.",
            known ? $"{R.N(avail)}% free" : "not measured yet", "Background apps give back unused memory once when Roblox starts", ev, BenefitKind.FramePacing,
            pressure ? "Fewer disk reads while Roblox runs." : "None expected.", "Apps you switch back to may be a little slower for a moment.",
            known ? Confidence.Medium : Confidence.Low, RiskLevel.Low, PermissionNeed.None, false,
            "Nothing to restore: Windows returns pages to each program on demand. The report lists how much was given back.",
            "Check that 'page reads per second' falls on the Overview page.", InBalanced: false, InPerformance: pressure);
    }
}

public sealed class TimerRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "1 ms timer resolution";
        bool perProcess = c.Hardware.Os.Build >= 19041;
        yield return new Recommendation(ActionIds.Timer, ActionKind.Session, ActionCategory.Scheduling, title, Applicability.Discouraged,
            perProcess ? "On this Windows version a program's timer request only affects that program, so Nighty asking for a finer timer does not change Roblox's timers."
                       : "Nighty can raise the system-wide timer resolution on this Windows version.",
            "Windows default", "1 ms requested by Nighty",
            new[]
            {
                perProcess ? R.O($"Windows build {c.Hardware.Os.Build} applies timer requests per process (changed in version 2004).") : R.O("Older Windows applies the highest request from any program to everyone."),
                R.H("Nothing Nighty measured shows this helps Roblox."),
            },
            BenefitKind.None, "Unverified.", "Slightly higher power use and battery drain.", Confidence.Low, RiskLevel.Low, PermissionNeed.None, true,
            "Released when Game Mode ends (Windows also releases it if Nighty closes).", "Not measurable from outside Roblox; compare with a benchmark run.", false, false);
    }
}

public sealed class AwakeRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        yield return new Recommendation(ActionIds.Awake, ActionKind.Session, ActionCategory.Windows, "Keep display awake", Applicability.Optional,
            "Prevents the screen and PC from sleeping during long sessions. It does not change game speed.", "Windows sleep timers", "Kept awake while Game Mode is on",
            new[] { R.O("This is a convenience, not a performance setting.") }, BenefitKind.None, "None for frame rate.", "Uses power when you walk away.",
            Confidence.High, RiskLevel.Low, PermissionNeed.None, true, "Released when Game Mode ends (or Nighty closes).", "Not applicable.", false, false);
    }
}

public sealed class GpuPreferenceRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Run Roblox on the high-performance graphics card";
        var gate = ActionEligibility.GpuPreference(c.Hardware);
        if (!gate.Allowed) { yield return R.NotApplicable("gpu", ActionKind.Tweak, ActionCategory.Graphics, title, gate.Reason + ".", ""); yield break; }
        if (c.State.RobloxExePath == null) { yield return R.NotApplicable("gpu", ActionKind.Tweak, ActionCategory.Graphics, title, "Roblox is not installed for this user, so there is nothing to set."); yield break; }

        bool set = c.State.RobloxGpuPreference?.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase) == true;
        var dedicated = c.Hardware.DedicatedGpus.First();
        var ev = new List<EvidenceLine>
        {
            R.O($"This PC has {R.Gpu(c.Hardware)}; Windows chooses between them for each program."),
            R.O(set ? "Windows is already told to use the high-performance card for this Roblox client." : "No graphics preference is saved for this Roblox client, so Windows chooses."),
        };
        var observed = c.Window.GameCount >= BottleneckAnalyzer.MinSamples ? BottleneckAnalyzer.Analyze(c.Hardware, c.Window, c.State.FrameRate).FirstOrDefault(f => f.Subject == FindingSubject.Adapter) : null;
        bool wrongNow = observed != null && observed.Statement.Contains("integrated", StringComparison.OrdinalIgnoreCase) && observed.Level == EvidenceKind.Observation;
        bool rightNow = observed != null && !wrongNow && observed.Level == EvidenceKind.Observation;
        if (observed != null) ev.Add(new EvidenceLine(observed.Level, observed.Statement + "."));
        else ev.Add(R.L("Roblox was not running during the recent measurements, so Nighty has not seen which card it uses."));

        if (set && !wrongNow) { yield return R.NotApplicable("gpu", ActionKind.Tweak, ActionCategory.Graphics, title, "Already set to the high-performance card for this Roblox client.", "High performance"); yield break; }
        yield return new Recommendation("gpu", ActionKind.Tweak, ActionCategory.Graphics, title,
            rightNow && !set ? Applicability.Optional : Applicability.Recommended,
            "On a PC with two graphics adapters, Roblox on the weaker one can cut the frame rate several times over.",
            set ? "high performance (but Roblox appears to use the other card)" : "Windows decides", $"High performance ({dedicated.Name})", ev, BenefitKind.FrameRate,
            wrongNow ? "Large: the game would move to the stronger card." : "None if Roblox already uses the stronger card; protects against Windows picking the weaker one.",
            "More power use and heat, and shorter battery life on a laptop.", wrongNow ? Confidence.High : Confidence.Medium, RiskLevel.Low, PermissionNeed.None, true,
            "Your previous Windows graphics preference for this program is restored when you turn the tweak off.",
            "Restart Roblox and check on the Overview page which card Roblox is using.", InBalanced: !rightNow, InPerformance: !rightNow);
    }
}

public sealed class GameDvrRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Turn off Game DVR background recording";
        if (c.State.GameDvrOn == false) { yield return R.NotApplicable("gamedvr", ActionKind.Tweak, ActionCategory.Windows, title, "Game DVR background recording is already off.", "off"); yield break; }
        bool on = c.State.GameDvrOn == true;
        yield return new Recommendation("gamedvr", ActionKind.Tweak, ActionCategory.Windows, title, on ? Applicability.Recommended : Applicability.Optional,
            "Windows' Game Bar can record gameplay in the background, which costs graphics and disk time.", on ? "on" : "unknown", "off",
            new[] { on ? R.O("Game DVR capture is switched on for your account.") : R.L("Windows did not report whether Game DVR is on.") },
            BenefitKind.FramePacing, "Small; removes background capture work if it was running.", "You lose Game Bar's 'record the last 30 seconds' feature (Nighty has its own Instant Replay).",
            on ? Confidence.Medium : Confidence.Low, RiskLevel.Low, PermissionNeed.None, true, "Your exact original values are restored when you turn it off.", "Compare a benchmark run before and after.",
            InBalanced: on, InPerformance: on);
    }
}

public sealed class FrameCapRule : IRecommendationRule
{
    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        const string title = "Remove Roblox's 60 FPS cap";
        var cfg = c.State.FrameRate;
        if (cfg?.VersionFolder == null) { yield return R.NotApplicable("fpscap", ActionKind.Tweak, ActionCategory.FrameRate, title, "Roblox is not installed for this user, so there is nothing to set."); yield break; }

        var ev = new List<EvidenceLine>();
        int? limit = cfg.ConfiguredLimit;
        if (cfg.FramerateCapSetting is int s) ev.Add(R.O($"Roblox's own 'maximum frame rate' setting is {s}."));
        if (cfg.TargetFpsFromFlags is int t) ev.Add(R.O($"The client flag {FrameRateConfig.TargetFpsFlag} is {t}."));
        if (cfg.TargetFpsFromFlags == null) ev.Add(R.O($"The client flag {FrameRateConfig.TargetFpsFlag} is not set."));

        if (limit is int l && l >= 240 && cfg.TargetFpsFromFlags is null or >= 240)
        { yield return R.NotApplicable("fpscap", ActionKind.Tweak, ActionCategory.FrameRate, title, $"Roblox is already allowed to run at {l} FPS or more, so the cap is not what limits it.", $"limit {l}"); yield break; }

        if (cfg.LauncherManagesFlags)
        {
            ev.Add(R.O($"{RobloxClientDetector.Display(cfg.Launcher)} keeps its own flag list and rewrites the client's file at every launch."));
            ev.Add(cfg.LauncherHasTargetFps ? R.O("Its list already includes the frame-rate flag.") : R.O($"Its list does not include {FrameRateConfig.TargetFpsFlag}, so a flag written by Nighty is removed the next time Roblox starts."));
            yield return new Recommendation("fpscap", ActionKind.Tweak, ActionCategory.FrameRate, title, Applicability.Discouraged,
                $"{cfg.Launcher} would undo this change at the next launch; set the frame-rate flag in {cfg.Launcher}'s own settings instead.",
                limit is int cl ? $"limit {cl}" : "no limit set", "(unchanged)", ev, BenefitKind.FrameRate, "None through Nighty with this launcher.",
                "Changing the file by hand while Roblox runs has no effect until the next launch, and the launcher then overwrites it.", Confidence.High, RiskLevel.Low, PermissionNeed.None, true,
                "Nothing is changed.", "Open the launcher's FastFlag settings and check the value there.", false, false);
            yield break;
        }

        yield return new Recommendation("fpscap", ActionKind.Tweak, ActionCategory.FrameRate, title, limit is int lm && lm <= 60 ? Applicability.Recommended : Applicability.Optional,
            "Roblox renders at 60 FPS unless told otherwise; a fast display can then not be used fully.",
            limit is int cur ? $"limit {cur}" : "no limit found (Roblox default)", $"{FrameRateConfig.TargetFpsFlag}=9999", ev, BenefitKind.FrameRate,
            "Large if the cap is what limits you; none otherwise.", "Higher frame rates use more power and make more heat; some scenes get less steady when uncapped.",
            limit is int ? Confidence.High : Confidence.Medium, RiskLevel.Low, PermissionNeed.None, true,
            "Nighty restores the flag's original value (or removes it); it is re-applied after Roblox updates until you turn it off.", "Measure FPS before and after, with the same scene.",
            InBalanced: false, InPerformance: limit is int lq && lq <= 60);
    }
}

/// <summary>The remaining per-user Windows tweaks: offered, explained honestly, and not pushed.</summary>
public sealed class ComfortTweakRule : IRecommendationRule
{
    private sealed record T(string Id, string Title, ActionCategory Cat, string Why, string Benefit, string Trade);
    private static readonly T[] Tweaks =
    {
        new("transparency", "Turn off window transparency effects", ActionCategory.Graphics, "Transparency blur is drawn by the graphics card on every frame of the desktop.", "Small; only noticeable on weak integrated graphics.", "Windows looks flatter."),
        new("visualfx", "Windows visual effects: best performance", ActionCategory.Graphics, "Fewer shadows, fades and smoothing in Windows.", "Small; only noticeable on weak integrated graphics or low-memory PCs.", "Windows looks plainer."),
        new("animations", "Turn off window animations", ActionCategory.Windows, "Windows stops animating windows and menus.", "None for game frame rate; makes alt-tabbing feel quicker.", "Less polish."),
        new("bgapps", "Stop apps running in the background", ActionCategory.Background, "Store apps stop running when you are not using them.", "Frees a little processor and memory.", "Some Store apps stop updating live tiles and notifications."),
        new("toasts", "Silence notification pop-ups", ActionCategory.Windows, "Notifications appearing over the game can steal focus.", "None for frame rate; fewer interruptions.", "You miss notifications until you turn it back on."),
        new("menudelay", "Make menus open instantly", ActionCategory.Windows, "Removes the short delay before Windows menus open.", "None for games.", "None."),
        new("startdelay", "Skip the startup app delay", ActionCategory.Windows, "Startup apps no longer wait after sign-in.", "None for games; the desktop is usable sooner.", "Sign-in may feel busier for a moment."),
        new("suggestions", "Turn off Windows suggestions and tips", ActionCategory.Windows, "Stops suggested apps and promotions.", "None measurable.", "None."),
        new("websearch", "Keep Start search on this PC only", ActionCategory.Windows, "Start search no longer queries the web.", "None for games.", "No web results in Start."),
    };

    public IEnumerable<Recommendation> Evaluate(EvaluationContext c)
    {
        var hw = c.Hardware;
        bool weakGraphics = !hw.DedicatedGpus.Any();
        bool lowMem = hw.TotalMemoryGb < 8.5;
        foreach (var t in Tweaks)
        {
            bool helps = (t.Id is "transparency" or "visualfx" && weakGraphics) || (t.Id is "visualfx" or "bgapps" && lowMem);
            var ev = new List<EvidenceLine>();
            if (t.Id is "transparency" or "visualfx") ev.Add(weakGraphics ? R.O("This PC has only integrated graphics, which shares memory and power with the processor.") : R.O($"This PC has a dedicated graphics card ({hw.DedicatedGpus.First().Name}), which handles desktop effects easily."));
            if (t.Id is "visualfx" or "bgapps") ev.Add(lowMem ? R.O($"Only {R.N(hw.TotalMemoryGb, "0.#")} GB of memory is installed.") : R.O($"{R.N(hw.TotalMemoryGb, "0.#")} GB of memory is installed, which is comfortable."));
            if (ev.Count == 0) ev.Add(R.O("This is a Windows comfort setting; Nighty has no measurement linking it to game speed."));
            yield return new Recommendation(t.Id, ActionKind.Tweak, t.Cat, t.Title, helps ? Applicability.Recommended : Applicability.Optional, t.Why, "", "", ev,
                helps ? BenefitKind.FramePacing : BenefitKind.None, t.Benefit, t.Trade, helps ? Confidence.Low : Confidence.High, RiskLevel.Low, PermissionNeed.None, true,
                "Your exact original Windows values are saved and restored when you switch it off.", helps ? "Compare a benchmark run before and after." : "Not applicable to frame rate.",
                InBalanced: false, InPerformance: helps);
        }
    }
}

public sealed class RecommendationEngine
{
    private readonly IReadOnlyList<IRecommendationRule> _rules;

    public RecommendationEngine(IEnumerable<IRecommendationRule>? rules = null)
        => _rules = (rules ?? DefaultRules()).ToList();

    public static IEnumerable<IRecommendationRule> DefaultRules() => new IRecommendationRule[]
    {
        new GpuPreferenceRule(), new PowerRule(), new ThrottleRule(), new WindowsGameModeRule(), new GameDvrRule(), new FrameCapRule(),
        new PriorityRule(), new CalmRule(), new MemoryRule(), new TimerRule(), new AwakeRule(), new ComfortTweakRule(),
    };

    /// <summary>Read-only: looks at the PC and returns a plan. Nothing is changed.</summary>
    public OptimizationPlan Evaluate(EvaluationContext ctx)
    {
        var items = new List<Recommendation>();
        var notes = new List<string>();
        foreach (var rule in _rules)
        {
            try { items.AddRange(rule.Evaluate(ctx)); }
            catch (Exception ex) { notes.Add($"{rule.GetType().Name} could not be evaluated: {ex.Message}"); }
        }
        // One entry per action id; a profile cannot select something twice.
        items = items.GroupBy(i => i.Id).Select(g => g.First()).ToList();
        var findings = BottleneckAnalyzer.Analyze(ctx.Hardware, ctx.Window, ctx.State.FrameRate);
        if (ctx.Window.Count < BottleneckAnalyzer.MinSamples) notes.Add("Recommendations that depend on measurements are cautious until Nighty has watched the PC for a few seconds.");
        return new OptimizationPlan(ctx.Now, ctx.Hardware.Fingerprint, items, findings, notes);
    }
}

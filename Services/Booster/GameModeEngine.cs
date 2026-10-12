using System.Diagnostics;

namespace Nighty.Services.Booster;

/// <summary>Identifiers of everything Game Mode can do. The first eight are Game Mode's own; the tweak ids live in <see cref="TweaksService"/>.</summary>
public static class ActionIds
{
    public const string Power = "power", Priority = "priority", Throttle = "throttle", Calm = "calm", Memory = "memory",
        Timer = "timer", GameMode = "gamemode", Awake = "awake";

    public static readonly IReadOnlyList<string> Session = new[] { Power, Priority, Throttle, Calm, Memory, Timer, GameMode, Awake };
    public static bool IsSession(string id) => Session.Contains(id);
}

public enum ActionState { Applied, AppliedUnverified, AlreadyInPlace, Waiting, Skipped, Failed }

public sealed record ActionReport(string ActionId, ActionState State, string Text)
{
    public bool Good => State is ActionState.Applied or ActionState.AlreadyInPlace;
}

/// <summary>Makes the targets Game Mode changes. Swapped for in-memory fakes in tests.</summary>
public interface ITargetFactory
{
    ISettingTarget GameBarAutoMode();
    ISettingTarget PowerScheme();
    ISettingTarget PowerOverlay();
    ISettingTarget Priority(ProcIdentity id, string name);
    ISettingTarget Throttle(ProcIdentity id, string name);
}

public sealed class DefaultTargetFactory : ITargetFactory
{
    private readonly IProcessControl _ctl;
    public DefaultTargetFactory(IProcessControl ctl) => _ctl = ctl;
    public ISettingTarget GameBarAutoMode() => new RegistryValueTarget(@"Software\Microsoft\GameBar", "AutoGameModeEnabled");
    public ISettingTarget PowerScheme() => new PowerSchemeTarget();
    public ISettingTarget PowerOverlay() => new PowerOverlayTarget();
    public ISettingTarget Priority(ProcIdentity id, string name) => new ProcessPriorityTarget(_ctl, id, name);
    public ISettingTarget Throttle(ProcIdentity id, string name) => new ProcessThrottleTarget(_ctl, id, name);
}

/// <summary>Things that exist only while Nighty runs and are given back by Windows if it dies (so they cannot be left behind).</summary>
public interface ISessionResources
{
    bool BeginTimerResolution();
    void EndTimerResolution();
    bool BeginKeepAwake();
    void EndKeepAwake();
}

public sealed class NativeSessionResources : ISessionResources
{
    public bool BeginTimerResolution() => Native.NativeMethods.timeBeginPeriod(1) == 0;
    public void EndTimerResolution() => Native.NativeMethods.timeEndPeriod(1);
    public bool BeginKeepAwake() => Native.NativeMethods.SetThreadExecutionState(Native.NativeMethods.ES_CONTINUOUS | Native.NativeMethods.ES_DISPLAY_REQUIRED | Native.NativeMethods.ES_SYSTEM_REQUIRED) != 0;
    public void EndKeepAwake() => Native.NativeMethods.SetThreadExecutionState(Native.NativeMethods.ES_CONTINUOUS);
}

public sealed class GameModeDeps
{
    public required IGameLocator Game { get; init; }
    public required IProcessSource Processes { get; init; }
    public required IProcessControl Control { get; init; }
    public required ITargetFactory Targets { get; init; }
    public required ChangeTransactions Journal { get; init; }
    public required ISessionResources Resources { get; init; }
    /// <summary>The latest hardware profile (power state is refreshed first through <see cref="RefreshPower"/>).</summary>
    public required Func<HardwareProfile?> Hardware { get; init; }
    public Action RefreshPower { get; init; } = () => { };
    /// <summary>Total and available physical memory right now, or null if unknown.</summary>
    public Func<(long Total, long Available)?> Memory { get; init; } = () => null;
    public Action<string> Log { get; init; } = _ => { };
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed record ActivationReport(string SessionId, IReadOnlyList<ActionReport> Actions)
{
    public ActionReport? Of(string id) => Actions.FirstOrDefault(a => a.ActionId == id);
}

/// <summary>
/// Game Mode's brain. Applies only the actions it is told are approved, only where this PC supports them, follows the actual
/// Roblox client processes (by id and start time), records every change in the journal before making it, and undoes exactly
/// what it did, leaving alone anything that was changed by someone else in the meantime.
/// </summary>
public sealed class GameModeEngine
{
    private const string BalancedPlanId = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(10);

    private readonly GameModeDeps _d;
    private readonly object _lock = new();
    private IReadOnlySet<string> _approved = new HashSet<string>();
    private readonly Dictionary<string, ActionReport> _reports = new();
    private readonly HashSet<ProcIdentity> _prioDone = new(), _throttleDone = new(), _trimmedFor = new();
    private readonly Dictionary<ProcIdentity, string> _calmed = new();
    private readonly Dictionary<(string Action, ProcIdentity Id), (int Tries, long Last)> _attempts = new();
    private bool _timerOn, _awakeOn, _powerTried;

    public GameModeEngine(GameModeDeps deps) => _d = deps;

    public bool IsActive { get; private set; }
    public string? SessionId { get; private set; }
    public event Action? Changed;

    public int AppsSlowed { get { lock (_lock) return _calmed.Count; } }
    public double MemoryFreedMb { get; private set; }
    public IReadOnlyList<ActionReport> Reports { get { lock (_lock) return _reports.Values.ToList(); } }
    public RestoreReport? LastRestore { get; private set; }

    // ---------------------------------------------------------------- activation
    public ActivationReport Activate(IReadOnlySet<string> approved)
    {
        lock (_lock)
        {
            if (IsActive) return new ActivationReport(SessionId!, Reports);
            SessionId = Guid.NewGuid().ToString("N");
            _approved = approved;
            _reports.Clear(); _prioDone.Clear(); _throttleDone.Clear(); _trimmedFor.Clear(); _calmed.Clear(); _attempts.Clear();
            MemoryFreedMb = 0; _powerTried = false;
            IsActive = true;

            try { _d.RefreshPower(); } catch (Exception ex) { _d.Log("Power refresh failed: " + ex.Message); }
            if (_approved.Contains(ActionIds.Power)) ApplyPower();
            if (_approved.Contains(ActionIds.GameMode)) ApplyGameBar();
            if (_approved.Contains(ActionIds.Timer)) StartTimer();
            if (_approved.Contains(ActionIds.Awake)) StartAwake();
            TickLocked();
            _d.Log("Game Mode on: " + string.Join(", ", _reports.Values.Select(r => $"{r.ActionId}={r.State}")));
        }
        Changed?.Invoke();
        return new ActivationReport(SessionId!, Reports);
    }

    // ---------------------------------------------------------------- watching Roblox
    /// <summary>Called every few seconds while active: picks up Roblox clients that started or exited since the last call.</summary>
    public void Tick()
    {
        bool changed;
        lock (_lock)
        {
            if (!IsActive) return;
            changed = TickLocked();
        }
        if (changed) Changed?.Invoke();
    }

    private bool TickLocked()
    {
        RobloxSession session;
        try { session = _d.Game.Find(); }
        catch (Exception ex) { _d.Log("Roblox detection failed: " + ex.Message); return false; }

        var before = _reports.Values.Select(r => (r.ActionId, r.State, r.Text)).ToHashSet();
        var live = session.Clients.Select(c => c.Id).ToHashSet();

        // Clients that are gone: their entries are no longer restorable, so close them out and forget them.
        ForgetGone(_prioDone, live); ForgetGone(_throttleDone, live); ForgetGone(_trimmedFor, live);
        if (live.Count == 0) { ReleaseCalm(); }

        bool wantPrio = _approved.Contains(ActionIds.Priority), wantThrottle = _approved.Contains(ActionIds.Throttle),
             wantCalm = _approved.Contains(ActionIds.Calm), wantMem = _approved.Contains(ActionIds.Memory);

        if (wantPrio) ApplyToClients(ActionIds.Priority, session, live, _prioDone, c => _d.Targets.Priority(c.Id, "Roblox"), PriorityNames.High, "Roblox set to High priority");
        if (wantThrottle) ApplyToClients(ActionIds.Throttle, session, live, _throttleDone, c => _d.Targets.Throttle(c.Id, "Roblox"),
            ThrottleState.Encode(ThrottleState.ExecutionSpeed | ThrottleState.IgnoreTimerResolution, 0), "Windows throttling is off for Roblox");
        if (live.Count > 0)
        {
            if (wantCalm) ApplyCalm(session);
            if (wantMem) TrimBackground(live);
        }
        else
        {
            foreach (var id in new[] { wantCalm ? ActionIds.Calm : null, wantMem ? ActionIds.Memory : null }.OfType<string>())
                SetReport(id, ActionState.Waiting, "Waiting for Roblox to start");
        }

        foreach (var id in new[] { wantPrio ? ActionIds.Priority : null, wantThrottle ? ActionIds.Throttle : null }.OfType<string>())
            if (live.Count == 0) SetReport(id, ActionState.Waiting, "Waiting for Roblox to start");

        foreach (var note in session.Notes.Distinct()) _d.Log(note);
        var after = _reports.Values.Select(r => (r.ActionId, r.State, r.Text)).ToHashSet();
        return !before.SetEquals(after);
    }

    private void ForgetGone(HashSet<ProcIdentity> set, HashSet<ProcIdentity> live)
    {
        foreach (var id in set.Where(i => !live.Contains(i)).ToList())
        {
            set.Remove(id);
            // Let the journal mark the dead process' entries finished so they do not pile up.
            _d.Journal.RestoreAll(SessionId, e => e.TargetKey.EndsWith("|" + id, StringComparison.Ordinal));
        }
    }

    private bool MayTry(string action, ProcIdentity id)
    {
        if (!_attempts.TryGetValue((action, id), out var a)) return true;
        if (a.Tries >= MaxAttempts) return false;
        return _d.Time.GetElapsedTime(a.Last) >= RetryGap;
    }

    private void Failed(string action, ProcIdentity id)
    {
        _attempts.TryGetValue((action, id), out var a);
        _attempts[(action, id)] = (a.Tries + 1, _d.Time.GetTimestamp());
    }

    private void ApplyToClients(string action, RobloxSession session, HashSet<ProcIdentity> live, HashSet<ProcIdentity> done,
                                Func<RobloxClient, ISettingTarget> target, string value, string okText)
    {
        if (session.Clients.Count == 0) { SetReport(action, ActionState.Waiting, "Waiting for Roblox to start"); return; }
        int ok = 0, failed = 0; string? why = null; bool unverified = false;
        foreach (var c in session.Clients)
        {
            if (done.Contains(c.Id)) { ok++; continue; }
            if (!MayTry(action, c.Id)) { failed++; continue; }
            var r = _d.Journal.Apply(SessionId!, action, target(c), value);
            if (r.Ok || r.Status == ApplyStatus.Unverified) { done.Add(c.Id); ok++; unverified |= r.Status == ApplyStatus.Unverified; }
            else if (r.Status == ApplyStatus.Gone) { /* exited while we looked */ }
            else { failed++; why = r.Detail; Failed(action, c.Id); }
        }
        string where = session.Clients.Count == 1 ? "" : $" ({ok} of {session.Clients.Count} clients)";
        if (failed == 0) SetReport(action, unverified ? ActionState.AppliedUnverified : ActionState.Applied, okText + where + (unverified ? " (could not confirm)" : ""));
        else SetReport(action, ok > 0 ? ActionState.AppliedUnverified : ActionState.Failed, $"{ok} set, {failed} could not be changed ({why ?? "access denied"})");
    }

    // ---------------------------------------------------------------- power
    private void ApplyPower()
    {
        _powerTried = true;
        var hw = _d.Hardware();
        if (hw == null) { SetReport(ActionIds.Power, ActionState.Skipped, "Hardware has not been scanned yet, so the power setting was left alone"); return; }
        var p = hw.Power;
        var gate = ActionEligibility.Power(hw);
        if (!gate.Allowed) { SetReport(ActionIds.Power, ActionState.Skipped, gate.Reason); return; }

        bool alreadyHigh = string.Equals(p.ActiveSchemeId, p.HighPerformancePlanId, StringComparison.OrdinalIgnoreCase) && p.HighPerformancePlanId != null;
        if (alreadyHigh) { SetReport(ActionIds.Power, ActionState.AlreadyInPlace, "High performance power plan is already active"); return; }
        if (p.Mode == PowerMode.BestPerformance) { SetReport(ActionIds.Power, ActionState.AlreadyInPlace, "Power mode is already Best performance"); return; }

        bool balanced = string.Equals(p.ActiveSchemeId, BalancedPlanId, StringComparison.OrdinalIgnoreCase);
        ApplyOutcome r;
        string what;
        if (p.PowerModeSupported && balanced)
        {
            r = _d.Journal.Apply(SessionId!, ActionIds.Power, _d.Targets.PowerOverlay(), PowerOverlayTarget.BestPerformance.ToString());
            what = "Power mode set to Best performance";
        }
        else if (p.HasHighPerformancePlan)
        {
            r = _d.Journal.Apply(SessionId!, ActionIds.Power, _d.Targets.PowerScheme(), p.HighPerformancePlanId);
            what = "Switched to the High performance power plan";
        }
        else { SetReport(ActionIds.Power, ActionState.Skipped, "This PC offers neither a High performance plan nor a power mode to switch to"); return; }
        Report(ActionIds.Power, r, what);
    }

    private void ApplyGameBar()
    {
        var r = _d.Journal.Apply(SessionId!, ActionIds.GameMode, _d.Targets.GameBarAutoMode(), RegistryValueTarget.Dword(1));
        Report(ActionIds.GameMode, r, "Windows Game Mode turned on");
    }

    private void Report(string action, ApplyOutcome r, string okText)
    {
        switch (r.Status)
        {
            case ApplyStatus.Verified: SetReport(action, ActionState.Applied, okText); break;
            case ApplyStatus.Unverified: SetReport(action, ActionState.AppliedUnverified, okText + " (could not confirm it took effect)"); break;
            case ApplyStatus.AlreadyInPlace: SetReport(action, ActionState.AlreadyInPlace, "Already set"); break;
            case ApplyStatus.CouldNotRead: SetReport(action, ActionState.Skipped, "Skipped: Nighty could not read the current setting, so it could not promise to put it back (" + r.Detail + ")"); break;
            case ApplyStatus.JournalFailed: SetReport(action, ActionState.Failed, r.Detail); break;
            default: SetReport(action, ActionState.Failed, r.Detail); break;
        }
    }

    // ---------------------------------------------------------------- calm background apps
    private void ApplyCalm(RobloxSession session)
    {
        var all = _d.Processes.List();
        int failed = 0;
        foreach (var entry in all)
        {
            if (!BackgroundPolicy.IsCandidateName(entry.Name) || entry.Pid == Environment.ProcessId) continue;
            var d = _d.Processes.Describe(entry.Pid);
            if (d == null || _calmed.ContainsKey(d.Id) || !MayTry(ActionIds.Calm, d.Id)) continue;

            var prio = _d.Targets.Priority(d.Id, entry.Name);
            var cur = prio.Read();
            if (!cur.IsOk) { if (cur.Status != ReadStatus.Gone) { failed++; Failed(ActionIds.Calm, d.Id); } continue; }
            if (cur.Value is PriorityNames.Idle or PriorityNames.BelowNormal) continue;   // already waiting; nothing of ours to undo

            var rp = _d.Journal.Apply(SessionId!, ActionIds.Calm, prio, PriorityNames.BelowNormal);
            if (!rp.Ok && rp.Status != ApplyStatus.Unverified) { if (rp.Status != ApplyStatus.Gone) { failed++; Failed(ActionIds.Calm, d.Id); } continue; }
            // Efficiency mode: off the fast cores. Failure here is fine; the priority change stands on its own.
            _d.Journal.Apply(SessionId!, ActionIds.Calm, _d.Targets.Throttle(d.Id, entry.Name), ThrottleState.Encode(ThrottleState.ExecutionSpeed, ThrottleState.ExecutionSpeed));
            _calmed[d.Id] = entry.Name;
        }
        // Forget the ones that exited.
        foreach (var id in _calmed.Keys.ToList())
            if (_d.Processes.Describe(id.Pid) is not { } cur || cur.Id != id) _calmed.Remove(id);

        string text = _calmed.Count == 0 ? "No background apps needed slowing" : $"{_calmed.Count} background app process{(_calmed.Count == 1 ? "" : "es")} waiting" + (failed > 0 ? $" ({failed} could not be changed)" : "");
        SetReport(ActionIds.Calm, ActionState.Applied, text);
    }

    /// <summary>Roblox closed: let the background apps go back to normal now rather than at the end of Game Mode.</summary>
    private void ReleaseCalm()
    {
        if (_calmed.Count == 0 && !_d.Journal.Pending.Any(e => e.SessionId == SessionId && e.ActionId == ActionIds.Calm)) return;
        _d.Journal.RestoreAll(SessionId, e => e.ActionId == ActionIds.Calm);
        _calmed.Clear();
    }

    // ---------------------------------------------------------------- memory
    private void TrimBackground(HashSet<ProcIdentity> live)
    {
        if (live.All(_trimmedFor.Contains)) return;
        var mem = _d.Memory();
        if (mem is { } m && m.Total > 0 && m.Available * 100.0 / m.Total >= 30)
        {
            foreach (var id in live) _trimmedFor.Add(id);
            SetReport(ActionIds.Memory, ActionState.Skipped, $"Skipped: {m.Available * 100.0 / m.Total:0}% of memory is free, so trimming would only slow apps down when they need their pages back");
            return;
        }
        foreach (var id in live) _trimmedFor.Add(id);
        double freed = 0; int n = 0;
        foreach (var entry in _d.Processes.List())
        {
            if (!BackgroundPolicy.IsCandidateName(entry.Name) || entry.Pid == Environment.ProcessId) continue;
            var d = _d.Processes.Describe(entry.Pid);
            if (d == null) continue;
            if (_d.Control.TrimWorkingSet(d.Id) is long bytes) { freed += bytes / 1048576.0; n++; }
        }
        MemoryFreedMb += freed;
        _d.Journal.Note(SessionId!, ActionIds.Memory, "Background memory", $"Trimmed {n} background processes ({freed:0} MB). Windows hands pages back as each app needs them, so there is nothing to restore.");
        SetReport(ActionIds.Memory, ActionState.Applied, $"{MemoryFreedMb:0} MB given back by background apps");
    }

    // ---------------------------------------------------------------- in-process resources
    private void StartTimer()
    {
        _timerOn = _d.Resources.BeginTimerResolution();
        SetReport(ActionIds.Timer, _timerOn ? ActionState.Applied : ActionState.Failed,
            _timerOn ? "1 ms timer resolution requested (Windows applies it to Nighty's own timers; the effect on Roblox is unverified)" : "Windows rejected the timer request");
    }

    private void StartAwake()
    {
        _awakeOn = _d.Resources.BeginKeepAwake();
        SetReport(ActionIds.Awake, _awakeOn ? ActionState.Applied : ActionState.Failed, _awakeOn ? "Display and system kept awake" : "Windows rejected the request");
    }

    // ---------------------------------------------------------------- deactivation
    public RestoreReport Deactivate()
    {
        RestoreReport report;
        lock (_lock)
        {
            if (!IsActive) return RestoreReport.Empty;
            if (_timerOn) { _d.Resources.EndTimerResolution(); _timerOn = false; }
            if (_awakeOn) { _d.Resources.EndKeepAwake(); _awakeOn = false; }
            report = _d.Journal.RestoreAll(SessionId);
            LastRestore = report;
            IsActive = false;
            _reports.Clear(); _calmed.Clear(); _prioDone.Clear(); _throttleDone.Clear(); _trimmedFor.Clear();
            _d.Log("Game Mode off. " + report.Summary());
        }
        Changed?.Invoke();
        return report;
    }

    /// <summary>
    /// Run once when Nighty starts: undoes whatever an earlier run left behind (after a crash, a kill, or a restart), whatever
    /// session it belonged to.
    /// </summary>
    public RestoreReport RecoverAtStartup()
    {
        lock (_lock)
        {
            _d.Journal.Load();
            var load = _d.Journal.LoadResult;
            if (load.State == LoadState.Unreadable) _d.Log("The recovery file could not be read, so automatic restoring is not possible: " + load.Detail);
            if (!_d.Journal.HasPending) return RestoreReport.Empty;
            _d.Log($"Found {_d.Journal.Pending.Count} unfinished change(s) from an earlier run; restoring");
            var report = _d.Journal.RestoreAll();
            LastRestore = report;
            _d.Log("Startup recovery: " + report.Summary());
            return report;
        }
    }

    private void SetReport(string id, ActionState state, string text) => _reports[id] = new ActionReport(id, state, text);

    public bool PowerWasTried => _powerTried;
}

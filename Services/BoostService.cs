using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services.Booster;

namespace Nighty.Services;

/// <summary>One row of the boost list: what it does, and what happened (or would happen) on this PC.</summary>
public sealed record BoostLine(string Id, string Title, string Why, string Status, StatusKind Kind);

/// <summary>
/// The one-button FPS boost. It switches on a short, fixed set of changes that measurably help Roblox, and puts every one of them
/// back when switched off. Two kinds of change are involved:
/// <list type="bullet">
/// <item><b>Settings Roblox reads when it starts</b> (graphics card choice, the frame-rate flag, Game DVR). They are applied the
/// moment the boost is switched on and kept while it stays on, so the next Roblox launch already has them.</item>
/// <item><b>Game Mode actions</b> (power, throttling, priority, Windows Game Mode). They apply while Roblox is running and are
/// undone the moment it closes, by the same journalled engine as before.</item>
/// </list>
/// Things that did not help (or got in the way) are deliberately absent: no slowing of other programs, no memory trimming, no 1 ms
/// timer request, no cache deleting.
/// </summary>
public sealed class BoostService
{
    public static readonly IReadOnlyList<string> TweakIds = new[] { "gpu", "fpscap", "gamedvr" };
    public static readonly IReadOnlyList<string> SessionIds = new[] { ActionIds.Power, ActionIds.Throttle, ActionIds.Priority, ActionIds.GameMode };

    private sealed record Item(string Id, string Title, string Why);
    private static readonly Item[] Catalog =
    {
        new("gpu", "Strong graphics card",
            "On a PC with two graphics chips, Windows can run Roblox on the weak one. This tells it to always use the fast one."),
        new("fpscap", "FPS cap removed",
            "Roblox stops at 60 FPS unless told otherwise. This raises the limit so a fast screen can be used fully."),
        new(ActionIds.Power, "Max-performance power",
            "Stops Windows holding the processor and graphics card back to save power. Plugged in only."),
        new(ActionIds.Throttle, "No Windows throttling",
            "Keeps Roblox on the fast cores with steady timing, even when it is windowed or briefly behind another window."),
        new(ActionIds.Priority, "Roblox priority",
            "Roblox is scheduled ahead of background work while you play."),
        new(ActionIds.GameMode, "Windows Game Mode",
            "Asks Windows to hold back its own background work, such as driver installs, while you play."),
        new("gamedvr", "Game DVR off",
            "Stops Windows' Game Bar recording gameplay behind your back, which costs graphics and disk time."),
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Text, StatusKind Kind)> _notes = new();
    private Dictionary<string, (string Text, StatusKind Kind)> _preview = new();
    private string? _versionFolder;

    public event Action? Changed;

    /// <summary>Set once start-up recovery has finished. Until then nothing is switched on, so recovery cannot undo a change made a moment earlier.</summary>
    public bool Ready { get; set; }

    /// <summary>True from the moment the boost is switched on until it is switched off (including while it waits for Roblox).</summary>
    public bool IsOn => Svc.S.Game.AutoGameMode;
    /// <summary>The Game Mode actions are applied right now (Roblox is running and the boost is on).</summary>
    public bool IsApplied => Svc.GameMode.IsActive;
    public bool Busy { get; private set; }
    /// <summary>Set when the boost was switched on while Roblox was already running: it needs one restart to pick up the new settings.</summary>
    public bool NeedsRestart { get; private set; }

    // ------------------------------------------------------------------------------------------------ switching
    public async Task EnableAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Busy = true; Changed?.Invoke();
            await Svc.Booster.EnsureScannedAsync();
            var plan = await Task.Run(() => Svc.Booster.BuildPlan());
            bool robloxUp = await Task.Run(() => Svc.Roblox.IsRunning);

            _notes.Clear();
            NeedsRestart = false;
            GameChoices.Set(Svc.S.Game, SessionIds);

            foreach (var id in TweakIds)
            {
                var rec = plan?.Find(id);
                if (rec == null) { _notes[id] = ("Skipped: Nighty could not read this PC", StatusKind.Warning); continue; }
                if (rec.Applicability == Applicability.NotApplicable) { _notes[id] = ("Not needed here: " + rec.Reason, StatusKind.Neutral); continue; }
                if (rec.Applicability == Applicability.Discouraged) { _notes[id] = ("Skipped: " + rec.Reason, StatusKind.Warning); continue; }
                if (Svc.Tweaks.IsApplied(id)) { _notes[id] = ("On", StatusKind.Success); continue; }
                var r = Svc.Tweaks.Set(id, true);
                _notes[id] = r.Kind == StatusKind.Success ? ("On", StatusKind.Success) : (r.Text, r.Kind);
                if (r.Kind == StatusKind.Success && id is "gpu" or "fpscap") NeedsRestart |= robloxUp;
            }

            var b = Svc.S.Booster;
            b.PlanReviewed = true; b.Profile = BoosterProfile.Custom; b.KeptFromOlderSetup = false;
            b.ApprovedFingerprint = plan?.HardwareFingerprint; b.ApprovedUtc = DateTime.UtcNow;
            Svc.S.Game.AutoGameMode = true;
            Svc.Settings.Save();
            _versionFolder = Svc.Roblox.FindVersionFolder();
            Log.Info("FPS boost on: " + string.Join(", ", _notes.Select(n => $"{n.Key}={n.Value.Kind}")));

            if (robloxUp) await Svc.GameMode.SetActiveAsync(true);
        }
        finally { Busy = false; _gate.Release(); Changed?.Invoke(); }
    }

    public async Task DisableAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Busy = true; Changed?.Invoke();
            Svc.S.Game.AutoGameMode = false;
            await Svc.GameMode.SetActiveAsync(false);
            Svc.Tweaks.RevertAll();   // the three boost settings, and anything an older Nighty left on
            _notes.Clear(); NeedsRestart = false;
            Svc.Settings.Save();
            Log.Info("FPS boost off");
        }
        finally { Busy = false; _gate.Release(); Changed?.Invoke(); }
    }

    /// <summary>Roblox started or closed: the Game Mode actions follow it while the boost is on.</summary>
    public async Task RobloxChangedAsync(bool running)
    {
        if (!IsOn || !Ready) return;
        await Svc.GameMode.SetActiveAsync(running);
        Changed?.Invoke();
    }

    /// <summary>Called every couple of seconds: after a Roblox update the client lives in a new folder, so the settings are written there too (before the next launch reads them).</summary>
    public void Tick()
    {
        if (!IsOn || !Ready || !_gate.Wait(0)) return;
        try
        {
            var folder = Svc.Roblox.FindVersionFolder();
            if (folder == null || folder == _versionFolder) return;
            _versionFolder = folder;
            foreach (var id in new[] { "gpu", "fpscap" })
                if (Svc.Tweaks.IsApplied(id)) Svc.Tweaks.Set(id, true);
            Log.Info("Roblox moved to a new version folder; boost settings written there");
        }
        catch (Exception ex) { Log.Warn("Boost tick failed", ex); }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------------------------------------ what the page shows
    /// <summary>Read-only look at this PC so the list can say, before anything is switched on, what applies here.</summary>
    public async Task RefreshPreviewAsync()
    {
        try
        {
            await Svc.Booster.EnsureScannedAsync();
            var plan = await Task.Run(() => Svc.Booster.BuildPlan());
            var p = new Dictionary<string, (string, StatusKind)>();
            foreach (var item in Catalog)
            {
                var rec = plan?.Find(item.Id);
                if (rec == null) continue;
                p[item.Id] = rec.Applicability switch
                {
                    Applicability.NotApplicable => ("Not needed here: " + rec.Reason, StatusKind.Neutral),
                    Applicability.Discouraged => ("Will be skipped: " + rec.Reason, StatusKind.Warning),
                    _ => ("Ready", StatusKind.Neutral),
                };
            }
            _preview = p;
        }
        catch (Exception ex) { Log.Warn("Boost preview failed", ex); }
        Changed?.Invoke();
    }

    public IReadOnlyList<BoostLine> Lines()
    {
        var results = Svc.GameMode.IsActive ? Svc.GameMode.Results : new Dictionary<string, OptionResult>();
        var list = new List<BoostLine>();
        foreach (var item in Catalog)
        {
            string text = ""; StatusKind kind = StatusKind.Neutral;
            if (!IsOn)
            {
                if (_preview.TryGetValue(item.Id, out var pv)) (text, kind) = pv;
            }
            else if (TweakIds.Contains(item.Id))
            {
                if (_notes.TryGetValue(item.Id, out var n)) (text, kind) = n;
            }
            else if (results.TryGetValue(item.Id, out var r)) { text = r.Text; kind = r.Kind; }
            else { text = "Starts when Roblox opens"; kind = StatusKind.Info; }
            list.Add(new BoostLine(item.Id, item.Title, item.Why, text, kind));
        }
        return list;
    }

    /// <summary>Ids the page lists, in order. Exposed so a test can check them against the rule set.</summary>
    public static IReadOnlyList<string> CatalogIds => Catalog.Select(i => i.Id).ToList();
}

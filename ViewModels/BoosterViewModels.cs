using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Services.Booster;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed record FactViewModel(string Label, string Value);

public sealed class MetricCardViewModel : ObservableObject
{
    private string _value = "—", _detail = "";
    private IReadOnlyList<double> _series = Array.Empty<double>();
    private StatusKind _kind = StatusKind.Neutral;
    public required string Title { get; init; }
    public double Maximum { get; init; } = 100;
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public IReadOnlyList<double> Series { get => _series; set => Set(ref _series, value); }
    public StatusKind Kind { get => _kind; set => Set(ref _kind, value); }
}

public sealed class FindingViewModel
{
    public FindingViewModel(Finding f)
    {
        Statement = f.Statement;
        Evidence = string.Join("\n", f.Evidence);
        HowToVerify = f.HowToVerify == null ? "" : "To check: " + f.HowToVerify;
        (Label, Kind) = f.Level switch
        {
            EvidenceKind.Observation => ("MEASURED", StatusKind.Success),
            EvidenceKind.Hypothesis => ("POSSIBLE", StatusKind.Warning),
            _ => ("CAN'T SEE", StatusKind.Neutral),
        };
        Confidence = f.Confidence + " confidence";
    }
    public string Statement { get; }
    public string Evidence { get; }
    public string HowToVerify { get; }
    public string Label { get; }
    public string Confidence { get; }
    public StatusKind Kind { get; }
}

public sealed class SourceViewModel
{
    public SourceViewModel(DataSourceStatus s) { Name = s.Name; Detail = s.Detail; Available = s.Available; Kind = s.Available ? StatusKind.Success : StatusKind.Neutral; }
    public string Name { get; }
    public string Detail { get; }
    public bool Available { get; }
    public StatusKind Kind { get; }
}

/// <summary>The live picture of this PC: hardware, current load, and what the evidence says (and does not say).</summary>
public sealed class OverviewViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IDisposable? _sub;
    private HardwareProfile? _shownHw;
    private int _tick;
    private FrameRateConfig? _cfg;
    private DateTime _cfgAt;
    private string _gameTitle = "Roblox is not running", _gameDetail = "", _banner = "", _scanText = "Scanning this PC…";
    private StatusKind _gameKind = StatusKind.Neutral, _bannerKind = StatusKind.Info;
    private bool _scanning;

    public ObservableCollection<FactViewModel> Facts { get; } = new();
    public ObservableCollection<MetricCardViewModel> Cards { get; } = new();
    public ObservableCollection<FindingViewModel> Findings { get; } = new();
    public ObservableCollection<SourceViewModel> Sources { get; } = new();

    public string GameTitle { get => _gameTitle; private set => Set(ref _gameTitle, value); }
    public string GameDetail { get => _gameDetail; private set => Set(ref _gameDetail, value); }
    public StatusKind GameKind { get => _gameKind; private set => Set(ref _gameKind, value); }
    public string Banner { get => _banner; private set => Set(ref _banner, value); }
    public StatusKind BannerKind { get => _bannerKind; private set => Set(ref _bannerKind, value); }
    public string ScanText { get => _scanText; private set => Set(ref _scanText, value); }
    public bool Scanning { get => _scanning; private set { Set(ref _scanning, value); RescanCommand?.Refresh(); } }

    public AsyncCommand RescanCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand SaveCommand { get; }

    public OverviewViewModel()
    {
        _timer.Tick += (_, _) => Refresh();
        RescanCommand = new AsyncCommand(RescanAsync, () => !Scanning);
        CopyCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(Svc.Booster.BuildDiagnostics()); Svc.Toast.Show("Diagnostics", "Copied. Names and folders were removed.", true); }
            catch (Exception ex) { Log.Warn("Copying diagnostics failed", ex); Dialogs.Info("Could not copy", ex.Message); }
        });
        SaveCommand = new RelayCommand(() =>
        {
            var dlg = new SaveFileDialog { Title = "Save diagnostics", Filter = "JSON (*.json)|*.json", FileName = $"nighty-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json" };
            if (dlg.ShowDialog() != true) return;
            try { File.WriteAllText(dlg.FileName, Svc.Booster.BuildDiagnostics()); Svc.Toast.Show("Diagnostics", "Saved. Names and folders were removed.", true); }
            catch (Exception ex) { Log.Warn("Saving diagnostics failed", ex); Dialogs.Info("Could not save", ex.Message); }
        });
        Svc.Booster.Hardware.Updated += (_, _, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { _shownHw = null; Refresh(); }));
    }

    public void Attach()
    {
        if (_sub != null) return;
        _sub = Svc.Booster.Telemetry.Subscribe();
        _timer.Start();
        Refresh();
    }

    public void Detach()
    {
        _timer.Stop();
        _sub?.Dispose(); _sub = null;
    }

    private async Task RescanAsync()
    {
        Scanning = true; ScanText = "Scanning this PC…";
        try { await Svc.Booster.RescanAsync(); }
        finally { Scanning = false; }
        _shownHw = null; Refresh();
    }

    private static string Gb(double bytes) => (bytes / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture);
    private static string N0(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    private void Refresh()
    {
        var hw = Svc.Booster.Hardware.Current;
        if (hw == null) { ScanText = "Scanning this PC…"; return; }
        ScanText = $"Scanned {hw.CapturedAt.LocalDateTime:t}";
        if (!ReferenceEquals(hw, _shownHw)) ShowHardware(hw);
        _shownHw = hw;
        _tick++;

        var now = DateTimeOffset.UtcNow;
        var all = Svc.Booster.Telemetry.Snapshot(TimeSpan.FromMinutes(2), now);
        var latest = all.LastOrDefault();
        if (latest != null) UpdateCards(hw, latest, all);
        UpdateGame(hw, latest);

        if (_tick % 3 == 1)
        {
            if (DateTime.UtcNow - _cfgAt > TimeSpan.FromSeconds(20))
            {
                _cfgAt = DateTime.UtcNow;
                var launcher = latest?.Game.Launcher ?? LauncherKind.Unknown;
                _ = Task.Run(() => FrameRateInspector.Inspect(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null, launcher))
                    .ContinueWith(t => { if (t.IsCompletedSuccessfully) _cfg = t.Result; });
            }
            var stats = WindowStats.From(all, now, TimeSpan.FromSeconds(90));
            var fs = BottleneckAnalyzer.Analyze(hw, stats, _cfg);
            Findings.Clear();
            foreach (var f in fs.OrderBy(f => f.Level == EvidenceKind.Limitation ? 2 : f.Level == EvidenceKind.Hypothesis ? 0 : 1)) Findings.Add(new FindingViewModel(f));
            Sources.Clear();
            foreach (var s in hw.Sources.Concat(Svc.Booster.Telemetry.Sources).GroupBy(x => x.Id).Select(g => g.Last()).OrderBy(x => x.Available).ThenBy(x => x.Name)) Sources.Add(new SourceViewModel(s));
        }

        // Banners: things the person needs to know about, most important first.
        if (Svc.GameMode.RecoveryNotice is { Length: > 0 } rn) { Banner = rn; BannerKind = StatusKind.Warning; }
        else if (!Svc.S.Booster.PlanReviewed) { Banner = "Nighty has not changed anything yet. Open the Plan tab to see what suits this PC and approve it."; BannerKind = StatusKind.Info; }
        else if (Svc.Booster.HardwareChangedSinceApproval) { Banner = "Your hardware or drivers changed since you approved a plan. Open the Plan tab to review it again."; BannerKind = StatusKind.Warning; }
        else Banner = "";
    }

    private void ShowHardware(HardwareProfile hw)
    {
        Facts.Clear();
        Facts.Add(new("Computer", $"{hw.SystemModel} · {hw.Platform.ToString().ToLowerInvariant()}"));
        Facts.Add(new("Processor", $"{hw.Cpu.Name} · {hw.Cpu.PhysicalCores?.ToString() ?? "?"} cores / {hw.Cpu.LogicalProcessors} threads" + (hw.Cpu.HybridTopology == true ? " · performance + efficiency cores" : "")));
        foreach (var g in hw.Gpus.Where(g => g.Kind != GpuKind.Software))
            Facts.Add(new("Graphics", $"{g.Name} · {g.Kind.ToString().ToLowerInvariant()}" + (g.DedicatedBytes is long d && d > (1L << 30) ? $" · {Gb(d)} GB" : "") + (g.DriverVersion != null ? $" · driver {g.DriverVersion}" : "")));
        Facts.Add(new("Memory", $"{Gb(hw.Memory.TotalBytes)} GB" + (hw.Memory.SpeedMhz is int sp ? $" at {sp} MHz" : "")));
        foreach (var d in hw.Displays) Facts.Add(new("Display", $"{d.Width}×{d.Height} at {d.RefreshHz} Hz" + (d.Primary ? " (main)" : "") + (d.AdapterIndex is int ai && hw.Gpus.FirstOrDefault(g => g.Index == ai) is { } gg ? $" · listed under {gg.Name}" : "")));
        foreach (var s in hw.Storage) Facts.Add(new("Storage", $"{s.Name} · {s.MediaType} · {s.BusType}"));
        Facts.Add(new("Windows", hw.Os.ToString()));
        Facts.Add(new("Power", $"{(hw.Power.Source == PowerSource.Ac ? "Plugged in" : hw.Power.Source == PowerSource.Battery ? "On battery" : "Unknown source")}"
            + (hw.Power.BatteryPercent is int bp ? $" ({bp}%)" : "") + $" · plan {hw.Power.ActiveSchemeName ?? "unknown"}" + (hw.Power.PowerModeSupported ? $" · mode {hw.Power.Mode}" : "")));
        Facts.Add(new("Permissions", (hw.Permissions.IsAdmin ? "administrator" : "standard user") + (hw.Permissions.CanTraceEtw ? " · can capture real frame timing" : " · cannot capture frame timing yet")));
    }

    private void UpdateCards(HardwareProfile hw, TelemetrySample s, IReadOnlyList<TelemetrySample> all)
    {
        var tail = all.Count > 120 ? all.Skip(all.Count - 120).ToList() : all;
        var gpus = hw.Gpus.Where(g => g.Kind != GpuKind.Software).ToList();
        string Short(GpuInfo g) => g.Name.Replace("NVIDIA GeForce ", "").Replace("Intel(R) ", "").Replace("(R)", "").Replace("(TM)", "").Trim();
        var titles = new[] { "Processor" }.Concat(gpus.Select(Short)).Concat(new[] { "Memory", "Disk", "Power and heat" }).ToList();
        if (!Cards.Select(x => x.Title).SequenceEqual(titles)) { Cards.Clear(); foreach (var t in titles) Cards.Add(new MetricCardViewModel { Title = t }); }

        var cpu = Cards[0];
        cpu.Value = s.Cpu.TotalPercent is double c ? N0(c) + "%" : "n/a";
        var cd = new List<string>();
        if (s.Cpu.BusiestLogicalPercent is double b) cd.Add($"busiest thread {N0(b)}%");
        if (s.Cpu.ActualMhz is int mhz) cd.Add($"{mhz} MHz" + (s.Cpu.PercentOfMaxFrequency is int pm ? $" ({pm}% of max)" : ""));
        if (s.Cpu.PerformanceLimitPercent is int pl && pl < 100) cd.Add($"Windows limit {pl}%");
        cpu.Detail = cd.Count == 0 ? "Not available" : string.Join(" · ", cd);
        cpu.Kind = s.Cpu.PerformanceLimitPercent is < 100 ? StatusKind.Warning : StatusKind.Neutral;
        cpu.Series = tail.Select(x => x.Cpu.TotalPercent ?? 0).ToList();

        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i]; var card = Cards[1 + i];
            var gs = s.Gpus.FirstOrDefault(x => x.Luid == g.Luid);
            card.Value = gs?.EnginePercent is double e ? N0(e) + "%" : "n/a";
            var parts = new List<string> { g.Kind.ToString().ToLowerInvariant() };
            if (gs?.DedicatedUsedBytes is double du && g.Kind == GpuKind.Dedicated) parts.Add($"{Gb(du)} / {Gb(g.DedicatedBytes ?? 0)} GB memory");
            else if (gs?.SharedUsedBytes is double su) parts.Add($"{Gb(su)} GB shared memory");
            if (gs?.TemperatureC is double t) parts.Add(N0(t) + " °C");
            if (gs?.CoreClockMhz is int cl) parts.Add(cl + " MHz");
            if (gs?.PowerWatts is double w) parts.Add(w.ToString("0.0", CultureInfo.InvariantCulture) + " W");
            if (gs?.GameEnginePercent is double ge && s.Game.Running) parts.Add($"Roblox {N0(ge)}%");
            card.Detail = string.Join(" · ", parts);
            card.Kind = gs?.ThermalThrottling == true ? StatusKind.Error : gs?.PowerLimited == true ? StatusKind.Warning : StatusKind.Neutral;
            card.Series = tail.Select(x => x.Gpus.FirstOrDefault(y => y.Luid == g.Luid)?.EnginePercent ?? 0).ToList();
        }

        int baseIdx = 1 + gpus.Count;
        var mem = Cards[baseIdx];
        mem.Value = N0(s.Memory.UsedPercent) + "%";
        mem.Detail = $"{Gb(s.Memory.AvailableBytes)} of {Gb(s.Memory.TotalBytes)} GB free" + (s.Memory.HardFaultsPerSec is double hf ? $" · {N0(hf)} page reads/s" : "");
        mem.Kind = s.Memory.AvailablePercent < 12 ? StatusKind.Warning : StatusKind.Neutral;
        mem.Series = tail.Select(x => x.Memory.UsedPercent).ToList();

        var disk = Cards[baseIdx + 1];
        disk.Value = s.Disk.ActivePercent is double da ? N0(da) + "%" : "n/a";
        disk.Detail = s.Disk.QueueLength is double q ? $"queue {q.ToString("0.0", CultureInfo.InvariantCulture)}" : "Not available";
        disk.Series = tail.Select(x => x.Disk.ActivePercent ?? 0).ToList();

        var pw = Cards[baseIdx + 2];
        pw.Value = hw.Power.Source == PowerSource.Ac ? "Plugged in" : hw.Power.Source == PowerSource.Battery ? "On battery" : "Unknown";
        var pd = new List<string> { hw.Power.ActiveSchemeName ?? "plan unknown" };
        if (hw.Power.PowerModeSupported) pd.Add("mode " + hw.Power.Mode);
        var hot = s.Gpus.Any(g => g.ThermalThrottling == true);
        var anyThermal = s.Gpus.Any(g => g.ThermalThrottling != null);
        pd.Add(hot ? "GPU reports heat slowdown" : anyThermal ? "no GPU heat slowdown reported" : "heat state not available");
        pw.Detail = string.Join(" · ", pd);
        pw.Kind = hot || hw.Power.Source == PowerSource.Battery && hw.IsLaptop ? StatusKind.Warning : StatusKind.Neutral;
        pw.Series = Array.Empty<double>();
    }

    private void UpdateGame(HardwareProfile hw, TelemetrySample? s)
    {
        if (s == null || !s.Game.Running)
        {
            var any = Svc.Booster.Game.Find();
            if (any.IsRunning) { GameTitle = "Roblox is running"; GameDetail = RobloxClientDetector.Display(any.Launcher); GameKind = StatusKind.Success; }
            else { GameTitle = "Roblox is not running"; GameDetail = "Start Roblox and play for a bit to see how it performs on this PC."; GameKind = StatusKind.Neutral; }
            return;
        }
        var g = s.Game;
        var d = new List<string> { RobloxClientDetector.Display(g.Launcher) };
        if (g.CpuPercentOfTotal is double c) d.Add($"CPU {N0(c)}%");
        d.Add($"{Gb(g.WorkingSetBytes)} GB memory");
        if (g.PresentFps is double f) d.Add($"{N0(f)} FPS (counted from Present events)");
        else d.Add("FPS not being captured");
        var disp = hw.Displays.FirstOrDefault(x => string.Equals(x.DeviceName, g.DisplayDevice, StringComparison.OrdinalIgnoreCase));
        if (disp != null) d.Add($"{disp.Width}×{disp.Height} @ {disp.RefreshHz} Hz");
        GameTitle = g.Pids.Count > 1 ? $"Roblox is running ({g.Pids.Count} clients)" : "Roblox is running";
        GameDetail = string.Join(" · ", d);
        GameKind = StatusKind.Success;
    }
}

public sealed class PlanItemViewModel : ObservableObject
{
    private bool _selected, _expanded;
    private readonly Action _changed;

    public PlanItemViewModel(Recommendation r, bool selected, Action changed)
    {
        Rec = r; _selected = selected; _changed = changed;
        Kind = r.Applicability switch { Applicability.Recommended => StatusKind.Success, Applicability.Optional => StatusKind.Info, Applicability.Discouraged => StatusKind.Warning, _ => StatusKind.Neutral };
        Tag = r.Applicability switch { Applicability.Recommended => "RECOMMENDED", Applicability.Optional => "OPTIONAL", Applicability.Discouraged => "NOT ADVISED", _ => "NOT APPLICABLE" };
        Evidence = string.Join("\n", r.Evidence.Select(e => (e.Kind switch { EvidenceKind.Observation => "Measured:  ", EvidenceKind.Hypothesis => "Possible:  ", _ => "Can't see:  " }) + e.Text));
        ChangeText = r.Applicability == Applicability.NotApplicable || string.IsNullOrEmpty(r.ProposedValue) ? "" : $"Now: {(string.IsNullOrEmpty(r.CurrentValue) ? "(Windows default)" : r.CurrentValue)}    →    Proposed: {r.ProposedValue}";
        Details = r.Applicability == Applicability.NotApplicable ? ""
            : $"Expected benefit: {r.ExpectedBenefit}\nTrade-offs: {r.TradeOffs}\nConfidence: {r.Confidence}   ·   Risk: {r.Risk}   ·   Needs: {(r.Permission == PermissionNeed.None ? "no administrator rights" : "administrator approval")}   ·   Undoable: {(r.Reversible ? "yes" : "no")}\nHow it is undone: {r.RestoreMethod}\nHow to check it worked: {r.VerifyMethod}";
        ToggleCommand = new RelayCommand(() => Expanded = !Expanded);
    }

    public Recommendation Rec { get; }
    public StatusKind Kind { get; }
    public string Tag { get; }
    public string Evidence { get; }
    public string ChangeText { get; }
    public string Details { get; }
    public RelayCommand ToggleCommand { get; }
    public bool CanSelect => Rec.Selectable;
    public string Title => Rec.Title;
    public string Reason => Rec.Reason;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }
    public bool Selected
    {
        get => _selected;
        set { if (!CanSelect && value) return; if (Set(ref _selected, value)) _changed(); }
    }
    public void SetSelectedQuietly(bool v) { _selected = v && CanSelect; OnPropertyChanged(nameof(Selected)); }
}

/// <summary>The personalised plan: what Nighty found, what it would change and why, and the approval step.</summary>
public sealed class PlanViewModel : ObservableObject
{
    private OptimizationPlan? _plan;
    private BoosterProfile _profile = BoosterProfile.Balanced;
    private string _summary = "Scanning this PC…", _result = "", _approved = "";
    private StatusKind _resultKind = StatusKind.Info;
    private bool _busy, _suppress;

    public ObservableCollection<PlanItemViewModel> Recommended { get; } = new();
    public ObservableCollection<PlanItemViewModel> Optional { get; } = new();
    public ObservableCollection<PlanItemViewModel> Unadvised { get; } = new();
    public ObservableCollection<PlanItemViewModel> NotApplicable { get; } = new();

    public BoosterProfile Profile
    {
        get => _profile;
        set
        {
            if (!Set(ref _profile, value)) return;
            if (value != BoosterProfile.Custom && _plan != null) ApplyProfileSelection(value);
            OnPropertyChanged(nameof(ProfileHelp));
        }
    }
    public string ProfileHelp => Profile switch
    {
        BoosterProfile.Balanced => "Balanced: only low-risk changes that suit this PC and keep Windows behaving normally.",
        BoosterProfile.Performance => "Performance: everything recommended for this PC, including changes that use more power. Still only things that apply here and can be undone.",
        _ => "Custom: you choose. Items marked not advised are available but their benefit is unproven.",
    };
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Result { get => _result; private set => Set(ref _result, value); }
    public StatusKind ResultKind { get => _resultKind; private set => Set(ref _resultKind, value); }
    public string Approved { get => _approved; private set => Set(ref _approved, value); }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); ApplyCommand?.Refresh(); RefreshCommand?.Refresh(); } }
    public int SelectedCount => AllItems().Count(i => i.Selected);

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ApplyCommand { get; }

    public PlanViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !Busy);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => !Busy && SelectedCount > 0);
        Svc.Booster.Hardware.Updated += (_, _, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
    }

    private IEnumerable<PlanItemViewModel> AllItems() => Recommended.Concat(Optional).Concat(Unadvised).Concat(NotApplicable);

    public async Task RefreshAsync()
    {
        Busy = true;
        try
        {
            await Svc.Booster.EnsureScannedAsync();
            var plan = await Task.Run(() => Svc.Booster.BuildPlan());
            if (plan == null) { Summary = "The PC has not been scanned yet."; return; }
            Show(plan);
        }
        finally { Busy = false; }
    }

    private void Show(OptimizationPlan plan)
    {
        // Keep what the person had ticked when they are in Custom mode; otherwise start from what is already enabled or from the profile.
        var keep = _plan == null ? null : AllItems().Where(i => i.Selected).Select(i => i.Rec.Id).ToHashSet();
        _plan = plan;
        _suppress = true;
        Recommended.Clear(); Optional.Clear(); Unadvised.Clear(); NotApplicable.Clear();

        IReadOnlySet<string> initial;
        var b = Svc.S.Booster;
        if (keep != null && Profile == BoosterProfile.Custom) initial = keep;
        else if (b.PlanReviewed && b.Profile == BoosterProfile.Custom && keep == null)
        {
            _profile = BoosterProfile.Custom; OnPropertyChanged(nameof(Profile)); OnPropertyChanged(nameof(ProfileHelp));
            initial = GameChoices.Enabled(Svc.S.Game).Concat(Svc.S.Backups.TweaksApplied).ToHashSet();
        }
        else
        {
            if (keep == null && b.PlanReviewed) { _profile = b.Profile; OnPropertyChanged(nameof(Profile)); OnPropertyChanged(nameof(ProfileHelp)); }
            initial = Profile == BoosterProfile.Custom ? (keep ?? new HashSet<string>()) : plan.Select(Profile);
        }

        foreach (var r in plan.Items)
        {
            var item = new PlanItemViewModel(r, initial.Contains(r.Id) && r.Selectable, OnItemChanged);
            (r.Applicability switch { Applicability.Recommended => Recommended, Applicability.Optional => Optional, Applicability.Discouraged => Unadvised, _ => NotApplicable }).Add(item);
        }
        _suppress = false;
        Summary = $"{Recommended.Count} recommended · {Optional.Count} optional · {Unadvised.Count} not advised · {NotApplicable.Count} don't apply to this PC";
        Approved = b.PlanReviewed
            ? (b.KeptFromOlderSetup ? "Your earlier Game Mode choices are being kept. Review this plan to see what suits this PC." : $"Approved {b.ApprovedUtc?.ToLocalTime():g} · {b.Profile}")
            : "Nothing has been approved yet, so Nighty has changed nothing.";
        OnPropertyChanged(nameof(SelectedCount)); ApplyCommand.Refresh();
    }

    private void ApplyProfileSelection(BoosterProfile p)
    {
        if (_plan == null) return;
        var ids = _plan.Select(p);
        _suppress = true;
        foreach (var i in AllItems()) i.SetSelectedQuietly(ids.Contains(i.Rec.Id));
        _suppress = false;
        OnPropertyChanged(nameof(SelectedCount)); ApplyCommand.Refresh();
    }

    private void OnItemChanged()
    {
        if (_suppress) return;
        // Choosing by hand turns any preset into Custom, so the label never claims more than what is ticked.
        if (Profile != BoosterProfile.Custom) { _profile = BoosterProfile.Custom; OnPropertyChanged(nameof(Profile)); OnPropertyChanged(nameof(ProfileHelp)); }
        OnPropertyChanged(nameof(SelectedCount)); ApplyCommand.Refresh();
    }

    private async Task ApplyAsync()
    {
        if (_plan == null) return;
        var chosen = AllItems().Where(i => i.Selected).ToList();
        if (chosen.Count == 0) return;
        var lines = chosen.Select(i => $"•  {i.Title}" + (i.Rec.Kind == ActionKind.Session ? "  (only while Game Mode is on)" : "")).ToList();
        string notAdvised = chosen.Any(i => i.Rec.Applicability == Applicability.Discouraged) ? "\n\nSome of these are marked not advised: their benefit is unproven." : "";
        string msg = $"Nighty will use these for {Profile} mode:\n\n{string.Join("\n", lines)}{notAdvised}\n\nEvery change saves your exact current value first and can be undone. Anything you change yourself afterwards is left alone when Nighty puts things back.";
        if (!Dialogs.Confirm("Approve this plan?", msg, "Approve and apply")) return;

        Busy = true;
        try
        {
            var ids = chosen.Select(i => i.Rec.Id).ToList();
            var res = await Task.Run(() => Svc.Booster.Approve(_plan, Profile, ids));
            int tweakOk = res.Tweaks.Count(t => t.Ok), tweakBad = res.Tweaks.Count - tweakOk;
            var text = new List<string> { $"Approved. Game Mode will use {res.SessionEnabled.Count} setting{(res.SessionEnabled.Count == 1 ? "" : "s")} when it runs." };
            if (res.Tweaks.Count > 0) text.Add($"{tweakOk} Windows tweak{(tweakOk == 1 ? "" : "s")} applied" + (tweakBad > 0 ? $", {tweakBad} could not be applied ({string.Join("; ", res.Tweaks.Where(t => !t.Ok).Select(t => t.Id + ": " + t.Text))})" : "") + ".");
            Result = string.Join(" ", text); ResultKind = tweakBad > 0 ? StatusKind.Warning : StatusKind.Success;
            if (Svc.Roblox.IsRunning && Svc.S.Game.AutoGameMode && !Svc.GameMode.IsActive) await Svc.GameMode.SetActiveAsync(true);
            await RefreshAsync();
        }
        finally { Busy = false; }
    }
}

public sealed class RunItemViewModel : ObservableObject
{
    private bool _before, _after;
    private readonly Action _changed;
    public RunItemViewModel(BenchmarkRun r, Action changed) { Run = r; _changed = changed; }
    public BenchmarkRun Run { get; }
    public string Label => Run.Label;
    public string When => Run.StartedUtc.ToLocalTime().ToString("g");
    public string Source => Run.Context.Source switch { FrameSourceKind.PresentEvents => "measured by Nighty", FrameSourceKind.ImportedCsv => "imported CSV", _ => "typed in" };
    public string Numbers
    {
        get
        {
            var parts = new List<string>();
            if (Run.AvgFps is double a) parts.Add($"{a:0} avg FPS");
            if (Run.OnePercentLow is double l) parts.Add($"{l:0} 1% low");
            if (Run.Frames is { } f) parts.Add($"{f.P95Ms:0.0} ms 95th pct");
            return string.Join(" · ", parts);
        }
    }
    public string Conditions
        => $"{(Run.Context.AppliedActions.Count == 0 ? "no optimizations" : Run.Context.AppliedActions.Count + " optimizations")} · {Run.Context.PowerSource} · {Run.Context.DisplayDescription ?? "display unknown"} · {Run.Context.RenderAdapter ?? "adapter unknown"} · scene: {(Run.Context.SceneNote.Length == 0 ? "(no note)" : Run.Context.SceneNote)}"
         + (Run.Warnings.Count > 0 ? $"\n⚠ {string.Join(" ", Run.Warnings)}" : "");
    public bool IsBefore { get => _before; set { if (Set(ref _before, value)) { if (value) { _after = false; OnPropertyChanged(nameof(IsAfter)); } _changed(); } } }
    public bool IsAfter { get => _after; set { if (Set(ref _after, value)) { if (value) { _before = false; OnPropertyChanged(nameof(IsBefore)); } _changed(); } } }
}

public sealed class MetricRowViewModel
{
    public MetricRowViewModel(MetricComparison m)
    {
        Name = m.Name;
        Values = $"{m.BaselineMean:0.0} → {m.AfterMean:0.0} {m.Unit}   ({m.ChangePercent:+0.#;-0.#;0}%)";
        Outcome = m.Outcome switch { "Improved" => "Improved", "Worse" => "Worse", "Within noise" => "No clear change", _ => "Not enough runs" };
        Explanation = m.Explanation;
        Kind = m.Outcome switch { "Improved" => StatusKind.Success, "Worse" => StatusKind.Error, _ => StatusKind.Neutral };
    }
    public string Name { get; }
    public string Values { get; }
    public string Outcome { get; }
    public string Explanation { get; }
    public StatusKind Kind { get; }
}

public sealed class BenchmarkViewModel : ObservableObject
{
    private string _label = "Baseline", _scene = "", _settings = "", _status = "", _stage = "", _headline = "", _csvLabel = "Imported run";
    private string _manualAvg = "", _manualLow = "";
    private int _lengthIndex = 1;
    private RunGroup _group = RunGroup.Baseline;
    private double _progress;
    private bool _running;
    private StatusKind _statusKind = StatusKind.Info, _verdictKind = StatusKind.Neutral;
    private CancellationTokenSource? _cts;
    private readonly List<BenchmarkRun> _runs = new();

    public static readonly int[] Lengths = { 30, 60, 120, 180 };
    public ObservableCollection<RunItemViewModel> Runs { get; } = new();
    public ObservableCollection<MetricRowViewModel> Metrics { get; } = new();
    public ObservableCollection<string> CompareNotes { get; } = new();

    public string Label { get => _label; set => Set(ref _label, value); }
    public string Scene { get => _scene; set => Set(ref _scene, value); }
    public string Settings { get => _settings; set => Set(ref _settings, value); }
    public RunGroup Group { get => _group; set => Set(ref _group, value); }
    public int LengthIndex { get => _lengthIndex; set { if (Set(ref _lengthIndex, Math.Clamp(value, 0, Lengths.Length - 1))) OnPropertyChanged(nameof(LengthText)); } }
    public string LengthText => $"{Lengths[_lengthIndex]} seconds (after a 5 second warm-up)";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string Stage { get => _stage; private set => Set(ref _stage, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Running { get => _running; private set { Set(ref _running, value); StartCommand?.Refresh(); CancelCommand?.Refresh(); } }
    public string ManualAvg { get => _manualAvg; set => Set(ref _manualAvg, value); }
    public string ManualLow { get => _manualLow; set => Set(ref _manualLow, value); }
    public string CsvLabel { get => _csvLabel; set => Set(ref _csvLabel, value); }
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public StatusKind VerdictKind { get => _verdictKind; private set => Set(ref _verdictKind, value); }
    public string CaptureText { get; private set; } = "";
    public bool CaptureAvailable { get; private set; }
    public bool HasRuns => Runs.Count > 0;

    public AsyncCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CompareCommand { get; }
    public RelayCommand AddManualCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public AsyncCommand SetupCommand { get; }

    public BenchmarkViewModel()
    {
        StartCommand = new AsyncCommand(StartAsync, () => !Running);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => Running);
        CompareCommand = new RelayCommand(Compare);
        AddManualCommand = new RelayCommand(AddManual);
        ImportCommand = new RelayCommand(Import);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        SetupCommand = new AsyncCommand(async () =>
        {
            var (ok, msg) = await Elevation.AddToPerformanceLogUsersAsync();
            Status = ok ? "Done. Sign out of Windows and back in, and frame capture will be available." : "Setup did not finish: " + msg;
            StatusKind = ok ? StatusKind.Info : StatusKind.Warning;
        });
        LoadRuns();
        RefreshCapture();
    }

    public void RefreshCapture()
    {
        var a = new PresentFrameSource(Svc.Fps).Availability();
        CaptureAvailable = a.Available; CaptureText = a.Detail;
        OnPropertyChanged(nameof(CaptureAvailable)); OnPropertyChanged(nameof(CaptureText));
    }

    private void LoadRuns()
    {
        _runs.Clear(); _runs.AddRange(Svc.Booster.BenchmarkStore.Load());
        Runs.Clear();
        foreach (var r in _runs.OrderByDescending(r => r.StartedUtc)) Runs.Add(new RunItemViewModel(r, () => { }));
        OnPropertyChanged(nameof(HasRuns));
    }

    private void Persist() { Svc.Booster.BenchmarkStore.Save(_runs); OnPropertyChanged(nameof(HasRuns)); }

    private void AddRun(BenchmarkRun r)
    {
        _runs.Add(r);
        var item = new RunItemViewModel(r, () => { });
        if (r.Group == RunGroup.Baseline) item.IsBefore = true; else if (r.Group == RunGroup.After) item.IsAfter = true;
        Runs.Insert(0, item);
        Persist();
    }

    private async Task StartAsync()
    {
        Status = ""; Headline = "";
        _cts = new CancellationTokenSource();
        Running = true; Progress = 0; Stage = "Starting";
        try
        {
            var req = new BenchmarkRequest(Label.Trim().Length == 0 ? "Run" : Label.Trim(), Group, TimeSpan.FromSeconds(Lengths[LengthIndex]), TimeSpan.FromSeconds(5), Scene, Settings);
            var prog = new Progress<(string Stage, double Fraction)>(p => { Stage = p.Stage; Progress = p.Fraction * 100; });
            var o = await Svc.Booster.Benchmarks.RunAsync(req, prog, _cts.Token);
            if (o.Cancelled) { Status = "Cancelled. Nothing was saved."; StatusKind = StatusKind.Warning; }
            else if (!o.Ok) { Status = o.Error ?? "The test failed."; StatusKind = StatusKind.Error; }
            else
            {
                AddRun(o.Run!);
                var f = o.Run!.Frames!;
                Status = $"Saved. {f.AvgFps:0} FPS average, {f.MedianMs:0.0} ms median frame time, {f.P95Ms:0.0} ms at the 95th percentile" + (f.OnePercentLowFps is double l ? $", {l:0} FPS 1% low" : "") + $" over {f.FrameCount:N0} frames.";
                if (o.Run.Warnings.Count > 0) Status += " Warnings: " + string.Join(" ", o.Run.Warnings);
                StatusKind = o.Run.Warnings.Count > 0 ? StatusKind.Warning : StatusKind.Success;
            }
        }
        finally { Running = false; Stage = ""; Progress = 0; RefreshCapture(); }
    }

    private void AddManual()
    {
        if (!double.TryParse(ManualAvg, NumberStyles.Float, CultureInfo.CurrentCulture, out double avg) || avg <= 0) { Status = "Type the average FPS you measured with another tool."; StatusKind = StatusKind.Warning; return; }
        double? low = double.TryParse(ManualLow, NumberStyles.Float, CultureInfo.CurrentCulture, out double l) && l > 0 ? l : null;
        var run = Svc.Booster.Benchmarks.FromManual(Label.Trim().Length == 0 ? "Typed-in run" : Label.Trim(), Group, avg, low, Lengths[LengthIndex], Scene, Settings, "");
        AddRun(run);
        Status = "Added. Typed-in figures are marked as unverified in comparisons."; StatusKind = StatusKind.Info;
        ManualAvg = ""; ManualLow = "";
    }

    private void Import()
    {
        var dlg = new OpenFileDialog { Title = "Import a PresentMon / CapFrameX CSV", Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > 200 * 1024 * 1024) { Status = "That file is too large (over 200 MB)."; StatusKind = StatusKind.Error; return; }
            var o = Svc.Booster.Benchmarks.FromCsv(CsvLabel.Trim().Length == 0 ? Path.GetFileNameWithoutExtension(dlg.FileName) : CsvLabel.Trim(), Group, File.ReadAllText(dlg.FileName), Scene, Settings);
            if (!o.Ok) { Status = o.Error ?? "Could not read the file."; StatusKind = StatusKind.Error; return; }
            AddRun(o.Run!);
            Status = $"Imported {o.Run!.Frames!.FrameCount:N0} frames."; StatusKind = StatusKind.Success;
        }
        catch (Exception ex) { Status = "Could not read the file: " + ex.Message; StatusKind = StatusKind.Error; }
    }

    private void DeleteSelected()
    {
        var gone = Runs.Where(r => r.IsBefore || r.IsAfter).ToList();
        if (gone.Count == 0) { Status = "Tick the runs you want to delete (Before or After), then press Delete."; StatusKind = StatusKind.Info; return; }
        if (!Dialogs.Confirm("Delete runs?", $"Delete {gone.Count} saved run(s)? This cannot be undone.", "Delete", danger: true)) return;
        foreach (var g in gone) { _runs.Remove(g.Run); Runs.Remove(g); }
        Persist(); Metrics.Clear(); CompareNotes.Clear(); Headline = "";
    }

    private void Compare()
    {
        var b = Runs.Where(r => r.IsBefore).Select(r => r.Run).ToList();
        var a = Runs.Where(r => r.IsAfter).Select(r => r.Run).ToList();
        var res = BenchmarkComparer.Compare(b, a);
        Headline = res.Headline;
        VerdictKind = res.Verdict switch { Verdict.Improved => StatusKind.Success, Verdict.Regressed => StatusKind.Error, Verdict.Mixed => StatusKind.Warning, Verdict.Incomparable => StatusKind.Warning, _ => StatusKind.Info };
        Metrics.Clear(); foreach (var m in res.Metrics) Metrics.Add(new MetricRowViewModel(m));
        CompareNotes.Clear();
        foreach (var r in res.IncomparableReasons) CompareNotes.Add("✖ " + r);
        foreach (var w in res.Warnings.Distinct()) CompareNotes.Add("⚠ " + w);
        if (res.Verdict is Verdict.Improved or Verdict.Mixed or Verdict.Regressed) CompareNotes.Add("ℹ A difference here shows the two groups differ; it does not by itself prove what caused it.");
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed class GameOptionViewModel : ObservableObject
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private string _status = "";
    private StatusKind _kind;

    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public Action<GameOptionViewModel>? Changed { get; set; }

    public GameOptionViewModel(Func<bool> get, Action<bool> set) { _get = get; _set = set; }

    public bool Enabled
    {
        get => _get();
        set { if (_get() == value) return; _set(value); OnPropertyChanged(); Changed?.Invoke(this); }
    }
    public void Refresh() => OnPropertyChanged(nameof(Enabled));
    public string Status { get => _status; set => Set(ref _status, value); }
    public StatusKind Kind { get => _kind; set => Set(ref _kind, value); }
}

public sealed class CleanerItemViewModel : ObservableObject
{
    private bool _selected;
    private string _size = "Not scanned", _detail = "";
    public required CleanerCategory Category { get; init; }
    public bool Selected { get => _selected; set { if (Set(ref _selected, value)) Changed?.Invoke(); } }
    public string SizeText { get => _size; set => Set(ref _size, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public Action? Changed { get; set; }
    public bool AdminWarning => Category.NeedsAdmin && !Elevation.IsAdmin;
}

public sealed class GamingViewModel : ObservableObject
{
    private int _tab;
    private bool _busy;
    private double _cpu, _ram;
    private string _cpuText = "—", _ramText = "—", _robloxText = "Roblox is not running", _gameStatus = "";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public int Tab { get => _tab; set => Set(ref _tab, value); }

    // ---------- Game Mode ----------
    public bool IsActive => Svc.GameMode.IsActive;
    public GameSettings Game => Svc.S.Game;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); PowerCommand.Refresh(); } }
    public string PowerLabel => IsBusy ? "Working…" : IsActive ? "Game Mode is ON" : "Game Mode is OFF";
    public string PowerHint => IsActive ? "Click to restore your original settings" : "Click to apply the selected optimizations";
    public double Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public double Ram { get => _ram; private set => Set(ref _ram, value); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string RamText { get => _ramText; private set => Set(ref _ramText, value); }
    public string RobloxText { get => _robloxText; private set => Set(ref _robloxText, value); }
    public string GameStatus { get => _gameStatus; private set => Set(ref _gameStatus, value); }
    public ObservableCollection<GameOptionViewModel> Options { get; } = new();
    public AsyncCommand PowerCommand { get; }

    // ---------- Tweaks ----------
    public ObservableCollection<GameOptionViewModel> TweakOptions { get; } = new();
    public RelayCommand RestoreTweaksCommand { get; }
    public string TweaksMessage { get => _tweaksMessage; private set => Set(ref _tweaksMessage, value); }
    private string _tweaksMessage = "";

    // ---------- Cleaner ----------
    public CleanerViewModel Cleaner { get; } = new();

    public GamingViewModel()
    {
        var g = Svc.S.Game;
        void Add(string key, string title, string desc, Func<bool> get, Action<bool> set) =>
            Options.Add(new GameOptionViewModel(get, set) { Key = key, Title = title, Description = desc, Changed = OnOptionChanged });

        Add("power", "High performance power plan",
            "Switches Windows to the High performance plan while Game Mode is on, then returns to your previous plan. Not available on every PC.",
            () => g.HighPerformancePower, v => g.HighPerformancePower = v);
        Add("priority", "Roblox High CPU priority",
            "Raises the priority of the running Roblox client so it is scheduled ahead of background work. Reset when Game Mode ends.",
            () => g.RobloxHighPriority, v => g.RobloxHighPriority = v);
        Add("timer", "1 ms timer resolution",
            "Requests a finer system timer, which can make frame pacing and input timing steadier. Released when Game Mode ends.",
            () => g.TimerResolution, v => g.TimerResolution = v);
        Add("gamemode", "Windows Game Mode",
            "Makes sure Windows' own Game Mode setting is on. Your original value is restored afterwards.",
            () => g.WindowsGameMode, v => g.WindowsGameMode = v);
        Add("awake", "Keep display awake",
            "Prevents the screen from dimming or sleeping during long sessions while Game Mode is on.",
            () => g.KeepDisplayAwake, v => g.KeepDisplayAwake = v);

        foreach (var t in Svc.Tweaks.All) TweakOptions.Add(MakeTweak(t));
        RestoreTweaksCommand = new RelayCommand(() =>
        {
            Svc.Tweaks.RevertAll();
            foreach (var o in TweakOptions) { o.Refresh(); o.Status = ""; o.Kind = StatusKind.Neutral; }
            TweaksMessage = "All tweaks are switched off and your original Windows settings are back.";
        });

        PowerCommand = new AsyncCommand(TogglePower, () => !IsBusy);
        Svc.GameMode.Changed += () => Application.Current.Dispatcher.BeginInvoke(RefreshGameMode);

        _timer.Tick += (_, _) => Sample();
        _timer.Start();
        Sample();
        RefreshGameMode();
    }

    private GameOptionViewModel MakeTweak(TweakInfo t)
    {
        OptionResult? last = null;
        var vm = new GameOptionViewModel(() => Svc.Tweaks.IsApplied(t.Id), on => last = Svc.Tweaks.Set(t.Id, on))
        {
            Key = t.Id, Title = t.Title, Description = t.Description,
        };
        vm.Changed = o =>
        {
            o.Status = last?.Text ?? "";
            o.Kind = last?.Kind ?? StatusKind.Neutral;
            o.Refresh();   // a failed tweak must show as off again
        };
        return vm;
    }

    private async Task TogglePower()
    {
        IsBusy = true; OnPropertyChanged(nameof(PowerLabel));
        try { await Svc.GameMode.SetActiveAsync(!Svc.GameMode.IsActive); }
        finally { IsBusy = false; RefreshGameMode(); }
    }

    private async void OnOptionChanged(GameOptionViewModel _)
    {
        if (!Svc.GameMode.IsActive || IsBusy) return;
        // Re-apply so the new selection takes effect immediately.
        IsBusy = true; OnPropertyChanged(nameof(PowerLabel));
        try { await Svc.GameMode.SetActiveAsync(false); await Svc.GameMode.SetActiveAsync(true); }
        finally { IsBusy = false; RefreshGameMode(); }
    }

    private void RefreshGameMode()
    {
        OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(PowerLabel)); OnPropertyChanged(nameof(PowerHint));
        foreach (var o in Options)
        {
            if (Svc.GameMode.IsActive && o.Enabled && Svc.GameMode.Results.TryGetValue(o.Key, out var r)) { o.Status = r.Text; o.Kind = r.Kind; }
            else { o.Status = ""; o.Kind = StatusKind.Neutral; }
        }
        GameStatus = Svc.GameMode.IsActive ? "Optimizations are applied. Turn Game Mode off to restore your original settings." : "";
    }

    private void Sample()
    {
        var u = Svc.Usage.Sample();
        Cpu = u.Cpu; Ram = u.RamPercent;
        CpuText = $"{u.Cpu:0}%";
        RamText = $"{u.RamUsedGb:0.0} / {u.RamTotalGb:0.0} GB";
        var rb = Svc.Usage.RobloxMemoryMb();
        RobloxText = rb is double mb ? $"Roblox is running · {mb:0} MB" : "Roblox is not running";
    }
}

public sealed class CleanerViewModel : ObservableObject
{
    private bool _busy, _hasScan;
    private double _progress;
    private string _status = "Choose what to clean, then scan to preview exactly which files would be removed.";
    private StatusKind _statusKind = StatusKind.Info;
    private string _totalText = "";
    private CancellationTokenSource? _cts;
    private Dictionary<string, CategoryScan> _scans = new();

    public ObservableCollection<CleanerItemViewModel> Items { get; } = new();
    public ObservableCollection<string> Preview { get; } = new();

    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); RefreshCommands(); } }
    public bool HasScan { get => _hasScan; private set { Set(ref _hasScan, value); RefreshCommands(); } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }

    public AsyncCommand ScanCommand { get; }
    public AsyncCommand CleanCommand { get; }
    public RelayCommand CancelCommand { get; }

    public CleanerViewModel()
    {
        var chosen = Svc.S.Game.CleanerCategories;
        foreach (var c in Svc.Cleaner.Categories)
        {
            Items.Add(new CleanerItemViewModel { Category = c, Selected = chosen.Contains(c.Id) });
        }
        foreach (var i in Items)
        {
            i.Changed = () =>
            {
                chosen.Clear();
                foreach (var it in Items.Where(x => x.Selected)) chosen.Add(it.Category.Id);
                Svc.Settings.MarkDirty();
                RefreshCommands();
            };
        }
        ScanCommand = new AsyncCommand(Scan, () => !IsBusy && Items.Any(i => i.Selected));
        CleanCommand = new AsyncCommand(Clean, () => !IsBusy && HasScan && _scans.Values.Any(s => s.Count > 0));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
    }

    private void RefreshCommands() { ScanCommand?.Refresh(); CleanCommand?.Refresh(); CancelCommand?.Refresh(); }

    private async Task Scan()
    {
        _cts = new CancellationTokenSource();
        IsBusy = true; HasScan = false; Progress = 0; Preview.Clear();
        StatusKind = StatusKind.Info;
        try
        {
            foreach (var i in Items) { i.SizeText = i.Selected ? "Scanning…" : "Not scanned"; i.Detail = ""; }
            var prog = new Progress<string>(s => Status = s);
            _scans = await Svc.Cleaner.ScanAsync(Items.Where(i => i.Selected).Select(i => i.Category.Id), prog, _cts.Token);

            foreach (var i in Items)
            {
                if (!_scans.TryGetValue(i.Category.Id, out var s)) continue;
                i.SizeText = Format(s.Bytes);
                i.Detail = s.Note ?? $"{s.Count:N0} file{(s.Count == 1 ? "" : "s")}" + (s.SkippedInaccessible > 0 ? $" · {s.SkippedInaccessible} inaccessible" : "");
            }
            var all = _scans.Values.SelectMany(s => s.Files).OrderByDescending(f => f.Size).Take(300);
            foreach (var f in all) Preview.Add($"{Format(f.Size),10}   {f.Path}");
            long bytes = _scans.Values.Sum(s => s.Bytes);
            int count = _scans.Values.Sum(s => s.Count);
            TotalText = $"{count:N0} item(s) · {Format(bytes)}";
            HasScan = true;
            Status = count == 0 ? "Nothing to clean in the selected categories." : $"Scan complete: {TotalText} can be removed. Review the preview, then click Clean.";
            StatusKind = count == 0 ? StatusKind.Success : StatusKind.Info;
        }
        catch (OperationCanceledException) { Status = "Scan cancelled."; StatusKind = StatusKind.Warning; }
        finally { IsBusy = false; }
    }

    private async Task Clean()
    {
        int count = _scans.Values.Sum(s => s.Count);
        long bytes = _scans.Values.Sum(s => s.Bytes);
        if (!Dialogs.Confirm("Clean selected files?", $"This will permanently delete {count:N0} item(s) ({Format(bytes)}) shown in the preview. Files that are in use will be skipped.", "Delete files", danger: true)) return;

        _cts = new CancellationTokenSource();
        IsBusy = true; Progress = 0; StatusKind = StatusKind.Info; Status = "Cleaning…";
        try
        {
            var prog = new Progress<(int Done, int Total)>(p => Progress = p.Total == 0 ? 100 : p.Done * 100.0 / p.Total);
            var r = await Svc.Cleaner.CleanAsync(_scans, prog, _cts.Token);
            Status = (r.Cancelled ? "Cancelled. " : "") + $"Removed {r.Deleted:N0} item(s), freed {Format(r.BytesFreed)}. Skipped {r.Skipped:N0} (in use or no permission).";
            StatusKind = r.Cancelled || r.Skipped > 0 ? StatusKind.Warning : StatusKind.Success;
            _scans.Clear(); HasScan = false; Preview.Clear(); TotalText = "";
            foreach (var i in Items) { i.SizeText = "Not scanned"; i.Detail = ""; }
        }
        finally { IsBusy = false; Progress = 0; }
    }

    public static string Format(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {u[i]}" : $"{v:0.#} {u[i]}";
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed class NavItem
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required ObservableObject Page { get; init; }
}

public sealed record SearchHit(string Label, NavItem Nav)
{
    public string PageTitle => Nav.Title;
}

public sealed class MainViewModel : ObservableObject
{
    private NavItem _selected;
    private string _robloxText = "", _clickerText = "", _gameText = "";
    private StatusKind _robloxKind, _clickerKind, _gameKind;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _searchText = "";
    private List<SearchHit> _index = new();
    private int _robloxTick;
    private bool _robloxRunning;

    public ObservableCollection<NavItem> Items { get; }
    public CombatViewModel Combat { get; } = new();
    public GamingViewModel Gaming { get; } = new();
    public UtilityViewModel Utility { get; } = new();
    public ModsViewModel Mods { get; } = new();
    public ExtrasViewModel Extras { get; } = new();
    public MacrosViewModel Macros { get; } = new();
    public OverlaysViewModel Overlays { get; } = new();
    public SettingsViewModel Settings { get; } = new();

    public GeneralSettings General => Svc.S.General;

    public NavItem Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(Current)); } }
    public ObservableObject Current => _selected.Page;

    public string RobloxText { get => _robloxText; private set => Set(ref _robloxText, value); }
    public StatusKind RobloxKind { get => _robloxKind; private set => Set(ref _robloxKind, value); }
    public string ClickerText { get => _clickerText; private set => Set(ref _clickerText, value); }
    public StatusKind ClickerKind { get => _clickerKind; private set => Set(ref _clickerKind, value); }
    public string GameText { get => _gameText; private set => Set(ref _gameText, value); }
    public StatusKind GameKind { get => _gameKind; private set => Set(ref _gameKind, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            RunSearch();
            OnPropertyChanged(nameof(IsSearching));
        }
    }
    public bool IsSearching => !string.IsNullOrWhiteSpace(_searchText);
    public ObservableCollection<SearchHit> SearchResults { get; } = new();
    public bool NoResults => IsSearching && SearchResults.Count == 0;
    public ICommand OpenHitCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public string AdminText => Elevation.IsAdmin ? "Administrator" : "Standard user";


    public MainViewModel(string? startPage = null)
    {
        Items = new ObservableCollection<NavItem>
        {
            new() { Title = "Combat", Glyph = "", Page = Combat },
            new() { Title = "Gaming", Glyph = "", Page = Gaming },
            new() { Title = "Utility", Glyph = "", Page = Utility },
            new() { Title = "Mods", Glyph = "", Page = Mods },
            new() { Title = "Extras", Glyph = "\uE734", Page = Extras },
            new() { Title = "Macros", Glyph = "", Page = Macros },
            new() { Title = "Overlays", Glyph = "", Page = Overlays },
            new() { Title = "Settings", Glyph = "", Page = Settings },
        };
        _selected = Items.FirstOrDefault(i => i.Title.Equals(startPage, StringComparison.OrdinalIgnoreCase)) ?? Items[0];

        BuildSearchIndex();
        OpenHitCommand = new RelayCommand(o =>
        {
            if (o is not SearchHit hit) return;
            Selected = hit.Nav;
            SearchText = "";
        });
        ClearSearchCommand = new RelayCommand(() => SearchText = "");

        // Emergency stop: stops the clicker and every running macro.
        Svc.Hotkeys.Register("stop-all", () => (Svc.S.General.StopHotkeyVk, Svc.S.General.StopHotkeyMods), down =>
        {
            if (down) { Svc.StopAllInput(); Log.Info("Emergency stop hotkey pressed"); }
        });

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void BuildSearchIndex()
    {
        NavItem Nav(string title) => Items.First(i => i.Title == title);
        void Add(string page, params string[] labels)
        {
            var nav = Nav(page);
            _index.Add(new SearchHit(page, nav));
            foreach (var l in labels) _index.Add(new SearchHit(l, nav));
        }
        Add("Combat", "Activation", "Click speed", "Duty cycle", "Hotkey", "Mouse button", "Only while Roblox is focused", "Presets", "Clicker");
        Add("Gaming", "Game Mode", "Tweaks", "PC tweaks", "FPS", "Cleaner", "Optimizations", "System usage", "What to clean");
        Add("Utility", "Brightness", "Movement Helper", "Tracking Helper", "Best DNS", "Apply the fastest automatically", "QoS Policy", "Disable Sticky Keys shortcut",
            "Disable Filter Keys shortcut", "Disable Toggle Keys shortcut", "Enhance pointer precision", "Fast key repeat", "Pointer speed", "Sensitivity calculator", "Network adapter", "Roblox traffic policy");
        Add("Mods", "Your mods");
        Add("Extras", "Bow slot", "Shoot automatically", "Only while fighting", "Switch back afterwards", "Trigger", "Try it");
        Add("Macros", "Your macros", "Steps", "Repeat", "Hotkey enabled");
        Add("Overlays", "Ping host", "Preview and position", "Crosshair", "Add crosshair");
        Add("Settings", "Emergency stop hotkey", "Keep window on top", "Start with Windows", "Permissions", "Logs", "App data folder", "Restore system changes", "Reset everything", "Safety");
    }

    private void RunSearch()
    {
        SearchResults.Clear();
        var q = _searchText.Trim();
        if (q.Length > 0)
            foreach (var h in _index.Where(h => h.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(h => h.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
                SearchResults.Add(h);
        OnPropertyChanged(nameof(NoResults));
    }

    private void Refresh()
    {
        if (_robloxTick++ % 4 == 0) _robloxRunning = Svc.Roblox.IsRunning;   // process scan every 2 s
        RobloxText = _robloxRunning ? "Running" : "Not running";
        RobloxKind = _robloxRunning ? StatusKind.Success : StatusKind.Neutral;

        var s = Svc.S.Clicker;
        if (Svc.Clicker.IsClicking) { ClickerText = $"Clicking · {Svc.Clicker.MeasuredCps:0} CPS"; ClickerKind = StatusKind.Success; }
        else if (s.Enabled) { ClickerText = "Armed"; ClickerKind = StatusKind.Info; }
        else { ClickerText = "Off"; ClickerKind = StatusKind.Neutral; }

        GameText = Svc.GameMode.IsActive ? "Active" : "Off";
        GameKind = Svc.GameMode.IsActive ? StatusKind.Success : StatusKind.Neutral;
    }
}

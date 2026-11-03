using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed class NavItem : ObservableObject
{
    private bool _current;
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required ObservableObject Page { get; init; }
    /// <summary>The top-bar heading this page lives under.</summary>
    public string Group { get; init; } = "";
}

/// <summary>One heading in the top bar. A group with several pages shows them as a second row of pills.</summary>
public sealed class NavGroup : ObservableObject
{
    private bool _selected;
    public required string Title { get; init; }
    public required IReadOnlyList<NavItem> Items { get; init; }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
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
    private bool _robloxRunning, _autoStarted;

    public ObservableCollection<NavItem> Items { get; }
    /// <summary>The headings across the top bar.</summary>
    public ObservableCollection<NavGroup> Groups { get; } = new();
    /// <summary>The pages of the selected heading (shown as a second row only when there is more than one).</summary>
    public IReadOnlyList<NavItem> SubItems => Groups.FirstOrDefault(g => g.IsSelected)?.Items ?? Array.Empty<NavItem>();
    public bool HasSubItems => SubItems.Count > 1;
    public ICommand SelectGroupCommand { get; }
    public ICommand SelectItemCommand { get; }
    private readonly Dictionary<string, NavItem> _lastInGroup = new();

    private void BuildGroups()
    {
        Groups.Clear();
        foreach (var g in Items.GroupBy(i => i.Group).Where(g => g.Key.Length > 0))
            Groups.Add(new NavGroup { Title = g.Key, Items = g.ToList() });
        SyncGroups();
    }

    private void SyncGroups()
    {
        foreach (var g in Groups) g.IsSelected = g.Items.Contains(_selected);
        foreach (var i in Items) i.IsCurrent = ReferenceEquals(i, _selected);
        _lastInGroup[_selected.Group] = _selected;
        OnPropertyChanged(nameof(SubItems)); OnPropertyChanged(nameof(HasSubItems));
    }
    public CombatViewModel Combat { get; } = new();
    public MsViewModel Ms { get; } = new();
    public GamingViewModel Gaming { get; } = new();
    public ModsViewModel Mods { get; } = new();
    public ExtrasViewModel Extras { get; } = new();
    public MacrosViewModel Macros { get; } = new();
    public OverlaysViewModel Overlays { get; } = new();
    public RecordViewModel Record { get; } = new();
    public MusicViewModel Music { get; } = new();
    public EditorViewModel Editor { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    private OwnerViewModel? _owner;

    public GeneralSettings General => Svc.S.General;

    public NavItem Selected { get => _selected; set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(Current)); SyncGroups(); } } }
    public ObservableObject Current => _selected.Page;

    public string RobloxText { get => _robloxText; private set => Set(ref _robloxText, value); }
    public StatusKind RobloxKind { get => _robloxKind; private set => Set(ref _robloxKind, value); }
    public string ClickerText { get => _clickerText; private set => Set(ref _clickerText, value); }
    public StatusKind ClickerKind { get => _clickerKind; private set => Set(ref _clickerKind, value); }
    public string GameText { get => _gameText; private set => Set(ref _gameText, value); }
    public StatusKind GameKind { get => _gameKind; private set => Set(ref _gameKind, value); }
    public string VersionBadge => "V" + (AppVersion.Text(typeof(MainViewModel).Assembly.GetName().Version) is { Length: > 0 } v ? v : "1.0.0");
    public string VersionText => "Version " + (AppVersion.Text(typeof(MainViewModel).Assembly.GetName().Version) is { Length: > 0 } v ? v : "1.0.0");
    private string _statusTitle = "Idle", _statusDetail = "";
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public ICommand OpenGitHubCommand { get; } = new RelayCommand(() =>
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/Garfieldking45/Nighty") { UseShellExecute = true }); } catch { }
    });
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


    /// <summary>The Owner page is in the sidebar only while the owner is signed in.</summary>
    private void SyncOwnerPage()
    {
        var existing = Items.FirstOrDefault(i => i.Page is OwnerViewModel);
        if (OwnerService.IsOwner && existing == null)
        {
            _owner ??= new OwnerViewModel();
            _owner.Refresh();
            Items.Add(new NavItem { Group = "Owner", Title = "Owner", Glyph = "\uE72E", Page = _owner });
        }
        else if (!OwnerService.IsOwner && existing != null)
        {
            if (ReferenceEquals(_selected, existing)) Selected = Items[0];
            Items.Remove(existing);
            BuildGroups();
            BuildGroups();
        }
    }

    public MainViewModel(string? startPage = null)
    {
        Items = new ObservableCollection<NavItem>
        {
            new() { Group = "Combat", Title = "Combat", Glyph = "", Page = Combat },
            new() { Group = "Gaming", Title = "Gaming", Glyph = "", Page = Gaming },
            new() { Group = "Tools", Title = "Mods", Glyph = "", Page = Mods },
            new() { Group = "Tools", Title = "Extras", Glyph = "\uE734", Page = Extras },
            new() { Group = "Tools", Title = "Ms's", Glyph = "", Page = Ms },
            new() { Group = "Tools", Title = "Macros", Glyph = "", Page = Macros },
            new() { Group = "Media", Title = "Overlays", Glyph = "", Page = Overlays },
            new() { Group = "Media", Title = "Record", Glyph = "", Page = Record },
            new() { Group = "Media", Title = "Editor", Glyph = "", Page = Editor },
            new() { Group = "Media", Title = "Music", Glyph = "", Page = Music },
            new() { Group = "Settings", Title = "Settings", Glyph = "", Page = Settings },
        };
        foreach (var n in Items) Controls.PageHeading.NavGlyphs[n.Title] = n.Glyph;
        SelectGroupCommand = new RelayCommand(o => { if (o is NavGroup g) Selected = _lastInGroup.TryGetValue(g.Title, out var last) && g.Items.Contains(last) ? last : g.Items[0]; });
        SelectItemCommand = new RelayCommand(o => { if (o is NavItem n) Selected = n; });
        // TEMPORARY hit-test page: present only when started with --testtab (or NIGHTY_TESTTAB=1). Not in search or the changelog.
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--testtab", StringComparison.OrdinalIgnoreCase)) || Environment.GetEnvironmentVariable("NIGHTY_TESTTAB") == "1")
            Items.Add(new NavItem { Group = "Tools", Title = "Test", Glyph = "", Page = new TestViewModel() });
        _selected = Items.FirstOrDefault(i => i.Title.Equals(startPage, StringComparison.OrdinalIgnoreCase)) ?? Items[0];
        SyncOwnerPage();
        BuildGroups();
        OwnerService.Changed += () => Svc.Dispatch(SyncOwnerPage);

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
            if (down) { Svc.StopAllInput(); Svc.Toast.Show("Emergency stop", "all input stopped", false); Log.Info("Emergency stop hotkey pressed"); }
        });

        // Slot-1 slowdown follows the clicker and the hotbar slot straight away (off the clicker thread so it never delays a click).
        Svc.Clicker.StateChanged += () => { if (Svc.S.Utility.Tracking.SlowInSlotOne) ThreadPool.QueueUserWorkItem(_ => Svc.Pointer.UpdateSlowdown()); };
        Svc.Bow.SlotChanged += () => { if (Svc.S.Utility.Tracking.SlowInSlotOne) ThreadPool.QueueUserWorkItem(_ => Svc.Pointer.UpdateSlowdown()); };
        Svc.S.Utility.Tracking.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrackingSettings.SlowInSlotOne)) Svc.Toast.Toggled("Slot 1 slowdown", Svc.S.Utility.Tracking.SlowInSlotOne);
            if (e.PropertyName is nameof(TrackingSettings.SlowInSlotOne) or nameof(TrackingSettings.SlowSpeed)) Svc.Pointer.UpdateSlowdown();
        };
        Svc.Pointer.StartForegroundWatch();   // Tracking Helper switches the instant Roblox gains or loses focus
        _timer.Tick += (_, _) =>
        {
            Refresh();
            if (_robloxTick % 8 == 1 && Svc.RobloxAssets.ReapplyIfNeeded() is { } msg) Svc.Toast.Show("Roblox", msg, true);
        };
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
        Add("Combat", "Aim assist", "Tracking Helper", "Pointer", "Slow down in slot 1", "Activation", "Click speed", "Duty cycle", "Hotkey", "Mouse button", "Only while Roblox is focused", "Presets", "Clicker");
        Add("Gaming", "FPS boost", "Boost FPS", "Game Mode", "FPS", "Frame rate", "FPS cap", "Graphics card", "Power", "Priority", "Throttling", "Game DVR");
        Add("Mods", "Cursors", "Roblox cursor", "Cursor Builder", "Picture to cursor", "Fonts", "Roblox font", "Your mods", "Remove background");
        Add("Extras", "Auto Crossbow", "Hotbar macros", "Auto Whim", "Auto Lasso", "Auto Build Up", "Auto Melody", "Auto fish");
        Add("Ms's", "Milliseconds", "Click timing", "Hold time", "Shot hold", "Equip delay", "Swap delay", "Cooldown");
        Add("Macros", "Your macros", "Steps", "Repeat", "Hotkey enabled");
        Add("Overlays", "Ping host", "Preview and position", "Crosshair", "Add crosshair");
        Add("Editor", "Video editor", "Trim", "Split", "Titles", "Export video");
        Add("Music", "Play music", "YouTube", "Library", "Music engine", "Shuffle", "Volume");
        Add("Record", "Instant Replay", "Save clip", "Start recording", "Screen recording", "Clips and recordings", "Record hotkeys", "Clip length", "Video quality");
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

    private bool _trackingWas = Svc.S.Utility.Tracking.ApplyOnlyInRoblox;

    private void Refresh()
    {
        Svc.Pointer.AutoTick();
        var tr = Svc.S.Utility.Tracking.ApplyOnlyInRoblox;
        if (tr != _trackingWas) { _trackingWas = tr; Svc.Toast.Toggled("Pointer settings (Roblox only)", tr); if (!tr) Svc.Pointer.AutoRelease(); }
        if (_robloxTick++ % 4 == 0)
        {
            bool was = _robloxRunning;
            _robloxRunning = Svc.Roblox.IsRunning;   // process scan every 2 s
            // FPS boost: the Game Mode actions follow Roblox starting / closing (only transitions).
            if (Svc.Boost.IsOn && _robloxRunning != was && (_robloxRunning || _autoStarted))
            {
                _autoStarted = _robloxRunning;
                bool on = _robloxRunning;
                _ = Task.Run(async () =>
                {
                    await Svc.Boost.RobloxChangedAsync(on);
                    Svc.Dispatch(() => Svc.Toast.Toggled("FPS boost", on));
                });
            }
            if (Svc.Boost.IsOn) _ = Task.Run(Svc.Boost.Tick);
        }
        RobloxText = _robloxRunning ? "Running" : "Not running";
        RobloxKind = _robloxRunning ? StatusKind.Success : StatusKind.Neutral;

        var s = Svc.S.Clicker;
        if (Svc.Clicker.IsClicking) { ClickerText = $"Clicking · {Svc.Clicker.MeasuredCps:0} CPS"; ClickerKind = StatusKind.Success; }
        else if (s.Enabled) { ClickerText = "Armed"; ClickerKind = StatusKind.Info; }
        else { ClickerText = "Off"; ClickerKind = StatusKind.Neutral; }

        StatusTitle = Svc.Clicker.IsClicking ? "Clicking" : s.Enabled ? "Armed" : "Idle";
        StatusDetail = $"{(s.UseRange ? $"{s.MinCps:0.##}-{s.MaxCps:0.##}" : $"{s.Cps:0.00}")} CPS · {(s.HotkeyVk > 0 ? Hotkeys.Format(s.HotkeyVk, s.HotkeyMods) : "no key")}";

        GameText = Svc.GameMode.IsActive ? "Boosting" : Svc.Boost.IsOn ? "Ready" : "Off";
        GameKind = Svc.GameMode.IsActive ? StatusKind.Success : Svc.Boost.IsOn ? StatusKind.Info : StatusKind.Neutral;
    }
}

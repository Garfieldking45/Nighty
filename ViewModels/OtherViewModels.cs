using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

// ============================================================ Mods

public sealed class ModRowViewModel : ObservableObject
{
    public required ModEntry Entry { get; init; }
    public int FileCount { get; init; }
    public int Rejected { get; init; }
    public bool Exists { get; init; }
    public string Detail => !Exists ? "Folder not found — remove or re-add this mod"
        : $"{FileCount} file(s) will be applied" + (Rejected > 0 ? $" · {Rejected} unsupported file(s) ignored" : "");
}

public sealed class ModsViewModel : ObservableObject
{
    private string? _version;
    private string _message = "";
    private StatusKind _kind = StatusKind.Info;
    private bool _busy;

    private int _tab;
    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public RobloxLocationViewModel Location { get; } = new();
    public CursorsViewModel Cursors { get; }
    public BuilderViewModel Builder { get; } = new();
    public ImageCursorViewModel Picture { get; } = new();
    public FontsViewModel Fonts { get; }

    public ObservableCollection<ModRowViewModel> Rows { get; } = new();
    public string? Version { get => _version; private set { Set(ref _version, value); OnPropertyChanged(nameof(InstallText)); OnPropertyChanged(nameof(Installed)); } }
    public bool Installed => Version != null;
    public string InstallText => Version == null ? "Roblox was not found in the per-user install location." : $"Roblox install: {System.IO.Path.GetFileName(Version)}";
    public bool IsApplied => Version != null && Svc.Mods.IsApplied(Version);
    public string AppliedText => IsApplied ? "Mods are currently applied to this Roblox version." : "No mods are applied.";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); ApplyCommand.Refresh(); RevertCommand.Refresh(); } }

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RescanCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand RevertCommand { get; }

    public ModsViewModel()
    {
        Cursors = new CursorsViewModel(Location);
        Fonts = new FontsViewModel(Location);
        Cursors.EditRequested += spec =>
        {
            var copy = spec.Clone(); copy.Name = spec.IsBuiltIn ? spec.Name : spec.Name + " copy";
            Builder.Load(copy, null); Tab = 2;
        };
        Builder.Saved += id => Cursors.Reload(id);
        Picture.Added += id => { Cursors.Reload(id); Tab = 0; };
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(p => { if (p is ModRowViewModel r) Svc.S.Mods.Remove(r.Entry); });
        RescanCommand = new RelayCommand(Rescan);
        ApplyCommand = new AsyncCommand(Apply, () => !IsBusy && Installed && Svc.S.Mods.Any(m => m.Enabled));
        RevertCommand = new AsyncCommand(Revert, () => !IsBusy && IsApplied);
        Svc.S.Mods.CollectionChanged += (_, _) => Rescan();
        Rescan();
    }

    private void Rescan()
    {
        Version = Svc.Roblox.FindVersionFolder();
        Rows.Clear();
        foreach (var m in Svc.S.Mods)
        {
            var (valid, rej) = Svc.Mods.Inspect(m.SourcePath);
            Rows.Add(new ModRowViewModel { Entry = m, FileCount = valid.Count, Rejected = rej, Exists = System.IO.Directory.Exists(m.SourcePath) });
        }
        OnPropertyChanged(nameof(IsApplied)); OnPropertyChanged(nameof(AppliedText));
        ApplyCommand?.Refresh(); RevertCommand?.Refresh();
    }

    private void Add()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a mod folder (contains files mirroring Roblox's content folder)" };
        if (dlg.ShowDialog() != true) return;
        var (valid, _) = Svc.Mods.Inspect(dlg.FolderName);
        if (valid.Count == 0) { Message = "That folder has no supported files (png, jpg, ogg, mp3, ttf, otf, ktx, tga)."; Kind = StatusKind.Warning; return; }
        Svc.S.Mods.Add(new ModEntry { Name = System.IO.Path.GetFileName(dlg.FolderName.TrimEnd('\\')), SourcePath = dlg.FolderName });
        Message = $"Added mod with {valid.Count} file(s). Click “Apply mods” to install."; Kind = StatusKind.Info;
    }

    private async Task Apply()
    {
        if (Version == null) return;
        if (!Dialogs.Confirm("Apply mods?", "This replaces cosmetic asset files inside your Roblox install. Originals are backed up and can be restored with “Revert”. Roblox updates may reset them.", "Apply mods")) return;
        IsBusy = true; Kind = StatusKind.Info; Message = "Applying…";
        try
        {
            var r = await Svc.Mods.ApplyAsync(Version, Svc.S.Mods, CancellationToken.None);
            if (r.Error != null) { Message = r.Error; Kind = StatusKind.Error; }
            else { Message = $"Applied {r.Copied} file(s)" + (r.Skipped > 0 ? $", skipped {r.Skipped}." : "."); Kind = StatusKind.Success; }
        }
        finally { IsBusy = false; Rescan(); }
    }

    private async Task Revert()
    {
        if (Version == null) return;
        IsBusy = true;
        try { Message = await Svc.Mods.RevertAsync(Version); Kind = Message.StartsWith("Restored") ? StatusKind.Success : StatusKind.Error; }
        finally { IsBusy = false; Rescan(); }
    }
}

// ============================================================ Macros

public sealed record StepTypeChoice(MacroStepType Type, string Label);

public sealed class MacrosViewModel : ObservableObject
{
    private MacroDef? _selected;
    private string _message = "";

    public ObservableCollection<MacroDef> Macros => Svc.S.Macros;
    public MacroDef? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(RunLabel)); } }
    }
    public bool HasSelection => Selected != null;
    public bool IsRunning => Selected != null && Svc.Macros.IsRunning(Selected.Id);
    public string RunLabel => IsRunning ? "Stop" : "Run in 3 s";
    public string Message { get => _message; private set => Set(ref _message, value); }
    public List<StepTypeChoice> StepTypes { get; } = new()
    {
        new(MacroStepType.KeyPress, "Press key"), new(MacroStepType.KeyDown, "Hold key"), new(MacroStepType.KeyUp, "Release key"),
        new(MacroStepType.Click, "Mouse click"), new(MacroStepType.Wait, "Wait"),
        new(MacroStepType.RandomWait, "Random wait"), new(MacroStepType.Scroll, "Scroll wheel"), new(MacroStepType.MoveMouse, "Move mouse"),
        new(MacroStepType.TypeText, "Type text"), new(MacroStepType.Note, "Note"),
        new(MacroStepType.MouseDown, "Mouse button down"), new(MacroStepType.MouseUp, "Mouse button up"), new(MacroStepType.AutoClick, "Auto-click"),
        new(MacroStepType.KeyCombo, "Key combo"), new(MacroStepType.DoubleClick, "Double click"), new(MacroStepType.MoveTo, "Move mouse to spot"),
    };
    public List<string> ClickNames { get; } = new() { "Left", "Right", "Middle" };

    public RelayCommand AddMacro { get; }
    public RelayCommand DeleteMacro { get; }
    public RelayCommand AddStep { get; }
    public RelayCommand RemoveStep { get; }
    public RelayCommand MoveUp { get; }
    public RelayCommand MoveDown { get; }
    public RelayCommand RunCommand { get; }

    // ---- record, duplicate, share
    private readonly MacroRecorder _rec = new();
    private bool _recording;
    private string _downloadUrl = "", _shareMsg = "";
    public bool IsRecording { get => _recording; private set { Set(ref _recording, value); OnPropertyChanged(nameof(RecordLabel)); } }
    public string RecordLabel => IsRecording ? "Stop recording" : "Record";
    public string DownloadUrl { get => _downloadUrl; set => Set(ref _downloadUrl, value); }
    public string ShareMessage { get => _shareMsg; private set => Set(ref _shareMsg, value); }
    public RelayCommand RecordCommand { get; }
    public RelayCommand DuplicateMacro { get; }
    public RelayCommand CopyCodeCommand { get; }
    public RelayCommand SaveFileCommand { get; }
    public RelayCommand PasteCodeCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public AsyncCommand DownloadCommand { get; }
    public RelayCommand PickSpotCommand { get; }

    private void AddImported(MacroDef m, string source)
    {
        var baseName = m.Name; int i = 2;
        while (Macros.Any(x => x.Name == m.Name)) m.Name = $"{baseName} {i++}";
        Macros.Add(m); Selected = m;
        ShareMessage = $"Added “{m.Name}”{source}. Its hotkey is off until you set one. Look through the steps before you run it.";
    }

    private void StopRecording()
    {
        if (!IsRecording) return;
        var steps = _rec.Stop();
        IsRecording = false;
        if (Selected == null) return;
        if (steps.Count == 0) { Message = "Nothing was recorded."; return; }
        foreach (var s in steps) { if (Selected.Steps.Count >= MacroShare.MaxSteps) break; Selected.Steps.Add(s); }
        Message = $"Recorded {steps.Count} step{(steps.Count == 1 ? "" : "s")}.";
    }

    public MacrosViewModel()
    {
        _rec.StopRequested += () => Application.Current.Dispatcher.BeginInvoke(StopRecording);
        RecordCommand = new RelayCommand(() =>
        {
            if (IsRecording) { StopRecording(); return; }
            if (Selected == null) return;
            if (!_rec.Start()) { Message = "Windows didn't allow Nighty to listen to input, so recording isn't available."; return; }
            IsRecording = true;
            Message = "Recording your keys and clicks. Clicks on Nighty aren't recorded. Press Esc or Stop recording when you're done.";
        });
        DuplicateMacro = new RelayCommand(() =>
        {
            if (Selected == null) return;
            var c = new MacroDef { Name = Selected.Name + " copy", Repeat = Selected.Repeat, RepeatDelayMs = Selected.RepeatDelayMs, Speed = Selected.Speed, OnlyInRoblox = Selected.OnlyInRoblox, HoldMode = Selected.HoldMode, HotkeyEnabled = false };
            foreach (var s in Selected.Steps) c.Steps.Add(new MacroStep { Type = s.Type, Value = s.Value, Value2 = s.Value2, Value3 = s.Value3, Text = s.Text });
            Macros.Add(c); Selected = c;
        });
        CopyCodeCommand = new RelayCommand(() =>
        {
            if (Selected == null) return;
            var code = MacroShare.ToCode(Selected);
            try { Clipboard.SetText(code); }
            catch { ShareMessage = "Windows didn't let Nighty use the clipboard. Try again."; return; }
            ShareMessage = code.Length > 1900 ? $"Code copied ({code.Length} characters): too long for one Discord message, so send the file instead."
                                              : "Code copied. Paste it in chat; your friend presses Paste code.";
        });
        SaveFileCommand = new RelayCommand(() =>
        {
            if (Selected == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the macro as a file", Filter = "Nighty macro (*.nightymacro)|*.nightymacro", FileName = Selected.Name, DefaultExt = ".nightymacro" };
            if (dlg.ShowDialog() != true) return;
            try { File.WriteAllText(dlg.FileName, MacroShare.ToJson(Selected)); ShareMessage = "Saved " + Path.GetFileName(dlg.FileName) + "."; }
            catch (Exception ex) { ShareMessage = "The file couldn't be saved there: " + ex.Message; }
        });
        PasteCodeCommand = new RelayCommand(() =>
        {
            string text;
            try { text = Clipboard.GetText(); } catch { ShareMessage = "Windows didn't let Nighty use the clipboard. Try again."; return; }
            var (m, err) = MacroShare.Parse(text);
            if (m == null) { ShareMessage = err ?? "That couldn't be added."; return; }
            AddImported(m, " from the clipboard");
        });
        OpenFileCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open a macro file", Filter = "Nighty macros|*.nightymacro;*.json;*.txt|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var info = new FileInfo(dlg.FileName);
                if (info.Length == 0 || info.Length > 4 * 1024 * 1024) { ShareMessage = "That file couldn't be read (or is larger than 4 MB)."; return; }
                var (m, err) = MacroShare.Parse(File.ReadAllText(dlg.FileName));
                if (m == null) { ShareMessage = err ?? "That couldn't be added."; return; }
                AddImported(m, " from " + info.Name);
            }
            catch (Exception ex) { ShareMessage = "That file couldn't be read: " + ex.Message; }
        });
        DownloadCommand = new AsyncCommand(async () =>
        {
            if (string.IsNullOrWhiteSpace(DownloadUrl)) { ShareMessage = "Paste an https:// link first."; return; }
            ShareMessage = "Downloading…";
            var (m, err) = await MacroShare.DownloadAsync(DownloadUrl);
            if (m == null) { ShareMessage = err ?? "The download failed."; return; }
            AddImported(m, " from the link");
        });
        PickSpotCommand = new RelayCommand(p =>
        {
            if (p is not MacroStep s) return;
            var win = new SpotPicker();
            if (Application.Current.MainWindow is { } mw) mw.WindowState = WindowState.Minimized;
            win.ShowDialog();
            if (Application.Current.MainWindow is { } mw2) { mw2.WindowState = WindowState.Normal; mw2.Activate(); }
            if (win.Result is { } r) { s.Value = r.X; s.Value2 = r.Y; Message = $"Spot set to {r.X}, {r.Y}."; }
        });

        AddMacro = new RelayCommand(() =>
        {
            var m = new MacroDef { Name = UniqueName() };
            m.Steps.Add(new MacroStep { Type = MacroStepType.KeyPress, Value = 0x20 });
            m.Steps.Add(new MacroStep { Type = MacroStepType.Wait, Value = 100 });
            Macros.Add(m); Selected = m;
        });
        DeleteMacro = new RelayCommand(() =>
        {
            if (Selected == null) return;
            if (!Dialogs.Confirm("Delete macro?", $"“{Selected.Name}” will be removed.", "Delete", danger: true)) return;
            Svc.Macros.Stop(Selected.Id);
            Macros.Remove(Selected);
            Selected = Macros.FirstOrDefault();
        });
        AddStep = new RelayCommand(p =>
        {
            if (Selected == null) return;
            if (Selected.Steps.Count >= MacroShare.MaxSteps) { Message = $"A macro can have up to {MacroShare.MaxSteps} steps."; return; }
            var t = Enum.Parse<MacroStepType>(p?.ToString() ?? "Wait");
            Selected.Steps.Add(new MacroStep
            {
                Type = t,
                Value = t switch
                {
                    MacroStepType.Wait => 100, MacroStepType.RandomWait => 50, MacroStepType.AutoClick => 12, MacroStepType.KeyCombo => 0x43, MacroStepType.Scroll => 1,
                    MacroStepType.Click or MacroStepType.MouseDown or MacroStepType.MouseUp or MacroStepType.DoubleClick or MacroStepType.MoveMouse or MacroStepType.MoveTo or MacroStepType.TypeText or MacroStepType.Note => 0,
                    _ => 0x20,
                },
                Value2 = t switch { MacroStepType.RandomWait => 150, MacroStepType.AutoClick => 400, _ => 0 },
                Value3 = t == MacroStepType.KeyCombo ? Hotkeys.Ctrl : 0,
                Text = t == MacroStepType.TypeText ? "hello" : "",
            });
        });
        RemoveStep = new RelayCommand(p => { if (p is MacroStep s) Selected?.Steps.Remove(s); });
        MoveUp = new RelayCommand(p => Move(p as MacroStep, -1));
        MoveDown = new RelayCommand(p => Move(p as MacroStep, +1));
        RunCommand = new RelayCommand(() =>
        {
            if (Selected == null) return;
            if (Selected.Steps.Count == 0) { Message = "Add at least one step first."; return; }
            Svc.Macros.Toggle(Selected, 3000);
            Message = Svc.Macros.IsRunning(Selected.Id) ? "Starting in 3 seconds — click the window you want the macro to control." : "Stopped.";
        });

        Macros.CollectionChanged += OnMacrosChanged;
        foreach (var m in Macros) RegisterHotkey(m);
        Svc.Macros.Changed += () => Application.Current.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(RunLabel)); });
        Selected = Macros.FirstOrDefault();
    }

    private string UniqueName()
    {
        for (int i = 1; ; i++)
        {
            var n = $"Macro {i}";
            if (!Macros.Any(m => m.Name == n)) return n;
        }
    }

    private void Move(MacroStep? s, int delta)
    {
        if (s == null || Selected == null) return;
        int i = Selected.Steps.IndexOf(s), j = i + delta;
        if (i < 0 || j < 0 || j >= Selected.Steps.Count) return;
        Selected.Steps.Move(i, j);
    }

    private void OnMacrosChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (MacroDef m in e.OldItems) Svc.Hotkeys.Unregister("macro:" + m.Id);
        if (e.NewItems != null) foreach (MacroDef m in e.NewItems) RegisterHotkey(m);
    }

    private void RegisterHotkey(MacroDef m)
    {
        Svc.Hotkeys.Register("macro:" + m.Id, () => m.HotkeyEnabled ? (m.HotkeyVk, m.HotkeyMods) : (0, 0), down =>
        {
            bool allowed = !m.OnlyInRoblox || Svc.Roblox.IsForeground || Svc.Macros.IsRunning(m.Id);
            if (m.HoldMode) { if (down) { if (allowed && !Svc.Macros.IsRunning(m.Id)) Svc.Macros.Start(m); } else Svc.Macros.Stop(m.Id); }
            else if (down && allowed) Svc.Macros.Toggle(m);
        });
    }
}

// ============================================================ Overlays

public sealed class OverlaysViewModel : ObservableObject
{
    public ObservableCollection<OverlayConfig> Items => Svc.S.Overlays.Items;
    public OverlaySettings Settings => Svc.S.Overlays;
    public sealed record AdapterChoice(string Id, string Name);
    /// <summary>"Automatic" plus every connected network adapter, for the Ping overlay.</summary>
    public ObservableCollection<AdapterChoice> PingAdapters { get; } = new();
    public RelayCommand RefreshPingAdaptersCommand { get; }
    private void LoadPingAdapters()
    {
        string keep = Settings.PingAdapterId;
        PingAdapters.Clear();
        PingAdapters.Add(new AdapterChoice("", "Automatic (Windows chooses)"));
        try { foreach (var a in Svc.Dns.GetAdapters()) PingAdapters.Add(new AdapterChoice(a.Id, a.Name)); } catch { }
        // A saved adapter that is unplugged right now stays selectable, so the choice is not silently forgotten.
        if (keep.Length > 0 && PingAdapters.All(a => a.Id != keep)) PingAdapters.Add(new AdapterChoice(keep, "Saved adapter (not connected)"));
        Settings.PingAdapterId = keep;   // clearing the list made the box write an empty choice back; put the real one back
    }
    public bool FpsNeedsSetup => !Elevation.CanTraceEtw && Items.Any(i => i.Kind == OverlayKind.Fps && i.Enabled);
    public bool IsAdmin => Elevation.IsAdmin;
    public string LiveStatus { get => _live; private set => Set(ref _live, value); }
    private string _live = "";

    // ---- presets, style helpers
    public sealed class OverlayPresetVm { public required string Name { get; init; } public bool BuiltIn { get; init; } public string Kind => BuiltIn ? "Ready-made" : "Yours"; }
    public ObservableCollection<OverlayPresetVm> Presets { get; } = new();
    private string _presetName = "", _presetMsg = "";
    public string PresetName { get => _presetName; set => Set(ref _presetName, value); }
    public string PresetMessage { get => _presetMsg; private set => Set(ref _presetMsg, value); }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand LoadPresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand ResetStyleCommand { get; }
    public RelayCommand CopyStyleCommand { get; }
    public OverlayVisibility[] VisibilityChoices { get; } = Enum.GetValues<OverlayVisibility>();
    public OverlayVisibility Visibility { get => Settings.Visibility; set { Settings.Visibility = value; OnPropertyChanged(); } }
    public bool FollowRoblox { get => Settings.FollowRobloxWindow; set { Settings.FollowRobloxWindow = value; OnPropertyChanged(); } }
    private static string PresetDir => Path.Combine(AppPaths.Root, "overlay-presets");

    private static readonly string[] BuiltInPresets = { "Competitive", "Keys", "Minimal" };

    private static List<OverlayConfig> BuiltIn(string name)
    {
        OverlayConfig O(OverlayKind k, string t, double x, double y, bool on = true) => new() { Kind = k, Title = t, X = x, Y = y, Enabled = on };
        return name switch
        {
            "Competitive" => new() { O(OverlayKind.Cps, "CPS", 1, 2), O(OverlayKind.Fps, "FPS", 12, 2), O(OverlayKind.Ping, "Ping", 23, 2), O(OverlayKind.Mouse, "Mouse", 1, 90) },
            "Keys" => new() { O(OverlayKind.Wasd, "WASD", 2, 78), O(OverlayKind.Mouse, "Mouse", 13, 85), O(OverlayKind.Cps, "CPS", 2, 2) },
            _ => new() { O(OverlayKind.Cps, "CPS", 1, 2) },
        };
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var n in BuiltInPresets) Presets.Add(new OverlayPresetVm { Name = n, BuiltIn = true });
        try
        {
            if (Directory.Exists(PresetDir))
                foreach (var f in Directory.EnumerateFiles(PresetDir, "*.json").OrderBy(f => f)) Presets.Add(new OverlayPresetVm { Name = Path.GetFileNameWithoutExtension(f) });
        }
        catch { }
    }

    /// <summary>Replaces every overlay with the given list; Undo brings the old ones back.</summary>
    private void ReplaceAll(List<OverlayConfig> list, string? pingHost = null)
    {
        _beforeLoad = Items.Select(CloneOverlay).ToList();
        _beforePing = Settings.PingHost;
        Items.Clear();
        foreach (var o in list) { o.Id = Guid.NewGuid(); Items.Add(o); }
        if (!string.IsNullOrWhiteSpace(pingHost) && pingHost.Length <= 100) Settings.PingHost = pingHost;
        UndoLoadCommand?.Refresh();
    }

    public RelayCommand AddKeyCommand { get; }
    public RelayCommand AddCrosshairCommand { get; }
    public IReadOnlyList<CrosshairStyleChoice> CrosshairStyles => CrosshairOptions.Styles;
    public IReadOnlyList<CrosshairColorChoice> CrosshairColors => CrosshairOptions.Colors;
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ResetPositionsCommand { get; }
    public RelayCommand RelaunchAdminCommand { get; }
    public RelayCommand SaveLayoutCommand { get; }
    public RelayCommand LoadLayoutCommand { get; }
    public RelayCommand UndoLoadCommand { get; }
    public string ShareMessage { get => _shareMsg; private set => Set(ref _shareMsg, value); }
    private string _shareMsg = "";
    private List<OverlayConfig>? _beforeLoad;
    private const int MaxLayoutBytes = 256 * 1024;
    public AsyncCommand SetupFpsCommand { get; }
    public string SetupMessage { get => _setupMsg; private set => Set(ref _setupMsg, value); }
    private string _setupMsg = "";
    private bool _autoSetupTried;

    public OverlaysViewModel()
    {
        RefreshPingAdaptersCommand = new RelayCommand(LoadPingAdapters);
        LoadPingAdapters();
        if (Items.Count == 0) SeedDefaults();
        if (!Items.Any(i => i.Kind == OverlayKind.FishTracker)) Items.Add(new OverlayConfig { Kind = OverlayKind.FishTracker, Title = "Fish tracker", X = 1, Y = 12 });
        if (!Items.Any(i => i.Kind == OverlayKind.Hud)) Items.Add(new OverlayConfig { Kind = OverlayKind.Hud, Title = "Macro HUD", X = 99, Y = 3 });
        foreach (var i in Items) Hook(i);
        Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (OverlayConfig c in e.NewItems) Hook(c);
            Svc.Overlays.Sync();
        };

        RefreshPresets();
        ResetStyleCommand = new RelayCommand(p => { if (p is OverlayConfig c) { c.ResetStyle(); PresetMessage = "Style reset to the defaults."; } });
        CopyStyleCommand = new RelayCommand(p =>
        {
            if (p is not OverlayConfig src) return;
            foreach (var o in Items.Where(o => o != src && o.Kind != OverlayKind.Crosshair)) o.CopyStyleFrom(src);
            PresetMessage = "Every overlay now uses this style.";
        });
        SavePresetCommand = new RelayCommand(() =>
        {
            var name = PresetName.Trim();
            if (name.Length == 0) { PresetMessage = "Give the preset a name first."; return; }
            name = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch));
            try
            {
                Directory.CreateDirectory(PresetDir);
                File.WriteAllText(Path.Combine(PresetDir, name + ".json"), System.Text.Json.JsonSerializer.Serialize(new OverlayLayoutFile { PingHost = Settings.PingHost, Items = Items.ToList() }, LayoutJson));
                PresetMessage = $"Preset “{name}” saved."; PresetName = ""; RefreshPresets();
            }
            catch (Exception ex) { PresetMessage = "Couldn't save the preset: " + ex.Message; }
        });
        LoadPresetCommand = new RelayCommand(p =>
        {
            if (p is not OverlayPresetVm pr) return;
            try
            {
                if (pr.BuiltIn) { ReplaceAll(BuiltIn(pr.Name)); }
                else
                {
                    var f = System.Text.Json.JsonSerializer.Deserialize<OverlayLayoutFile>(File.ReadAllText(Path.Combine(PresetDir, pr.Name + ".json")), LayoutJson);
                    if (f?.Items == null || f.Items.Count == 0) { PresetMessage = "That preset is empty."; return; }
                    ReplaceAll(f.Items, f.PingHost);
                }
                PresetMessage = $"Loaded “{pr.Name}”. Undo is under Share overlays.";
            }
            catch (Exception ex) { PresetMessage = "Couldn't load the preset: " + ex.Message; }
        });
        DeletePresetCommand = new RelayCommand(p =>
        {
            if (p is not OverlayPresetVm { BuiltIn: false } pr) return;
            try { File.Delete(Path.Combine(PresetDir, pr.Name + ".json")); RefreshPresets(); PresetMessage = "Preset deleted."; } catch { PresetMessage = "The preset couldn't be deleted."; }
        });
        AddKeyCommand = new RelayCommand(() =>
        {
            int? vk = Dialogs.CaptureKey("Add key overlay", "Press the key you want to show on screen.");
            if (vk is not int v || v <= 0) return;
            if (Items.Any(i => i.Kind == OverlayKind.Key && i.KeyVk == v)) return;
            Items.Add(new OverlayConfig { Kind = OverlayKind.Key, KeyVk = v, Title = $"Key: {Hotkeys.KeyName(v)}", Enabled = true, X = 50, Y = 30 + (Items.Count * 2 % 40) });
        });
        AddCrosshairCommand = new RelayCommand(() =>
        {
            int n = Items.Count(i => i.Kind == OverlayKind.Crosshair) + 1;
            Items.Add(new OverlayConfig { Kind = OverlayKind.Crosshair, Title = n == 1 ? "Crosshair" : $"Crosshair {n}", Enabled = true, X = 50, Y = 50, Opacity = 1 });
        });
        RemoveCommand = new RelayCommand(p => { if (p is OverlayConfig c) Items.Remove(c); });
        SaveLayoutCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save overlays", Filter = "Nighty overlays (*.nightyoverlay)|*.nightyoverlay", FileName = "My overlays", DefaultExt = ".nightyoverlay" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new OverlayLayoutFile { PingHost = Settings.PingHost, Items = Items.ToList() }, LayoutJson);
                File.WriteAllText(dlg.FileName, json);
                ShareMessage = $"Saved {Items.Count} overlays to {Path.GetFileName(dlg.FileName)}. Send it to anyone with Nighty.";
            }
            catch (Exception ex) { ShareMessage = "Couldn't save the file: " + ex.Message; }
        });
        LoadLayoutCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Load overlays", Filter = "Nighty overlays (*.nightyoverlay)|*.nightyoverlay" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var info = new FileInfo(dlg.FileName);
                if (info.Length == 0) { ShareMessage = "The file is empty."; return; }
                if (info.Length > MaxLayoutBytes) { ShareMessage = "The file is too big to be an overlay layout."; return; }
                var file = System.Text.Json.JsonSerializer.Deserialize<OverlayLayoutFile>(File.ReadAllText(dlg.FileName), LayoutJson);
                if (file?.Items == null || file.Items.Count == 0 || file.Items.Count > 64) { ShareMessage = "That file couldn't be loaded: it has no valid overlays."; return; }
                if (!Dialogs.Confirm("Load overlays?", $"This replaces your {Items.Count} overlays with {file.Items.Count} from the file. Undo brings yours back.", "Load")) return;
                _beforeLoad = Items.Select(CloneOverlay).ToList();
                var oldPing = Settings.PingHost;
                Items.Clear();
                foreach (var o in file.Items) { o.Id = Guid.NewGuid(); Items.Add(o); }
                if (!string.IsNullOrWhiteSpace(file.PingHost) && file.PingHost.Length <= 100) Settings.PingHost = file.PingHost;
                _beforePing = oldPing;
                UndoLoadCommand?.Refresh();
                ShareMessage = $"Loaded {file.Items.Count} overlays. Undo is under Share overlays.";
            }
            catch (Exception ex) { ShareMessage = "That file couldn't be loaded. " + ex.Message; }
        });
        UndoLoadCommand = new RelayCommand(() =>
        {
            if (_beforeLoad == null) return;
            Items.Clear();
            foreach (var o in _beforeLoad) Items.Add(o);
            if (_beforePing != null) Settings.PingHost = _beforePing;
            _beforeLoad = null;
            UndoLoadCommand?.Refresh();
            ShareMessage = "Your previous overlays are back.";
        }, () => _beforeLoad != null);
        ResetPositionsCommand = new RelayCommand(() =>
        {
            int n = 0;
            foreach (var i in Items) { i.X = 50; i.Y = 4 + n++ * 9 % 80; i.Scale = 1; i.Opacity = 0.9; }
        });
        SetupFpsCommand = new AsyncCommand(async () =>
        {
            SetupMessage = "Waiting for administrator approval…";
            var (ok, msg) = await Elevation.AddToPerformanceLogUsersAsync();
            SetupMessage = ok ? "Done. Sign out of Windows and back in, then restart Nighty." : "Not changed: " + msg;
        });
        RelaunchAdminCommand = new RelayCommand(() =>
        {
            if (Dialogs.Confirm("Restart as administrator?", "The FPS overlay reads frame events through Windows tracing (ETW), which only administrators can start. Nighty will restart; your settings are kept.", "Restart"))
                if (Elevation.RelaunchAsAdmin()) Application.Current.Shutdown();
        });
        Svc.Overlays.Sync();
    }

    private string? _beforePing;
    private static readonly System.Text.Json.JsonSerializerOptions LayoutJson = new() { WriteIndented = true };

    private static OverlayConfig CloneOverlay(OverlayConfig o)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(o);
        return System.Text.Json.JsonSerializer.Deserialize<OverlayConfig>(json)!;
    }

    /// <summary>A .nightyoverlay file: positions and styles only, nothing else from the PC.</summary>
    public sealed class OverlayLayoutFile
    {
        public string? PingHost { get; set; }
        public List<OverlayConfig> Items { get; set; } = new();
    }

    private void SeedDefaults()
    {
        void A(OverlayKind k, string t, double x, double y) => Items.Add(new OverlayConfig { Kind = k, Title = t, X = x, Y = y });
        A(OverlayKind.Cps, "CPS", 1, 2); A(OverlayKind.Fps, "FPS", 12, 2); A(OverlayKind.Ping, "Ping", 23, 2);
        A(OverlayKind.Wasd, "WASD", 3, 80); A(OverlayKind.Mouse, "Mouse", 14, 82);
    }

    private void Hook(OverlayConfig c)
    {
        c.PropertyChanged += (_, e) =>
        {
            Svc.Overlays.Sync();
            if (e.PropertyName == nameof(OverlayConfig.Enabled))
            {
                OnPropertyChanged(nameof(FpsNeedsSetup));
                // Turning the FPS overlay on runs the one-time setup straight away (once per session unless it succeeds/fails visibly).
                if (c.Kind == OverlayKind.Fps && c.Enabled && !Elevation.CanTraceEtw && !_autoSetupTried)
                {
                    _autoSetupTried = true;
                    SetupFpsCommand.Execute(null);
                }
            }
        };
    }
}

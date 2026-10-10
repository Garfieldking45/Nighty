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
    };
    public List<string> ClickNames { get; } = new() { "Left", "Right", "Middle" };

    public RelayCommand AddMacro { get; }
    public RelayCommand DeleteMacro { get; }
    public RelayCommand AddStep { get; }
    public RelayCommand RemoveStep { get; }
    public RelayCommand MoveUp { get; }
    public RelayCommand MoveDown { get; }
    public RelayCommand RunCommand { get; }

    public MacrosViewModel()
    {
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
            var t = Enum.Parse<MacroStepType>(p?.ToString() ?? "Wait");
            Selected.Steps.Add(new MacroStep { Type = t, Value = t switch { MacroStepType.Wait => 100, MacroStepType.RandomWait => 50, MacroStepType.Click => 0, MacroStepType.Scroll => 1, MacroStepType.MoveMouse or MacroStepType.TypeText or MacroStepType.Note => 0, _ => 0x20 }, Value2 = t == MacroStepType.RandomWait ? 150 : 0, Text = t == MacroStepType.TypeText ? "hello" : "" });
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
            if (down && (!m.OnlyInRoblox || Svc.Roblox.IsForeground || Svc.Macros.IsRunning(m.Id))) Svc.Macros.Toggle(m);
        });
    }
}

// ============================================================ Overlays

public sealed class OverlaysViewModel : ObservableObject
{
    public ObservableCollection<OverlayConfig> Items => Svc.S.Overlays.Items;
    public OverlaySettings Settings => Svc.S.Overlays;
    public bool FpsNeedsSetup => !Elevation.CanTraceEtw && Items.Any(i => i.Kind == OverlayKind.Fps && i.Enabled);
    public bool IsAdmin => Elevation.IsAdmin;
    public string LiveStatus { get => _live; private set => Set(ref _live, value); }
    private string _live = "";

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
        if (Items.Count == 0) SeedDefaults();
        if (!Items.Any(i => i.Kind == OverlayKind.FishTracker)) Items.Add(new OverlayConfig { Kind = OverlayKind.FishTracker, Title = "Fish tracker", X = 1, Y = 12 });
        if (!Items.Any(i => i.Kind == OverlayKind.Hud)) Items.Add(new OverlayConfig { Kind = OverlayKind.Hud, Title = "Macro HUD", X = 99, Y = 3 });
        foreach (var i in Items) Hook(i);
        Items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (OverlayConfig c in e.NewItems) Hook(c);
            Svc.Overlays.Sync();
        };

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

// ============================================================ Settings

public sealed class SettingsViewModel : ObservableObject
{
    private string _message = "";
    public GeneralSettings General => Svc.S.General;
    public IReadOnlyList<ThemeChoice> Themes => ThemeService.Themes;
    public string SelectedTheme
    {
        get => ThemeService.Find(General.Theme).Id;
        set { General.Theme = value; ThemeService.Apply(value); OnPropertyChanged(); foreach (var w in Swatches) w.IsSelected = w.Choice.Id == value; }
    }
    private int _tab;
    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public sealed class ThemeSwatch : ObservableObject
    {
        private bool _sel;
        public required ThemeChoice Choice { get; init; }
        public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
    }
    public List<ThemeSwatch> Swatches { get; }
    public RelayCommand SelectThemeCommand { get; }
    public bool StartMinimized
    {
        get => General.StartMinimized;
        set
        {
            General.StartMinimized = value;
            if (StartupRegistration.IsEnabled) { try { StartupRegistration.Set(true); } catch (Exception ex) { Message = "Could not update startup entry: " + ex.Message; } }
            OnPropertyChanged();
        }
    }
    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set { try { StartupRegistration.Set(value); } catch (Exception ex) { Message = "Could not change startup setting: " + ex.Message; } OnPropertyChanged(); }
    }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public IReadOnlyList<ChangelogEntry> Changelog => Nighty.Models.Changelog.Entries;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public string AdminText => Elevation.IsAdmin ? "Running as administrator" : "Running as a standard user";
    public bool IsAdmin => Elevation.IsAdmin;
    public string SettingsPath => AppPaths.SettingsFile;

    public RelayCommand OpenDataFolder { get; }
    public RelayCommand OpenLogsFolder { get; }
    public RelayCommand RelaunchAdmin { get; }
    public AsyncCommand RestoreAllCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand ImportProfileCommand { get; }

    public SettingsViewModel()
    {
        Swatches = ThemeService.Themes.Select(t => new ThemeSwatch { Choice = t, IsSelected = t.Id == ThemeService.Find(Svc.S.General.Theme).Id }).ToList();
        SelectThemeCommand = new RelayCommand(o => { if (o is string id) SelectedTheme = id; });
        OpenDataFolder = new RelayCommand(() => Open(AppPaths.Root));
        OpenLogsFolder = new RelayCommand(() => Open(AppPaths.Logs));
        RelaunchAdmin = new RelayCommand(() =>
        {
            if (Dialogs.Confirm("Restart as administrator?", "Needed only for features such as the FPS overlay. Nighty will restart; your settings are kept.", "Restart"))
                if (Elevation.RelaunchAsAdmin()) Application.Current.Shutdown();
        }, () => !Elevation.IsAdmin);
        RestoreAllCommand = new AsyncCommand(async () =>
        {
            Svc.StopAllInput();
            Svc.S.Utility.Socd.Enabled = false;
            Svc.Tweaks.RevertAll();
            await Svc.GameMode.SetActiveAsync(false);
            var m = Svc.Movement.HasBackup ? Svc.Movement.Restore() : null;
            var p = Svc.Pointer.HasBackup ? Svc.Pointer.Restore() : null;
            Message = "Restored: Game Mode" + (m != null ? ", keyboard/accessibility" : "") + (p != null ? ", pointer" : "") +
                      ". DNS, QoS and mods are restored from their own pages because they need separate approval.";
        });
        ExportProfileCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export profile", Filter = "Nighty profile (*.json)|*.json", FileName = "nighty-profile", DefaultExt = ".json" };
            if (dlg.ShowDialog() != true) return;
            try { Svc.Settings.ExportProfile(dlg.FileName); Message = "Profile exported to " + Path.GetFileName(dlg.FileName) + ". It's plain JSON, so you can share it."; }
            catch (Exception ex) { Message = "Couldn't export: " + ex.Message; }
        });
        ImportProfileCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import profile", Filter = "Nighty profile (*.json)|*.json" };
            if (dlg.ShowDialog() != true) return;
            if (!Dialogs.Confirm("Import profile?", "This replaces your clicker, macros, overlays and other preferences with the file's, then restarts Nighty. Windows backups and mods are kept.", "Import")) return;
            var err = Svc.Settings.ImportProfile(dlg.FileName);
            if (err != null) { Message = err; return; }
            Svc.StopAllInput();
            // Start the new copy a moment later, once this one has released its single-instance lock.
            try { Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & start \"\" \"{Environment.ProcessPath}\"") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            Application.Current.Shutdown();
        });
        ResetCommand = new RelayCommand(() =>
        {
            if (!Dialogs.Confirm("Reset all settings?", "This restores system changes made by Nighty, deletes your presets, macros and overlay layout, and restarts the app.", "Reset", danger: true)) return;
            Svc.StopAllInput();
            Svc.GameMode.RestoreOnExit();
            if (Svc.Movement.HasBackup) Svc.Movement.Restore();
            if (Svc.Pointer.HasBackup) Svc.Pointer.Restore();
            Svc.Tweaks.RevertAll();
            Svc.Settings.Reset();
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); } catch { }
            Application.Current.Shutdown();
        });
    }

    private static void Open(string path)
    {
        try { System.IO.Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open folder failed", ex); }
    }
}

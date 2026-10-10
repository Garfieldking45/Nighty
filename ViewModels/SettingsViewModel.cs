using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed class ThemeCardVm : ObservableObject
{
    private bool _sel;
    public required ThemeDef Def { get; init; }
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
    public Brush Bg => Brush(Def.Bg);
    public Brush Panel => Brush(Def.Panel);
    public Brush Sidebar => Brush(Def.Sidebar);
    public Brush Accent => Brush(Def.Accent);
    public Brush Accent2 => Brush(Def.Accent2);
    public Brush Text => Brush(Def.Text);
    public Brush Border => Brush(Def.Border);
    private static Brush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
}

public sealed class AccentSwatchVm : ObservableObject
{
    private bool _sel;
    public required string Name { get; init; }
    public required string Hex { get; init; }
    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }
    public Brush Fill => (Brush)new BrushConverter().ConvertFromString(Hex)!;
}

public sealed class ColorRowVm : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private string _hex = "";
    public ColorRowVm(SettingsViewModel owner, string key, string label) { _owner = owner; Key = key; Label = label; }
    public string Key { get; }
    public string Label { get; }
    public Brush Swatch => ThemeService.TryParse(_hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
    public string Hex
    {
        get => _hex;
        set
        {
            var v = (value ?? "").Trim();
            if (!v.StartsWith('#')) v = "#" + v;
            if (v.Length != 7 || !ThemeService.TryParse(v, out _)) { OnPropertyChanged(); return; }
            v = v.ToUpperInvariant();
            if (v == _hex) return;
            _hex = v; OnPropertyChanged(); OnPropertyChanged(nameof(Swatch));
            _owner.SetCustomColor(Key, v);
        }
    }
    public void Load(string hex) { _hex = hex.ToUpperInvariant(); OnPropertyChanged(nameof(Hex)); OnPropertyChanged(nameof(Swatch)); }
}

public sealed class SettingsViewModel : ObservableObject
{
    private string _message = "";
    public GeneralSettings General => Svc.S.General;

    private int _tab;
    public int Tab { get => _tab; set => Set(ref _tab, value); }

    // ---------------- themes & colours ----------------
    public List<ThemeCardVm> ThemeCards { get; }
    public ObservableCollection<AccentSwatchVm> AccentSwatches { get; } = new();
    public List<ColorRowVm> ColorRows { get; }
    public RelayCommand SelectThemeCommand { get; }
    public RelayCommand SelectAccentCommand { get; }
    public RelayCommand ResetColorsCommand { get; }
    private string _contrastWarning = "";
    public string ContrastWarning { get => _contrastWarning; private set { Set(ref _contrastWarning, value); OnPropertyChanged(nameof(HasContrastWarning)); } }
    public bool HasContrastWarning => _contrastWarning.Length > 0;
    public string ThemeName => ThemeService.Palette(General.Theme).Name;

    // ---------------- font ----------------
    public List<string> Fonts { get; }
    public string SelectedFont
    {
        get => General.FontName.Length == 0 ? Fonts[0] : General.FontName;
        set
        {
            General.FontName = value == Fonts[0] ? "" : value;
            ThemeService.ApplyFont(General.FontName);
            OnPropertyChanged();
        }
    }

    // ---------------- background ----------------
    public RelayCommand ChooseBackgroundCommand { get; }
    public RelayCommand RemoveBackgroundCommand { get; }
    public bool HasBackground => General.BackgroundImage.Length > 0;
    public string BackgroundName => HasBackground ? Path.GetFileName(General.BackgroundImage) : "No background image";
    public bool BackgroundFit { get => General.BackgroundFit; set { General.BackgroundFit = value; OnPropertyChanged(); } }
    public double BackgroundStrengthPct { get => General.BackgroundStrength * 100; set { General.BackgroundStrength = value / 100; OnPropertyChanged(); } }

    // ---------------- performance ----------------
    public PrecisionMode Precision { get => General.Precision; set { General.Precision = value; OnPropertyChanged(); OnPropertyChanged(nameof(PrecisionHint)); } }
    public string PrecisionHint => General.Precision switch
    {
        PrecisionMode.Efficient => "Uses the least CPU. Clicks can be about 1 ms off.",
        PrecisionMode.Precise => "Most exact. Uses more CPU while clicking.",
        _ => "Exact and light. Best for most people.",
    };
    public AsyncCommand RunTimingTestCommand { get; }
    private bool _testing;
    private string _testMessage = "Press Run test to see how exact this PC delivers clicks with your current settings.";
    public bool IsTesting { get => _testing; private set { Set(ref _testing, value); RunTimingTestCommand?.Refresh(); } }
    public string TestMessage { get => _testMessage; private set => Set(ref _testMessage, value); }
    private TimingReport? _report;
    public TimingReport? Report { get => _report; private set { Set(ref _report, value); OnPropertyChanged(nameof(HasReport)); } }
    public bool HasReport => _report != null;

    // ---------------- discord ----------------
    public string DiscordStatus => Svc.Discord.Status;

    // ---------------- startup ----------------
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
    public RelayCommand CopySupportCommand { get; }
    public RelayCommand SaveNowCommand { get; }

    public SettingsViewModel()
    {
        ThemeCards = ThemeService.Palettes.Select(p => new ThemeCardVm { Def = p, IsSelected = p.Id == General.Theme }).ToList();
        ColorRows = new List<ColorRowVm>
        {
            new(this, "Accent", "Primary accent"), new(this, "Accent2", "Secondary accent"), new(this, "Bg", "Background"), new(this, "Sidebar", "Sidebar"),
            new(this, "Cards", "Cards"), new(this, "Text", "Primary text"), new(this, "Muted", "Secondary text"), new(this, "Borders", "Borders"),
        };
        Fonts = new List<string> { "Segoe UI Variable (default)" };
        Fonts.AddRange(System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        RefreshThemeState();

        SelectThemeCommand = new RelayCommand(o =>
        {
            if (o is not string id) return;
            General.Theme = id;
            ThemeService.ApplyFromSettings(true);
            RefreshThemeState();
            Sfx_();
        });
        SelectAccentCommand = new RelayCommand(o =>
        {
            if (o is not AccentSwatchVm sw) return;
            var themeAccent = ThemeService.Palette(General.Theme).Accent;
            General.AccentOverride = sw.Hex.Equals(themeAccent, StringComparison.OrdinalIgnoreCase) ? null : sw.Hex;
            General.CustomColors.Remove("Accent");
            ThemeService.ApplyFromSettings(true);
            RefreshThemeState();
        });
        ResetColorsCommand = new RelayCommand(() =>
        {
            General.CustomColors.Clear(); General.AccentOverride = null;
            ThemeService.ApplyFromSettings(true);
            RefreshThemeState();
            Message = "Colors reset to the theme.";
        });

        ChooseBackgroundCommand = new RelayCommand(ChooseBackground);
        RemoveBackgroundCommand = new RelayCommand(() =>
        {
            General.BackgroundImage = "";
            RefreshBackground();
            Message = "Background removed.";
        });

        RunTimingTestCommand = new AsyncCommand(RunTimingTest, () => !IsTesting);

        Svc.Discord.StatusChanged += () => Svc.Dispatch(() => OnPropertyChanged(nameof(DiscordStatus)));
        General.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GeneralSettings.DiscordPresence) or nameof(GeneralSettings.DiscordAppId)) Svc.Discord.Sync();
            if (e.PropertyName == nameof(GeneralSettings.AlwaysOnTop)) Svc.Toast.Toggled("Keep on top", General.AlwaysOnTop);
        };

        OpenDataFolder = new RelayCommand(() => Open(AppPaths.Root));
        OpenLogsFolder = new RelayCommand(() => Open(AppPaths.Logs));
        SaveNowCommand = new RelayCommand(() => { Svc.Settings.Save(); Message = "Settings saved."; });
        CopySupportCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(BuildSupportInfo()); Message = "Support info copied. Paste it when you ask for help."; }
            catch { Message = "Couldn't use the clipboard. Try again."; }
        });
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

    private static void Sfx_() => Nighty.Controls.Sfx.Click();

    // ---------------- theme helpers ----------------
    private void RefreshThemeState()
    {
        var p = ThemeService.Palette(General.Theme);
        foreach (var c in ThemeCards) c.IsSelected = c.Def.Id == p.Id;

        AccentSwatches.Clear();
        string eff = Effective("Accent");
        AccentSwatches.Add(new AccentSwatchVm { Name = p.Name + " accent", Hex = p.Accent });
        foreach (var a in ThemeService.Accents)
            if (!a.Accent.Equals(p.Accent, StringComparison.OrdinalIgnoreCase)) AccentSwatches.Add(new AccentSwatchVm { Name = a.Name, Hex = a.Accent });
        foreach (var s in AccentSwatches) s.IsSelected = s.Hex.Equals(eff, StringComparison.OrdinalIgnoreCase);

        foreach (var r in ColorRows) r.Load(Effective(r.Key));
        OnPropertyChanged(nameof(ThemeName));
        UpdateContrast();
    }

    private string Effective(string key)
    {
        var p = ThemeService.Palette(General.Theme);
        if (General.CustomColors.TryGetValue(key, out var v) && ThemeService.TryParse(v, out _)) return v;
        if (key == "Accent" && General.AccentOverride is { Length: > 0 } ao && ThemeService.TryParse(ao, out _)) return ao;
        return key switch
        {
            "Accent" => p.Accent, "Accent2" => p.Accent2, "Bg" => p.Bg, "Sidebar" => p.Sidebar, "Cards" => p.Panel,
            "Text" => p.Text, "Muted" => p.Muted, _ => p.Border,
        };
    }

    public void SetCustomColor(string key, string hex)
    {
        var p = ThemeService.Palette(General.Theme);
        string themeValue = key switch
        {
            "Accent" => p.Accent, "Accent2" => p.Accent2, "Bg" => p.Bg, "Sidebar" => p.Sidebar, "Cards" => p.Panel, "Text" => p.Text, "Muted" => p.Muted, _ => p.Border,
        };
        if (hex.Equals(themeValue, StringComparison.OrdinalIgnoreCase)) General.CustomColors.Remove(key); else General.CustomColors[key] = hex;
        if (key == "Accent") General.AccentOverride = null;
        Svc.Settings.MarkDirty();
        ThemeService.ApplyFromSettings(true);
        foreach (var s in AccentSwatches) s.IsSelected = s.Hex.Equals(Effective("Accent"), StringComparison.OrdinalIgnoreCase);
        UpdateContrast();
    }

    private void UpdateContrast()
    {
        var msgs = new List<string>();
        Color C(string key) => ThemeService.TryParse(Effective(key), out var c) ? c : Colors.Gray;
        double a = ThemeService.Contrast(C("Text"), C("Bg"));
        if (a < 4.5) msgs.Add($"Primary text is hard to read on this background ({a:0.0}:1). Aim for at least 4.5:1.");
        double b = ThemeService.Contrast(C("Muted"), C("Cards"));
        if (b < 3) msgs.Add($"Secondary text is hard to read on cards ({b:0.0}:1). Aim for at least 3:1.");
        double c2 = ThemeService.Contrast(C("Accent"), C("Cards"));
        if (c2 < 1.8) msgs.Add($"The accent barely stands out from the cards ({c2:0.0}:1).");
        ContrastWarning = string.Join("\n", msgs);
    }

    // ---------------- background ----------------
    private void ChooseBackground()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a background image", Filter = "Images (PNG, JPG, GIF)|*.png;*.jpg;*.jpeg;*.gif" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 60 * 1024 * 1024) { Message = "The image is larger than 60 MB."; return; }
            var test = Nighty.Controls.BackdropImage.Load(dlg.FileName);
            if (test.Count == 0) { Message = "Couldn't use that image."; return; }
            var dir = Path.Combine(AppPaths.Root, "backgrounds");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "background" + info.Extension.ToLowerInvariant());
            foreach (var old in Directory.GetFiles(dir, "background.*")) { try { File.Delete(old); } catch { } }
            File.Copy(dlg.FileName, dest, true);
            General.BackgroundImage = dest;
            RefreshBackground();
            Message = "Background set.";
        }
        catch (Exception ex) { Message = "Couldn't use that image: " + ex.Message; }
    }

    private void RefreshBackground()
    {
        OnPropertyChanged(nameof(HasBackground)); OnPropertyChanged(nameof(BackgroundName));
    }

    // ---------------- timing test ----------------
    private async Task RunTimingTest()
    {
        if (Svc.Clicker.IsClicking) { TestMessage = "Stop the clicker first."; return; }
        IsTesting = true; Report = null;
        TestMessage = "Testing for 5 seconds. Don't move your mouse too much…";
        try
        {
            var cps = Svc.S.Clicker.UseRange ? (Svc.S.Clicker.MinCps + Svc.S.Clicker.MaxCps) / 2 : Svc.S.Clicker.Cps;
            var r = await TimingTestService.RunAsync(cps, General.Precision, Svc.S.Clicker.HitFix, 5, CancellationToken.None);
            Report = r;
            TestMessage = r.Summary;
        }
        catch (Exception ex) { Log.Error("Timing test failed", ex); TestMessage = "Timing test failed: " + ex.Message; }
        finally { IsTesting = false; }
    }

    // ---------------- misc ----------------
    private string BuildSupportInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Nighty V{Version}");
        sb.AppendLine($"Windows: {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")}), {Environment.ProcessorCount} logical cores");
        sb.AppendLine($"Elevation: {(Elevation.IsAdmin ? "administrator" : "standard user")}");
        sb.AppendLine($"Theme: {General.Theme}, animations {(General.Animations ? "on" : "off")}, precision {General.Precision}");
        var c = Svc.S.Clicker;
        sb.AppendLine($"Clicker: {(c.UseRange ? $"{c.MinCps}-{c.MaxCps}" : c.Cps.ToString())} CPS, duty {c.DutyCycle}%, HitFix {(c.HitFix ? "on" : "off")}, per hit {c.ClicksPerHit}");
        sb.AppendLine($"Roblox running: {Svc.Roblox.IsRunning}");
        sb.AppendLine($"Discord: {Svc.Discord.Status}");
        return sb.ToString();
    }

    private static void Open(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open folder failed", ex); }
    }
}

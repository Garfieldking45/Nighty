using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

// TEMPORARY page for crossbow-switch testing. It is only added to the sidebar when Nighty starts with --testtab (or NIGHTY_TESTTAB=1),
// is not in the search index, and is not in the changelog. Delete this file, TestView and the MainViewModel/MainWindow lines to remove it.

public enum TestMode { Both, ClickerOnly, MacroOnly, Synced }

/// <summary>One crossbow setting to try, with a tick for "include in the queue".</summary>
public sealed class TestPreset : ObservableObject
{
    private bool _selected;
    public required string Name { get; init; }
    public required double Equip { get; init; }
    public required double Hold { get; init; }
    public required double Swap { get; init; }
    /// <summary>0 = automatic (1350 ms).</summary>
    public required double Cooldown { get; init; }
    public required bool ClickerShots { get; init; }
    /// <summary>Null leaves HitFix as it is; true or false switches it for this test.</summary>
    public bool? HitFix { get; init; }
    /// <summary>Both (normal), only the Auto Clicker, or only the crossbow macro (which then does its own clicking).</summary>
    public TestMode Mode { get; init; } = TestMode.Both;
    /// <summary>Synced mode only: ms between sword clicks. Just over the 0.3 s cooldown, so every click is a hit.</summary>
    public double SyncGapMs { get; init; } = 305;
    internal SyncedRunner.Part Part { get; init; } = SyncedRunner.Part.Both;
    public double PostPauseMs { get; init; }
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Line => Mode == TestMode.Synced
        ? $"SYNCED · one sword click every {SyncGapMs:0} ms, crossbow right after a hit · equip {Equip:0} · hold {Hold:0} · swap {Swap:0} ms · cooldown {(Cooldown <= 0 ? 1300 : Cooldown):0}{(HitFix is bool sh ? (sh ? " · HitFix on" : " · HitFix off") : "")}"
        : $"equip {Equip:0} · hold {Hold:0} · swap {Swap:0} ms · cooldown {(Cooldown <= 0 ? "auto" : Cooldown.ToString("0"))}{(ClickerShots ? " · clicker fires the shot" : "")}{(HitFix is bool h ? (h ? " · HitFix on" : " · HitFix off") : "")}{(Mode == TestMode.ClickerOnly ? " · clicker only" : Mode == TestMode.MacroOnly ? " · macro only" : "")}";
}

/// <summary>One finished run. The hit count is typed in by hand: the game, not Nighty, knows how many hits landed.</summary>
public sealed class TestRun : ObservableObject
{
    private string _hitsText = "";
    public required int Number { get; init; }
    public required string Name { get; init; }
    public required string Setting { get; init; }
    public required double Cps { get; init; }
    public required int Duty { get; init; }
    public required double WindowSec { get; init; }
    public required double ActualSec { get; init; }
    public required long Clicks { get; init; }
    public required long Shots { get; init; }
    public long Resyncs { get; init; }
    public long LateClicks { get; init; }
    public double MaxLateMs { get; init; }
    public double MaxAllowedMs { get; init; }
    public double MaxAfterClickMs { get; init; }
    public string HitsText { get => _hitsText; set => Set(ref _hitsText, value); }
    private string _note = "";
    /// <summary>Whether the hits were read off the screen or still need typing.</summary>
    public string Note { get => _note; set => Set(ref _note, value); }
    public double ExpectedClicks => Cps * ActualSec;
    public string Summary => $"{Name} · {Cps:0.0} CPS / {Duty}%";
    public string Detail => $"{ActualSec:0.00} s · {Shots} crossbow shots · {Clicks} sword clicks (about {ExpectedClicks:0} without switching)";
    /// <summary>Where clicks may have been lost: re-syncs drop a click each; the two "slowest" numbers name the likely culprit.</summary>
    public string Health => $"{Setting}\n{Resyncs} re-syncs · {LateClicks} clicks over 1 ms late · worst {MaxLateMs:0.0} ms · slowest foreground check {MaxAllowedMs:0.0} ms · slowest after-click hook {MaxAfterClickMs:0.0} ms";
}

public sealed class TestViewModel : ObservableObject
{
    private double _windowSec = 10.0;
    private int _waitSec = 8, _reps = 3;
    private string _status = "Ready. Put your clicker on its 34-hit setting, pick a crossbow setting, press Run, then switch to Roblox before the countdown ends.";
    private bool _isRunning;
    private CancellationTokenSource? _cts;
    private int _runNumber;

    public ClickerSettings Clicker => Svc.S.Clicker;
    /// <summary>The real Auto Crossbow macro the Extras page edits; the boxes on this page change the same values.</summary>
    public SlotMacroConfig Crossbow { get; }

    public double WindowSec { get => _windowSec; set => Set(ref _windowSec, Math.Clamp(value, 1, 120)); }
    public int WaitSec { get => _waitSec; set => Set(ref _waitSec, Math.Clamp(value, 0, 60)); }
    public int Reps { get => _reps; set => Set(ref _reps, Math.Clamp(value, 1, 20)); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    // ---- Reading the game's hit counter off the screen ----
    private HitReader.Region _area;
    private bool _autoRead = true;
    private string _areaText = "No hit area selected yet. Select one and the hits are filled in after every run.";
    private string _readText = "";
    private ImageSource? _preview;
    private static readonly string AreaFile = Path.Combine(AppPaths.Root, "testtab-area.json");
    /// <summary>How long after a run ends the counter is watched, so a result that appears a moment late is still caught.</summary>
    private const double ReadSeconds = 3.0;
    private bool _findByWord = true;
    private string _keyword = "Hits";
    private HitReader.Region? _lastFound;
    /// <summary>True: read the whole Roblox window and take the number next to <see cref="Keyword"/>. False: read the box that was drawn.</summary>
    public bool FindByWord
    {
        get => _findByWord;
        set { if (Set(ref _findByWord, value)) { OnPropertyChanged(nameof(UseBox)); TestRead?.Refresh(); } }
    }
    public bool UseBox { get => !_findByWord; set => FindByWord = !value; }
    public string Keyword { get => _keyword; set => Set(ref _keyword, value ?? ""); }
    public bool AutoRead { get => _autoRead; set => Set(ref _autoRead, value); }
    public string AreaText { get => _areaText; private set => Set(ref _areaText, value); }
    public string ReadText { get => _readText; private set => Set(ref _readText, value); }
    public ImageSource? Preview { get => _preview; private set => Set(ref _preview, value); }
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            Run?.Refresh(); RunQueue?.Refresh(); StopCommand?.Refresh(); SelectArea?.Refresh(); TestRead?.Refresh(); ShowArea?.Refresh();
        }
    }
    public bool IsIdle => !_isRunning;

    // The first block is the next round to try (ticked); the "done" block was run already (Faster swap scored best: 36, 36, 35).
    public ObservableCollection<TestPreset> Presets { get; } = new()
    {
        // ROUND 4: SYNCED. One sword click per cooldown (every ~305 ms, so each click is a hit) with the crossbow cycle right after a click, inside the gap.
        // Idea: about 33 sword hits + about 8 crossbow hits = 41. Different engine: the normal clicker and macro are not used for these.
        new() { Name = "DIAG A: sword only, 305 ms (expect 33)", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 305, Part = SyncedRunner.Part.SwordOnly, Selected = true },
        new() { Name = "DIAG B: crossbow only (expect 8)", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 305, Part = SyncedRunner.Part.BowOnly, Selected = true },
        new() { Name = "DIAG C: synced + 100 ms pause after swap", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 305, PostPauseMs = 100, Selected = true },
        new() { Name = "SYNCED 305 ms, Tight timing", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 305, Selected = false },
        new() { Name = "SYNCED 310 ms, Tight timing", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 310, Selected = false },
        new() { Name = "SYNCED 302 ms, Tight timing", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 302, Selected = false },
        new() { Name = "SYNCED 305 ms, Faster swap timing", Equip = 30, Hold = 30, Swap = 10, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 305, Selected = false },
        new() { Name = "SYNCED 305 ms, Tight timing, HitFix ON", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = true, Mode = TestMode.Synced, SyncGapMs = 305, Selected = false },
        new() { Name = "SYNCED 305 ms, cooldown 1320", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1320, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 305, Selected = false },
        new() { Name = "SYNCED 305 ms, cooldown 1350", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1350, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 305, Selected = false },
        new() { Name = "SYNCED 300 ms (right on the edge)", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.Synced, SyncGapMs = 300, Selected = false },
        // ROUND 3 (done). Baselines: sword only gave 34, 34, 34. Tight + 1300 gave 35, 35, 36; 1280 gave 34, 34, 34; HitFix ON gave 36, 35, 37.
        new() { Name = "Baseline: clicker only, no crossbow", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.ClickerOnly, Selected = false },
        new() { Name = "Baseline: crossbow macro only (it clicks for itself)", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Mode = TestMode.MacroOnly, Selected = false },
        // The best so far was Tight with cooldown 1300 / Tighter with cooldown 1300 (36, 36, 36). Walk the cooldown down: shots that come too early are swallowed.
        new() { Name = "Tight, cooldown 1300", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Selected = false },
        new() { Name = "Tight, cooldown 1280", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1280, ClickerShots = false, HitFix = false, Selected = false },
        new() { Name = "Tight, cooldown 1260", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1260, ClickerShots = false, HitFix = false, Selected = false },
        new() { Name = "Tight, cooldown 1250 (9 shots fit in 10 s)", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1250, ClickerShots = false, HitFix = false, Selected = false },
        // Same best setting with HitFix on: are the 30 ms stalls from the pinned thread? (A quiet system showed none, so it is the game or the macro.)
        new() { Name = "Tight, cooldown 1300, HitFix ON", Equip = 25, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = true, Selected = false },
        // Hold length: 10 ms looked too short (Tighter scored lower than Tight).
        new() { Name = "Hold 15, cooldown 1300", Equip = 25, Hold = 15, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Selected = false },
        new() { Name = "Hold 25, cooldown 1300", Equip = 25, Hold = 25, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Selected = false },
        new() { Name = "Equip 30, hold 20, cooldown 1300", Equip = 30, Hold = 20, Swap = 0, Cooldown = 1300, ClickerShots = false, HitFix = false, Selected = false },
        // EARLIER ROUNDS (3 runs each; Tight and Tighter+1300 both gave 36, 36, 36): kept to compare against.
        new() { Name = "Tight (done: 36, 36, 36)", Equip = 25, Hold = 20, Swap = 0, Cooldown = 0, ClickerShots = false, Selected = false },
        new() { Name = "Tighter (done: 34, 35, 35)", Equip = 22, Hold = 10, Swap = 0, Cooldown = 0, ClickerShots = false, Selected = false },
        new() { Name = "Tighter, cooldown 1300 (done: 36, 36, 36)", Equip = 22, Hold = 10, Swap = 0, Cooldown = 1300, ClickerShots = false, Selected = false },
        new() { Name = "Faster swap (done: 36, 36, 35)", Equip = 30, Hold = 30, Swap = 10, Cooldown = 0, ClickerShots = false, Selected = false },
        new() { Name = "Defaults (done)", Equip = 35, Hold = 35, Swap = 25, Cooldown = 0, ClickerShots = false, Selected = false },
        new() { Name = "Defaults + clicker shot (done, worse)", Equip = 35, Hold = 35, Swap = 25, Cooldown = 0, ClickerShots = true, Selected = false },
        new() { Name = "Faster swap + clicker shot (done, worse)", Equip = 30, Hold = 30, Swap = 10, Cooldown = 0, ClickerShots = true, Selected = false },
        new() { Name = "Defaults, cooldown 1400", Equip = 35, Hold = 35, Swap = 25, Cooldown = 1400, ClickerShots = false, Selected = false },
    };

    public ObservableCollection<TestRun> Results { get; } = new();

    public RelayCommand UsePreset { get; }
    public AsyncCommand SelectArea { get; }
    public AsyncCommand TestRead { get; }
    public AsyncCommand ShowArea { get; }
    public AsyncCommand Run { get; }
    public AsyncCommand RunQueue { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CopyResults { get; }
    public RelayCommand ClearResults { get; }

    public TestViewModel()
    {
        var found = Svc.S.SlotMacros.FirstOrDefault(m => m.Kind == SlotMacroKind.Crossbow);
        if (found == null) { found = SlotMacroConfig.Create(SlotMacroKind.Crossbow); Svc.S.SlotMacros.Insert(0, found); }
        Crossbow = found;
        LoadArea();
        SelectArea = new AsyncCommand(SelectAreaAsync, () => IsIdle);
        TestRead = new AsyncCommand(TestReadAsync, () => IsIdle && (FindByWord || !_area.IsEmpty));
        ShowArea = new AsyncCommand(ShowAreaAsync, () => IsIdle);

        UsePreset = new RelayCommand(o =>
        {
            if (o is not TestPreset p || IsRunning) return;
            Apply(p);
            Status = $"Set to {p.Name}.";
        });
        Run = new AsyncCommand(() => RunSession(new[] { (Name: "Current", Preset: (TestPreset?)null) }, 1), () => IsIdle);
        RunQueue = new AsyncCommand(() =>
        {
            var chosen = Presets.Where(p => p.Selected).Select(p => (p.Name, Preset: (TestPreset?)p)).ToArray();
            if (chosen.Length == 0) { Status = "Tick at least one test first."; return Task.CompletedTask; }
            return RunSession(chosen, Reps);
        }, () => IsIdle);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        CopyResults = new RelayCommand(() =>
        {
            try { Clipboard.SetText(ResultsText()); Status = "Results copied."; } catch { Status = "Could not reach the clipboard."; }
        });
        ClearResults = new RelayCommand(() => { Results.Clear(); _runNumber = 0; });
    }

    private void LoadArea()
    {
        try
        {
            if (!File.Exists(AreaFile)) return;
            var a = JsonSerializer.Deserialize<int[]>(File.ReadAllText(AreaFile));
            if (a is { Length: 4 } && new HitReader.Region(a[0], a[1], a[2], a[3]) is { IsEmpty: false } r) { _area = r; AreaText = $"Hit area: {r} (remembered)"; }
        }
        catch { }
    }

    private void SaveArea()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AreaFile, JsonSerializer.Serialize(new[] { _area.X, _area.Y, _area.W, _area.H }));
        }
        catch { }
    }

    private static ImageSource? ToImage(HitReader.Reading r)
    {
        if (r.Bgra.Length == 0 || r.Width <= 0 || r.Height <= 0) return null;
        var bmp = BitmapSource.Create(r.Width, r.Height, 96, 96, PixelFormats.Bgr32, null, r.Bgra, r.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static IntPtr RobloxWindow()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; } catch { }
            finally { p.Dispose(); }
        }
        return IntPtr.Zero;
    }

    /// <summary>One read, by whichever method is selected: the word search in the Roblox window, or the drawn box.</summary>
    private Task<HitReader.Reading> ReadNowAsync()
    {
        if (FindByWord)
        {
            var hwnd = RobloxWindow();
            string kw = Keyword;
            return Task.Run(() => HitReader.ReadWindowAsync(hwnd, kw));
        }
        var area = _area;
        return Task.Run(() => HitReader.ReadAsync(area));
    }

    /// <summary>Reads once and shows what was captured and what was read.</summary>
    private async Task<HitReader.Reading> ReadOnceAsync()
    {
        var r = await ReadNowAsync();
        Preview = ToImage(r);
        _lastFound = r.Found;
        ReadText = r.Problem ?? (r.Value is int v ? $"Read {v}  (text seen: “{r.Text}”)" : $"No number found. Text seen: “{r.Text}”. Try a tighter box around just the number.");
        return r;
    }

    /// <summary>Minimises Nighty so the game is visible, runs <paramref name="action"/> after a short countdown, then brings Nighty back.</summary>
    private async Task WithGameVisible(string what, int seconds, Func<Task> action)
    {
        var main = Application.Current.MainWindow;
        main.WindowState = WindowState.Minimized;
        try
        {
            for (int i = seconds; i > 0; i--)
            {
                Beep(660, 70);
                await Task.Delay(1000);
            }
            await action();
        }
        finally
        {
            main.WindowState = WindowState.Normal;
            main.Activate();
        }
    }

    private async Task SelectAreaAsync()
    {
        Status = "Switch to the game and show the hit counter. The selector opens in 5 seconds: drag a tight box around just the number.";
        await WithGameVisible("select", 5, async () =>
        {
            var picker = new RegionPickerWindow("Drag a box around the hit number.  Esc cancels.");
            picker.ShowDialog();
            if (picker.Result is not { } r) { Status = "Selection cancelled."; return; }
            _area = r;
            SaveArea();
            AreaText = $"Hit area: {r}";
            TestRead?.Refresh();
            await ReadOnceAsync();
            Status = _readText.StartsWith("Read ") ? "Area saved. " + _readText : "Area saved, but no number was read yet. " + _readText;
        });
    }

    /// <summary>Draws an orange outline for 3 seconds over where Nighty is reading from, so a wrong spot is obvious at a glance.</summary>
    private async Task ShowAreaAsync()
    {
        Status = FindByWord ? "Switch to the game with the counter showing. It looks for the word in 4 seconds and outlines what it finds."
                            : "Switch to the game. The saved box is outlined in 4 seconds.";
        await WithGameVisible("show", 4, async () =>
        {
            HitReader.Region? where;
            if (FindByWord)
            {
                var r = await ReadOnceAsync();
                where = r.Found;
                if (where == null) { Status = r.Problem ?? "Could not find it, so there is nothing to outline. " + ReadText; return; }
            }
            else where = _area.IsEmpty ? null : _area;
            if (where is not { } box) { Status = "No hit area selected yet."; return; }
            var flash = new AreaFlashWindow(box);
            flash.Show();
            await Task.Delay(3000);
            flash.Close();
            Status = FindByWord ? $"Outlined where it read the number. {ReadText}" : $"Outlined the saved box ({box}).";
        });
    }

    private async Task TestReadAsync()
    {
        Status = "Switch to the game and show the hit counter. It is read in 4 seconds.";
        await WithGameVisible("test read", 4, async () =>
        {
            await ReadOnceAsync();
            Status = ReadText;
        });
    }

    /// <summary>
    /// Watches the counter for a few seconds after a run and returns the value it settles on: the last number seen twice in a row,
    /// so a still-counting display gives its final value and one stray misread is ignored.
    /// </summary>
    private async Task<int?> ReadFinalHitsAsync(CancellationToken ct)
    {
        var reads = new List<int?>();
        HitReader.Reading? last = null;
        long until = Wait.Now + Wait.FromMs(ReadSeconds * 1000);
        while (Wait.Now < until)
        {
            ct.ThrowIfCancellationRequested();
            last = await ReadNowAsync();
            reads.Add(last.Value);
            if (last.Problem != null && !FindByWord) break;   // a missing word is worth retrying (the number may appear a moment later); a broken box is not
            await Task.Delay(100, ct);
        }
        if (last != null) { Preview = ToImage(last); _lastFound = last.Found; ReadText = last.Problem ?? $"Last read: {HitReader.Settle(reads)?.ToString() ?? "no number"}  (text seen: “{last.Text}”)"; }
        return HitReader.Settle(reads);
    }

    private void Apply(TestPreset p)
    {
        Crossbow.EquipDelayMs = p.Equip; Crossbow.ShotHoldMs = p.Hold; Crossbow.SwapDelayMs = p.Swap;
        Crossbow.CooldownMs = p.Cooldown; Crossbow.UseClickerShots = p.ClickerShots;
        if (p.HitFix is bool hf) Clicker.HitFix = hf;
    }

    private string CurrentSetting() =>
        $"equip {Crossbow.EquipDelayMs:0} · hold {Crossbow.ShotHoldMs:0} · swap {Crossbow.SwapDelayMs:0} ms · cooldown {(Crossbow.CooldownMs <= 0 ? "auto" : Crossbow.CooldownMs.ToString("0"))}"
        + $"{(Crossbow.UseClickerShots ? " · clicker fires the shot" : "")} · HitFix {(Clicker.HitFix ? "on" : "off")}";

    private string ResultsText()
    {
        var sb = new StringBuilder("#\ttest\tcps\tduty\tcrossbow setting\twindow s\tactual s\tcrossbow shots\tsword clicks\tresyncs\tlate>1ms\tworst late ms\tmax allowed ms\tmax afterclick ms\thits\thits source\n");
        foreach (var r in Results)
            sb.Append(CultureInfo.InvariantCulture,
                $"{r.Number}\t{r.Name}\t{r.Cps:0.0}\t{r.Duty}\t{r.Setting.Replace("\t", " ")}\t{r.WindowSec:0.0}\t{r.ActualSec:0.00}\t{r.Shots}\t{r.Clicks}\t{r.Resyncs}\t{r.LateClicks}\t{r.MaxLateMs:0.0}\t{r.MaxAllowedMs:0.0}\t{r.MaxAfterClickMs:0.0}\t{r.HitsText}\t{(r.Note.StartsWith("Hits read") ? "screen" : "typed")}\n");
        return sb.ToString();
    }

    private static void Beep(int hz, int ms) => Task.Run(() => { try { Console.Beep(hz, ms); } catch { } });

    private async Task RunSession((string Name, TestPreset? Preset)[] plan, int reps)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        var c = Clicker;
        var x = Crossbow;
        // What the page borrows for the run: no start delay or limits on the clicker, and the crossbow macro switched on, in Toggle style,
        // not tied to slot 1. Everything is put back at the end, together with the crossbow values the tests overwrite.
        var (delay, limit, after, hitFix) = (c.StartDelayMs, c.TimeLimitSec, c.StopAfterClicks, c.HitFix);
        var (xEnabled, xMode, xSlotOne) = (x.Enabled, x.Mode, x.OnlyInSlotOne);
        var (eq, hold, swap, cd, cs) = (x.EquipDelayMs, x.ShotHoldMs, x.SwapDelayMs, x.CooldownMs, x.UseClickerShots);
        c.StartDelayMs = 0; c.TimeLimitSec = 0; c.StopAfterClicks = 0;
        x.Enabled = true; x.Mode = SlotMacroStyle.Toggle; x.OnlyInSlotOne = false;
        try
        {
            int total = plan.Length * reps, done = 0;
            foreach (var p in plan)
            {
                for (int rep = 1; rep <= reps; rep++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (p.Preset != null) Apply(p.Preset);
                    string label = $"[{++done}/{total}] {p.Name}";
                    for (int i = WaitSec; i > 0; i--)
                    {
                        Status = $"{label}: starting in {i} s. Switch to Roblox and hold the sword.";
                        Beep(660, 70);
                        await Task.Delay(1000, ct);
                    }
                    Status = $"{label}: running for {WindowSec:0.0} s…";
                    Beep(1000, 180);
                    long shots0 = SlotMacroService.ShotsFired;
                    var mode = p.Preset?.Mode ?? TestMode.Both;
                    string setting = CurrentSetting() + (mode == TestMode.Synced && p.Preset != null ? $" · SYNCED: one sword click every {p.Preset.SyncGapMs:0} ms" : "");
                    SyncedRunner.Result? synced = null;
                    long t0, t1;
                    if (mode == TestMode.Synced && p.Preset != null)
                    {
                        // The synced engine drives the mouse and keys itself on its own thread; the real clicker and macro stay off.
                        var pr = p.Preset;
                        var cfg = new SyncedRunner.Settings(pr.SyncGapMs, 20, x.EquipDelayMs, x.ShotHoldMs, x.SwapDelayMs,
                            x.CooldownMs > 0 ? x.CooldownMs : 1300, (int)x.SlotA, (int)x.SlotB, pr.Part, pr.PostPauseMs);
                        bool boost = c.HitFix;
                        t0 = Wait.Now;
                        synced = await Task.Run(() => SyncedRunner.Run(cfg, WindowSec * 1000, ct, null, boost));
                        t1 = Wait.Now;
                    }
                    else
                    {
                        if (mode != TestMode.MacroOnly) Svc.Clicker.Start();
                        if (mode != TestMode.ClickerOnly) Svc.SlotMacros.Start(x);
                        t0 = Wait.Now;
                        long end = t0 + Wait.FromMs(WindowSec * 1000);
                        // The clicker and the macro have their own threads; this one only watches the clock so the window ends on time.
                        await Task.Run(() => Wait.Until(end, ct));
                        t1 = Wait.Now;
                        Svc.SlotMacros.Stop(SlotMacroKind.Crossbow);
                        Svc.Clicker.Stop();
                    }
                    Beep(500, 120);
                    bool clickerRan = mode is TestMode.Both or TestMode.ClickerOnly;
                    var run = new TestRun
                    {
                        Number = ++_runNumber, Name = p.Name, Setting = setting,
                        Cps = synced != null ? 1000.0 / p.Preset!.SyncGapMs : c.UseRange ? (c.MinCps + c.MaxCps) / 2 : c.Cps,
                        Duty = synced != null ? (int)Math.Clamp(Math.Round(20 / p.Preset!.SyncGapMs * 100), 5, 95) : c.DutyCycle,
                        WindowSec = WindowSec, ActualSec = Wait.ToMs(t1 - t0) / 1000.0,
                        Clicks = synced?.SwordClicks ?? (clickerRan ? Svc.Clicker.TotalClicks : 0),
                        Shots = synced?.Shots ?? SlotMacroService.ShotsFired - shots0,
                        Resyncs = synced?.CyclesSkipped ?? (clickerRan ? Svc.Clicker.Resyncs : 0),
                        LateClicks = synced?.LateClicks ?? (clickerRan ? Svc.Clicker.LateClicks : 0),
                        MaxLateMs = synced?.MaxLateMs ?? (clickerRan ? Svc.Clicker.MaxLateMs : 0),
                        MaxAllowedMs = clickerRan && synced == null ? Svc.Clicker.MaxAllowedMs : 0,
                        MaxAfterClickMs = clickerRan && synced == null ? Svc.Clicker.MaxAfterClickMs : 0,
                    };
                    Results.Add(run);
                    if (mode != TestMode.MacroOnly && run.Clicks == 0) Status = $"{label}: no clicks were sent. Roblox must be in front when the countdown ends.";
                    else if (mode != TestMode.ClickerOnly && run.Shots == 0) Status = $"{label}: the crossbow never fired. Roblox must be in front and your crossbow key must be set on the Extras page.";
                    if (AutoRead)
                    {
                        if (!FindByWord && _area.IsEmpty) run.Note = "No hit area selected, so type the hits in.";
                        else
                        {
                            Status = $"{label}: reading the hit counter…";
                            int? hits = await ReadFinalHitsAsync(ct);
                            if (hits is int h) { run.HitsText = h.ToString(); run.Note = "Hits read from the screen. Check it against the game if unsure."; }
                            else run.Note = "Could not read the counter, so type the hits in.";
                        }
                    }
                    await Task.Delay(300, CancellationToken.None);   // let the macro thread finish winding down before the next run
                }
            }
            Status = $"Done: {Results.Count} run(s) logged. Type the hits you counted into the table, then Copy.";
        }
        catch (OperationCanceledException) { Status = "Stopped."; }
        finally
        {
            Svc.SlotMacros.Stop(SlotMacroKind.Crossbow);
            Svc.Clicker.Stop();
            c.StartDelayMs = delay; c.TimeLimitSec = limit; c.StopAfterClicks = after; c.HitFix = hitFix;
            x.Enabled = xEnabled; x.Mode = xMode; x.OnlyInSlotOne = xSlotOne;
            x.EquipDelayMs = eq; x.ShotHoldMs = hold; x.SwapDelayMs = swap; x.CooldownMs = cd; x.UseClickerShots = cs;
            IsRunning = false;
            _cts?.Dispose(); _cts = null;
        }
    }
}

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Nighty.Mvvm;

namespace Nighty.Models;

public enum ClickButton { Left, Right, Middle }
/// <summary>Efficient waits on the OS timer only, Balanced spins the last 0.4 ms, Precise spins the last 2 ms.</summary>
public enum PrecisionMode { Efficient, Balanced, Precise }
public enum ActivationMode { Toggle, Hold }
public enum OverlayKind { Cps, Fps, Ping, Wasd, Mouse, Key, Crosshair, FishTracker, Hud }
public enum CrosshairStyle { Cross, CrossDot, Dot, Circle }

public sealed record CrosshairStyleChoice(string Name, CrosshairStyle Value);
public sealed record CrosshairColorChoice(string Name, string Hex);

public static class CrosshairOptions
{
    public static readonly IReadOnlyList<CrosshairStyleChoice> Styles = new CrosshairStyleChoice[]
    {
        new("Cross", CrosshairStyle.Cross), new("Cross with dot", CrosshairStyle.CrossDot), new("Dot", CrosshairStyle.Dot), new("Circle", CrosshairStyle.Circle),
    };
    public static readonly IReadOnlyList<CrosshairColorChoice> Colors = new CrosshairColorChoice[]
    {
        new("Green", "#00FF55"), new("Red", "#FF3B30"), new("White", "#FFFFFF"), new("Cyan", "#00E5FF"),
        new("Yellow", "#FFE600"), new("Magenta", "#FF2DDC"), new("Blue", "#3B82F6"), new("Black", "#000000"),
    };
}
public enum SocdMode { LastInput, Neutral, FirstInput }
public enum OverlayVisibility { Always, WhileRobloxOpen, WhileRobloxFront }
public enum CpsLayout { Compact, Big, Graph }
/// <summary>Which clicks the CPS overlay counts: both, only your own, or only the auto clicker's (and macros').</summary>
public enum CpsSource { Both, Mine, Clicker }
public enum CpsButtons { Left, Right, Both }
public enum SlotMacroKind { Crossbow, Whim, Lasso, BuildUp, Melody, GingerBread }
public enum SlotMacroStyle { Toggle, Press, Hold }

public sealed class SlotMacroConfig : ObservableObject
{
    private bool _enabled, _onlyRoblox = true;
    private int _vk, _mods, _delay, _look;
    private double _a = 1, _b = 2;

    public SlotMacroKind Kind { get; set; }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool OnlyWhenRobloxFocused { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }
    private bool _onlySlotOne = true;
    /// <summary>Auto Crossbow only: start only while hotbar slot 1 is the slot you are holding (pick it with the 1 key). Other macros ignore this.</summary>
    public bool OnlyInSlotOne { get => _onlySlotOne; set => Set(ref _onlySlotOne, value); }
    [JsonIgnore] public bool IsCrossbow => Kind == SlotMacroKind.Crossbow;
    public int HotkeyVk { get => _vk; set { if (Set(ref _vk, value)) OnPropertyChanged(nameof(KeyText)); } }
    public int HotkeyMods { get => _mods; set { if (Set(ref _mods, value)) OnPropertyChanged(nameof(KeyText)); } }
    /// <summary>First hotbar slot used (book, lasso, blocks, guitar or gumdrop).</summary>
    public double SlotA { get => _a; set => Set(ref _a, Math.Clamp(Math.Round(value), 1, 9)); }
    /// <summary>Second hotbar slot used (sword, blocks or pickaxe).</summary>
    public double SlotB { get => _b; set => Set(ref _b, Math.Clamp(Math.Round(value), 1, 9)); }
    private double _block;
    /// <summary>Hotbar slot holding your blocks (0 = off). Auto Crossbow does nothing while it is selected, so you keep placing blocks.</summary>
    public double BlockSlot { get => _block; set => Set(ref _block, Math.Clamp(Math.Round(value), 0, 9)); }
    public int DelayMs { get => _delay; set => Set(ref _delay, Math.Clamp(value, 0, 10000)); }
    /// <summary>Mouse movement down, in pixels (restored afterwards by Build Up).</summary>
    public int LookDown { get => _look; set => Set(ref _look, Math.Clamp(value, 0, 4000)); }

    [JsonIgnore] public string KeyText => HotkeyVk <= 0 ? "no key set" : Hotkeys.Format(HotkeyVk, HotkeyMods);
    private SlotMacroStyle? _mode;
    /// <summary>How the key works. Null uses the default for this macro.</summary>
    public SlotMacroStyle? Mode
    {
        get => _mode;
        set { if (Set(ref _mode, value)) { OnPropertyChanged(nameof(Style)); OnPropertyChanged(nameof(StyleText)); OnPropertyChanged(nameof(KeyHint)); } }
    }
    [JsonIgnore] public SlotMacroStyle Style
    {
        get => Kind is SlotMacroKind.Lasso or SlotMacroKind.GingerBread ? SlotMacroStyle.Press : _mode ?? DefaultStyle(Kind);
        set => Mode = value;
    }
    /// <summary>One-shot macros only have Press; the others can be Toggle, Hold or (crossbow, whim) a single Press.</summary>
    [JsonIgnore] public bool HasModes => Kind is not (SlotMacroKind.Lasso or SlotMacroKind.GingerBread);
    private static SlotMacroStyle DefaultStyle(SlotMacroKind k) => k switch
    {
        SlotMacroKind.Whim => SlotMacroStyle.Toggle,
        SlotMacroKind.Crossbow or SlotMacroKind.BuildUp or SlotMacroKind.Melody => SlotMacroStyle.Hold,
        _ => SlotMacroStyle.Press,
    };
    [JsonIgnore] public string Title => Kind switch
    {
        SlotMacroKind.Crossbow => "Auto Crossbow", SlotMacroKind.Whim => "Auto Whim", SlotMacroKind.Lasso => "Auto Lasso", SlotMacroKind.BuildUp => "Auto Build Up",
        SlotMacroKind.Melody => "Auto Melody", _ => "Auto GingerBread Man",
    };
    [JsonIgnore] public string Description => Kind switch
    {
        SlotMacroKind.Crossbow => "Fires the crossbow, swings the sword the instant it is selected, clicks through the 1.3 s cooldown, then repeats.",
        SlotMacroKind.Whim => "Swaps to the book, fires, swings the sword, and clicks through the 1.1 s cooldown, then repeats.",
        SlotMacroKind.Lasso => "Holds the lasso, looks down, swaps to blocks, then places five blocks under you.",
        SlotMacroKind.BuildUp => "Swaps to blocks, looks down and spam clicks at the Auto Clicker speed; when it stops it restores your view and swaps back to the sword.",
        SlotMacroKind.Melody => "Hits with the sword, swaps to the guitar and clicks, swaps back to the sword, then waits.",
        _ => "Swaps to the gumdrop and clicks, waits, then swaps to the pickaxe and clicks.",
    };
    [JsonIgnore] public string StyleText => Style switch { SlotMacroStyle.Toggle => "Toggle", SlotMacroStyle.Hold => "Hold", _ => "Press" };
    [JsonIgnore] public string KeyHint => $"{StyleText}: click, then press a key or side button.";
    [JsonIgnore] public string LabelA => Kind switch
    {
        SlotMacroKind.Crossbow => "CROSSBOW SLOT", SlotMacroKind.Whim => "BOOK SLOT", SlotMacroKind.Lasso => "LASSO SLOT", SlotMacroKind.BuildUp => "BLOCK SLOT",
        SlotMacroKind.Melody => "GUITAR SLOT", _ => "GUMDROP SLOT",
    };
    [JsonIgnore] public string LabelBlock => Kind == SlotMacroKind.Crossbow ? "BLOCK SLOT (0 = OFF)" : "";
    [JsonIgnore] public string LabelB => Kind switch
    {
        SlotMacroKind.Lasso => "BLOCK SLOT", SlotMacroKind.GingerBread => "PICKAXE SLOT", _ => "SWORD SLOT",
    };
    [JsonIgnore] public string LabelDelay => Kind switch
    {
        SlotMacroKind.Lasso => "HOLD (MS)", SlotMacroKind.Melody or SlotMacroKind.GingerBread => "DELAY (MS)", _ => "",
    };
    [JsonIgnore] public string LabelLook => Kind is SlotMacroKind.Lasso or SlotMacroKind.BuildUp ? "LOOK DOWN (PX)" : "";

    public SlotMacroConfig Snapshot() => (SlotMacroConfig)MemberwiseClone();

    public static SlotMacroConfig Create(SlotMacroKind k) => k switch
    {
        SlotMacroKind.Crossbow => new() { Kind = k, HotkeyVk = 0x05, SlotA = 3, SlotB = 1, BlockSlot = 2 },
        SlotMacroKind.Whim => new() { Kind = k, HotkeyVk = 0x05, SlotA = 3, SlotB = 1 },
        SlotMacroKind.Lasso => new() { Kind = k, HotkeyVk = 0x06, SlotA = 1, SlotB = 2, DelayMs = 1000, LookDown = 1200 },
        SlotMacroKind.BuildUp => new() { Kind = k, HotkeyVk = 0x06, SlotA = 2, SlotB = 1, LookDown = 1200 },
        SlotMacroKind.Melody => new() { Kind = k, HotkeyVk = 0x06, SlotA = 2, SlotB = 1, DelayMs = 1000 },
        _ => new() { Kind = k, HotkeyVk = 0x05, SlotA = 1, SlotB = 2, DelayMs = 1000 },
    };
}

public enum MacroStepType { KeyPress, KeyDown, KeyUp, Click, Wait, Scroll, MoveMouse, RandomWait, TypeText, Note, MouseDown, MouseUp, AutoClick, KeyCombo, DoubleClick, MoveTo }

public sealed class ClickerSettings : ObservableObject
{
    private bool _enabled;
    private double _cps = 12, _minCps = 10, _maxCps = 14;
    private bool _useRange, _onlyRoblox;
    private ClickButton _button = ClickButton.Left;
    private int _duty = 50, _vk = 0x75, _mods;
    private ActivationMode _mode = ActivationMode.Toggle;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private bool _jitter;
    /// <summary>Randomize each gap between clicks to 60-140% of the base period.</summary>
    public bool Jitter { get => _jitter; set => Set(ref _jitter, value); }
    private int _perHit = 1;
    /// <summary>How many clicks are sent each time the clicker fires (1 = normal).</summary>
    public int ClicksPerHit { get => _perHit; set => Set(ref _perHit, Math.Clamp(value, 1, 5)); }
    public double Cps { get => _cps; set => Set(ref _cps, Math.Clamp(Math.Round(value, 1), 1, 100)); }
    public bool UseRange { get => _useRange; set => Set(ref _useRange, value); }
    public double MinCps
    {
        get => _minCps;
        set { if (Set(ref _minCps, Math.Clamp(Math.Round(value, 1), 1, 100)) && _minCps > _maxCps) MaxCps = _minCps; }
    }
    public double MaxCps
    {
        get => _maxCps;
        set { if (Set(ref _maxCps, Math.Clamp(Math.Round(value, 1), 1, 100)) && _maxCps < _minCps) MinCps = _maxCps; }
    }
    public ClickButton Button { get => _button; set => Set(ref _button, value); }
    public int DutyCycle { get => _duty; set => Set(ref _duty, Math.Clamp(value, 5, 95)); }
    public ActivationMode Mode { get => _mode; set => Set(ref _mode, value); }
    public int HotkeyVk { get => _vk; set => Set(ref _vk, value); }
    public int HotkeyMods { get => _mods; set => Set(ref _mods, value); }
    public bool OnlyWhenRobloxFocused { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }

    private bool _hitFix = true;
    private int _stopAfter, _timeLimit, _startDelayMs;
    /// <summary>Steadier clicks: the clicker thread gets real-time priority, its own CPU core and an exact final spin before every click.</summary>
    public bool HitFix { get => _hitFix; set => Set(ref _hitFix, value); }
    /// <summary>Stops by itself after this many clicks (0 = never).</summary>
    public int StopAfterClicks { get => _stopAfter; set => Set(ref _stopAfter, Math.Clamp(value, 0, 1_000_000)); }
    /// <summary>Stops by itself after this many seconds (0 = never).</summary>
    public int TimeLimitSec { get => _timeLimit; set => Set(ref _timeLimit, Math.Clamp(value, 0, 86400)); }
    /// <summary>Wait this long after starting before the first click.</summary>
    public int StartDelayMs { get => _startDelayMs; set => Set(ref _startDelayMs, Math.Clamp(value, 0, 10000)); }

    public void CopyFrom(ClickerSettings o)
    {
        Cps = o.Cps; UseRange = o.UseRange; MinCps = o.MinCps; MaxCps = o.MaxCps; Button = o.Button;
        DutyCycle = o.DutyCycle; Mode = o.Mode; HotkeyVk = o.HotkeyVk; HotkeyMods = o.HotkeyMods;
        OnlyWhenRobloxFocused = o.OnlyWhenRobloxFocused; ClicksPerHit = o.ClicksPerHit; Jitter = o.Jitter;
        HitFix = o.HitFix; StopAfterClicks = o.StopAfterClicks; TimeLimitSec = o.TimeLimitSec; StartDelayMs = o.StartDelayMs;
    }

    public ClickerSettings Clone() { var c = new ClickerSettings(); c.CopyFrom(this); return c; }
}

public sealed class ClickerPreset : ObservableObject
{
    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public ClickerSettings Data { get; set; } = new();
    public string Summary =>
        $"{(Data.UseRange ? $"{Data.MinCps:0.#}–{Data.MaxCps:0.#}" : $"{Data.Cps:0.#}")} CPS · {Data.Button} · {Data.DutyCycle}% duty · {Data.Mode}";
}

public sealed class GameSettings : ObservableObject
{
    private bool _power = true, _priority = true, _timer = true, _gameMode = true, _awake, _auto = true;
    /// <summary>Turn Game Mode on when Roblox starts and off again when it closes.</summary>
    public bool AutoGameMode { get => _auto; set => Set(ref _auto, value); }
    public bool HighPerformancePower { get => _power; set => Set(ref _power, value); }
    public bool RobloxHighPriority { get => _priority; set => Set(ref _priority, value); }
    public bool TimerResolution { get => _timer; set => Set(ref _timer, value); }
    public bool WindowsGameMode { get => _gameMode; set => Set(ref _gameMode, value); }
    public bool KeepDisplayAwake { get => _awake; set => Set(ref _awake, value); }
    private bool _calm = true, _freeMem = true;
    /// <summary>While Roblox runs, browsers, launchers and updaters wait: they drop to Below normal priority until Game Mode ends.</summary>
    public bool CalmBackgroundApps { get => _calm; set => Set(ref _calm, value); }
    /// <summary>When Roblox starts, those same background apps give back memory they are not using.</summary>
    public bool FreeMemory { get => _freeMem; set => Set(ref _freeMem, value); }
    public HashSet<string> CleanerCategories { get; set; } = new() { "user_temp", "shader", "crash", "roblox_logs", "thumbs" };
}

public sealed class MovementSettings : ObservableObject
{
    private bool _sticky, _filter, _toggle, _repeat;
    public bool DisableStickyKeysShortcut { get => _sticky; set => Set(ref _sticky, value); }
    public bool DisableFilterKeysShortcut { get => _filter; set => Set(ref _filter, value); }
    public bool DisableToggleKeysShortcut { get => _toggle; set => Set(ref _toggle, value); }
    public bool FastKeyRepeat { get => _repeat; set => Set(ref _repeat, value); }
}

/// <summary>Resolves opposite directions held together (A+D, W+S) so one of them always wins.</summary>
public sealed class SocdSettings : ObservableObject
{
    private bool _enabled, _leftRight = true, _forwardBack = true;
    private SocdMode _mode = SocdMode.FirstInput;
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public SocdMode Mode { get => _mode; set => Set(ref _mode, value); }
    public bool LeftRight { get => _leftRight; set => Set(ref _leftRight, value); }
    public bool ForwardBack { get => _forwardBack; set => Set(ref _forwardBack, value); }
}

public sealed class TrackingSettings : ObservableObject
{
    private int _speed = 10;
    private bool _precision = true;
    public int PointerSpeed { get => _speed; set => Set(ref _speed, Math.Clamp(value, 1, 20)); }
    public bool EnhancePointerPrecision { get => _precision; set => Set(ref _precision, value); }

    private bool _slowOne;
    private int _slowSpeed = 6;
    /// <summary>While hotbar slot 1 is selected (and Roblox is in front), use the slower pointer speed below.</summary>
    public bool SlowInSlotOne { get => _slowOne; set => Set(ref _slowOne, value); }
    public int SlowSpeed { get => _slowSpeed; set => Set(ref _slowSpeed, Math.Clamp(value, 1, 20)); }
    private int _otherSlotSpeed = 10;
    /// <summary>While Roblox is in front with the slot-1 slowdown on, the pointer speed in every slot other than 1.</summary>
    public int OtherSlotSpeed { get => _otherSlotSpeed; set => Set(ref _otherSlotSpeed, Math.Clamp(value, 1, 20)); }

    private int _scrollLines, _doubleClickMs;
    /// <summary>Lines per wheel notch. 0 leaves the Windows setting alone.</summary>
    public int ScrollLines { get => _scrollLines; set => Set(ref _scrollLines, value <= 0 ? 0 : Math.Clamp(value, 1, 100)); }
    /// <summary>Double-click speed in milliseconds. 0 leaves the Windows setting alone.</summary>
    public int DoubleClickMs { get => _doubleClickMs; set => Set(ref _doubleClickMs, value <= 0 ? 0 : Math.Clamp(value, 200, 900)); }

    private bool _onlyRoblox;
    private int _dpi = 800;
    private double _sens = 0.5;
    /// <summary>Use the values above only while Roblox is in front; Windows' own values come back when you leave.</summary>
    public bool ApplyOnlyInRoblox { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }
    public int MouseDpi { get => _dpi; set => Set(ref _dpi, Math.Clamp(value, 100, 32000)); }
    public double GameSensitivity { get => _sens; set => Set(ref _sens, Math.Clamp(Math.Round(value, 3), 0.01, 100)); }
}

public sealed class DnsSettings : ObservableObject
{
    private string? _adapter;
    private bool _autoBest;
    public string? AdapterId { get => _adapter; set => Set(ref _adapter, value); }
    /// <summary>Resolvers you added yourself (up to 6), each "primary" or "primary,secondary" (IPv4).</summary>
    public List<string> Custom { get; set; } = new();
    public bool AutoApplyBest { get => _autoBest; set => Set(ref _autoBest, value); }
}

public sealed class QosSettings : ObservableObject
{
    private int _dscp = 46;
    public int Dscp { get => _dscp; set => Set(ref _dscp, value); }
    private bool _home = true, _nlaSet;
    /// <summary>Also apply the policy on home Wi-Fi and Ethernet. Without this Windows only marks traffic on domain networks.</summary>
    public bool UseOnHomeNetworks { get => _home; set => Set(ref _home, value); }
    /// <summary>True when Nighty created the "Do not use NLA" value, so Remove only deletes what Nighty added.</summary>
    public bool NlaSetByNighty { get => _nlaSet; set => Set(ref _nlaSet, value); }
}

public sealed class UtilitySettings : ObservableObject
{
    public MovementSettings Movement { get; set; } = new();
    public SocdSettings Socd { get; set; } = new();
    public TrackingSettings Tracking { get; set; } = new();
    public DnsSettings Dns { get; set; } = new();
    public QosSettings Qos { get; set; } = new();
}

public sealed class OverlayConfig : ObservableObject
{
    private bool _enabled;
    private double _x = 50, _y = 5, _scale = 1, _opacity = 0.9;
    private string _title = "";
    private int _vk;

    public Guid Id { get; set; } = Guid.NewGuid();
    public OverlayKind Kind { get; set; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Position as a percentage of the free space on the primary display (0–100).</summary>
    public double X { get => _x; set => Set(ref _x, Math.Clamp(Math.Round(value, 1), 0, 100)); }
    public double Y { get => _y; set => Set(ref _y, Math.Clamp(Math.Round(value, 1), 0, 100)); }
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(Math.Round(value, 2), 0.5, 2.5)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(Math.Round(value, 2), 0.2, 1)); }
    public int KeyVk { get => _vk; set { if (Set(ref _vk, value)) OnPropertyChanged(nameof(Blurb)); } }

    // Crosshair appearance (only used when Kind is Crosshair).
    private CrosshairStyle _style = CrosshairStyle.Cross;
    private int _arm = 10, _thickness = 2, _gap = 4;
    private string _color = "#00FF55";
    private bool _outline = true;
    public CrosshairStyle Style { get => _style; set => Set(ref _style, value); }
    /// <summary>Length of each arm (or the ring width for the circle), in pixels.</summary>
    public int ArmLength { get => _arm; set => Set(ref _arm, Math.Clamp(value, 1, 80)); }
    public int Thickness { get => _thickness; set => Set(ref _thickness, Math.Clamp(value, 1, 10)); }
    /// <summary>Empty space around the centre, in pixels.</summary>
    public int Gap { get => _gap; set => Set(ref _gap, Math.Clamp(value, 0, 60)); }
    public string Color { get => _color; set => Set(ref _color, value); }
    public bool Outline { get => _outline; set => Set(ref _outline, value); }

    // ---- look (every overlay except the crosshair)
    private string _bg = "#101014", _text = "#FFFFFF", _hi = "#3B82F6", _border = "#2E2E38";
    private double _bgOpacity = 0.85, _corner = 8;
    private bool _showBorder = true, _shadow, _labels = true;
    public string BgColor { get => _bg; set => Set(ref _bg, value); }
    public string TextColor { get => _text; set => Set(ref _text, value); }
    /// <summary>Colour of a pressed key or button.</summary>
    public string HighlightColor { get => _hi; set => Set(ref _hi, value); }
    public string BorderColor { get => _border; set => Set(ref _border, value); }
    /// <summary>How solid the box behind the overlay is (0 = none, 1 = solid).</summary>
    public double BgOpacity { get => _bgOpacity; set => Set(ref _bgOpacity, Math.Clamp(Math.Round(value, 2), 0, 1)); }
    public double CornerRadius { get => _corner; set => Set(ref _corner, Math.Clamp(Math.Round(value), 0, 24)); }
    public bool ShowBorder { get => _showBorder; set => Set(ref _showBorder, value); }
    public bool Shadow { get => _shadow; set => Set(ref _shadow, value); }
    /// <summary>Show the name in front of the number ("CPS", "FPS", "PING").</summary>
    public bool Labels { get => _labels; set => Set(ref _labels, value); }

    // ---- per kind
    private CpsLayout _cpsLayout = CpsLayout.Compact;
    private CpsSource _cpsSource = CpsSource.Both;
    private CpsButtons _cpsButtons = CpsButtons.Both;
    private bool _frameTime, _colorBySpeed = true, _counter, _space, _shift, _side, _cpsOnButtons = true, _includeClicker = true;
    public CpsLayout CpsLayout { get => _cpsLayout; set => Set(ref _cpsLayout, value); }
    public CpsSource CpsSource { get => _cpsSource; set => Set(ref _cpsSource, value); }
    public CpsButtons CpsButtons { get => _cpsButtons; set => Set(ref _cpsButtons, value); }
    /// <summary>FPS overlay: also show how long each frame takes.</summary>
    public bool ShowFrameTime { get => _frameTime; set => Set(ref _frameTime, value); }
    /// <summary>Ping overlay: green under 80 ms, yellow under 150 ms, red above.</summary>
    public bool ColorBySpeed { get => _colorBySpeed; set => Set(ref _colorBySpeed, value); }
    /// <summary>Key overlay: count how many times the key was pressed since Nighty started.</summary>
    public bool PressCounter { get => _counter; set => Set(ref _counter, value); }
    /// <summary>WASD overlay: also show the space bar and shift.</summary>
    public bool ShowSpace { get => _space; set => Set(ref _space, value); }
    public bool ShowShift { get => _shift; set => Set(ref _shift, value); }
    /// <summary>Mouse overlay: mouse 4 and mouse 5.</summary>
    public bool SideButtons { get => _side; set => Set(ref _side, value); }
    public bool CpsOnButtons { get => _cpsOnButtons; set => Set(ref _cpsOnButtons, value); }
    /// <summary>Mouse overlay: buttons also light up when the auto clicker clicks.</summary>
    public bool IncludeClicker { get => _includeClicker; set => Set(ref _includeClicker, value); }

    /// <summary>Puts every look option back to its default (position and kind-specific choices stay).</summary>
    public void ResetStyle()
    {
        BgColor = "#101014"; TextColor = "#FFFFFF"; HighlightColor = "#3B82F6"; BorderColor = "#2E2E38";
        BgOpacity = 0.85; CornerRadius = 8; ShowBorder = true; Shadow = false; Labels = true;
    }
    public void CopyStyleFrom(OverlayConfig o)
    {
        BgColor = o.BgColor; TextColor = o.TextColor; HighlightColor = o.HighlightColor; BorderColor = o.BorderColor;
        BgOpacity = o.BgOpacity; CornerRadius = o.CornerRadius; ShowBorder = o.ShowBorder; Shadow = o.Shadow; Labels = o.Labels;
    }

    [JsonIgnore] public bool IsStylable => Kind != OverlayKind.Crosshair;
    [JsonIgnore] public bool IsCustom => Kind is OverlayKind.Key or OverlayKind.Crosshair;
    [JsonIgnore] public string Glyph => Kind switch
    {
        OverlayKind.Crosshair => "",
        OverlayKind.Cps => "", OverlayKind.Fps => "", OverlayKind.Ping => "",
        OverlayKind.Wasd => "", OverlayKind.Mouse => "", _ => "",
    };
    [JsonIgnore] public string Blurb => Kind switch
    {
        OverlayKind.Cps => "Live left / right clicks per second, counted from real mouse input.",
        OverlayKind.Fps => "Roblox frame rate from Windows frame events. Needs administrator rights and a running Roblox client.",
        OverlayKind.Ping => "Round-trip time to the ping host set below (ICMP).",
        OverlayKind.Wasd => "W, A, S, D key states.",
        OverlayKind.Mouse => "Left, middle and right mouse button states.",
        OverlayKind.Hud => "Lists every macro you have switched on with its key; the ones running right now are marked.",
        OverlayKind.FishTracker => "Fish caught by Auto Fish this session, with a count for each rarity.",
        OverlayKind.Crosshair => "A crosshair drawn on top of your screen. Position 50% / 50% is the exact centre.",
        _ => $"Shows when {Hotkeys.KeyName(KeyVk)} is held.",
    };
}

public sealed class OverlaySettings : ObservableObject
{
    private string _pingHost = "1.1.1.1";
    private OverlayVisibility _visibility = OverlayVisibility.Always;
    private bool _followRoblox;
    /// <summary>When overlays show: always, while Roblox is open, or only while Roblox is the window in front.</summary>
    public OverlayVisibility Visibility { get => _visibility; set => Set(ref _visibility, value); }
    /// <summary>Position overlays inside the Roblox window instead of the whole screen (useful when it is not full screen).</summary>
    public bool FollowRobloxWindow { get => _followRoblox; set => Set(ref _followRoblox, value); }
    public string PingHost { get => _pingHost; set => Set(ref _pingHost, value); }
    public ObservableCollection<OverlayConfig> Items { get; set; } = new();
}

public sealed class MacroStep : ObservableObject
{
    private MacroStepType _type = MacroStepType.KeyPress;
    private int _value = 0x20;
    public MacroStepType Type { get => _type; set { if (Set(ref _type, value)) OnPropertyChanged(nameof(Description)); } }
    /// <summary>Virtual-key code, ClickButton index, or milliseconds depending on <see cref="Type"/>.</summary>
    public int Value { get => _value; set { if (Set(ref _value, value)) OnPropertyChanged(nameof(Description)); } }
    private int _value2 = 0, _value3 = 0;
    private string _text = "";
    /// <summary>Third number: mouse button for Auto-click, modifier keys for Key combo.</summary>
    public int Value3 { get => _value3; set { if (Set(ref _value3, value)) OnPropertyChanged(nameof(Description)); } }
    /// <summary>Second number: vertical pixels for MoveMouse, maximum milliseconds for RandomWait.</summary>
    public int Value2 { get => _value2; set { if (Set(ref _value2, value)) OnPropertyChanged(nameof(Description)); } }
    /// <summary>Text typed by TypeText, or the reminder shown by Note.</summary>
    public string Text { get => _text; set { if (Set(ref _text, value ?? "")) OnPropertyChanged(nameof(Description)); } }
    public string Description => Type switch
    {
        MacroStepType.Scroll => $"Scroll {(Value >= 0 ? "up" : "down")} {Math.Abs(Value)}",
        MacroStepType.MoveMouse => $"Move mouse {Value}, {Value2} px",
        MacroStepType.RandomWait => $"Wait {Value}–{Value2} ms",
        MacroStepType.TypeText => $"Type “{Text}”",
        MacroStepType.Note => $"Note: {Text}",
        MacroStepType.KeyPress => $"Press {Hotkeys.KeyName(Value)}",
        MacroStepType.KeyDown => $"Hold {Hotkeys.KeyName(Value)}",
        MacroStepType.KeyUp => $"Release {Hotkeys.KeyName(Value)}",
        MacroStepType.Click => $"Click {(ClickButton)Math.Clamp(Value, 0, 2)} button",
        MacroStepType.MouseDown => $"{(ClickButton)Math.Clamp(Value, 0, 2)} button down",
        MacroStepType.MouseUp => $"{(ClickButton)Math.Clamp(Value, 0, 2)} button up",
        MacroStepType.DoubleClick => $"Double click {(ClickButton)Math.Clamp(Value, 0, 2)} button",
        MacroStepType.AutoClick => $"Auto-click {(ClickButton)Math.Clamp(Value3, 0, 2)} at {Value} CPS for {Value2} ms",
        MacroStepType.KeyCombo => $"Press {Hotkeys.Format(Value, Value3)}",
        MacroStepType.MoveTo => $"Move mouse to {Value}, {Value2}",
        _ => $"Wait {Value} ms",
    };
}

public sealed class MacroDef : ObservableObject
{
    private string _name = "New macro";
    private int _repeat = 1, _vk, _mods;
    private bool _enabled = true;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>0 repeats until stopped.</summary>
    public int Repeat { get => _repeat; set => Set(ref _repeat, Math.Max(0, value)); }
    public int HotkeyVk { get => _vk; set => Set(ref _vk, value); }
    public int HotkeyMods { get => _mods; set => Set(ref _mods, value); }
    public bool HotkeyEnabled { get => _enabled; set => Set(ref _enabled, value); }
    private bool _onlyRoblox, _hold;
    private int _repeatDelay;
    private double _speed = 1;
    /// <summary>False: the hotkey toggles the macro. True: it plays while the key is held.</summary>
    public bool HoldMode { get => _hold; set => Set(ref _hold, value); }
    /// <summary>The hotkey only starts the macro while Roblox is the window in front.</summary>
    public bool OnlyInRoblox { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }
    /// <summary>Pause between repeats, in milliseconds.</summary>
    public int RepeatDelayMs { get => _repeatDelay; set => Set(ref _repeatDelay, Math.Clamp(value, 0, 600000)); }
    /// <summary>Plays every wait faster (above 1) or slower (below 1).</summary>
    public double Speed { get => _speed; set => Set(ref _speed, Math.Clamp(Math.Round(value, 2), 0.1, 10)); }
    public ObservableCollection<MacroStep> Steps { get; set; } = new();
}

public sealed class ModEntry : ObservableObject
{
    private bool _enabled = true;
    private string _name = "";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set => Set(ref _name, value); }
    public string SourcePath { get; set; } = "";
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
}

public enum BowMode { Auto, Hotkey }

public sealed class BowSwitchSettings : ObservableObject
{
    private bool _enabled, _shoot = true, _return = true, _onlyRoblox = true;
    private int _vk = 0x56, _mods;
    private double _bow = 2, _ret = 1;
    private ClickButton _button = ClickButton.Left;
    private BowMode _mode = BowMode.Auto;
    private bool _requireFighting = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    private int _toggleVk, _toggleMods;
    /// <summary>Optional key that turns Bow Switch on and off.</summary>
    public int ToggleVk { get => _toggleVk; set => Set(ref _toggleVk, value); }
    public int ToggleMods { get => _toggleMods; set => Set(ref _toggleMods, value); }
    /// <summary>Auto: runs by itself while fighting. Hotkey: runs when the trigger key is pressed.</summary>
    public BowMode Mode { get => _mode; set => Set(ref _mode, value); }
    /// <summary>Auto mode only fires while the auto clicker is clicking (manual clicks never trigger it).</summary>
    public bool OnlyWhileFighting { get => _requireFighting; set => Set(ref _requireFighting, value); }
    public int HotkeyVk { get => _vk; set => Set(ref _vk, value); }
    public int HotkeyMods { get => _mods; set => Set(ref _mods, value); }
    /// <summary>Hotbar slot (1-9) holding the bow.</summary>
    public double BowSlot { get => _bow; set => Set(ref _bow, Math.Clamp(Math.Round(value), 1, 9)); }
    public bool Shoot { get => _shoot; set => Set(ref _shoot, value); }
    public ClickButton ShootButton { get => _button; set => Set(ref _button, value); }
    private double _blockSlot;
    /// <summary>Hotbar slot (1-9) holding your blocks; Auto mode never switches to the bow while it is selected. 0 = off.</summary>
    public double BlockSlot { get => _blockSlot; set => Set(ref _blockSlot, Math.Clamp(Math.Round(value), 0, 9)); }
    public bool ReturnToSlot { get => _return; set => Set(ref _return, value); }
    private bool _returnPrev = true;
    /// <summary>Go back to the hotbar slot you were holding (sword, blocks, ...) instead of a fixed one.</summary>
    public bool ReturnToPrevious { get => _returnPrev; set => Set(ref _returnPrev, value); }
    public double ReturnSlot { get => _ret; set => Set(ref _ret, Math.Clamp(Math.Round(value), 1, 9)); }
    public bool OnlyWhenRobloxFocused { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }
}

public enum FishRarity { Common, Blue, Special, Gold, Emerald, Unknown }

public sealed class FishingSettings : ObservableObject
{
    private int _vk = 0x76, _mods;
    private bool _right = true;
    private double _cast = 700;
    private bool _skipCommon, _skipBlue, _skipSpecial, _skipGold, _skipEmerald;
    /// <summary>Skipper: when the fish on the line is one of these, jump once to dismiss it so you can fish again.</summary>
    public bool SkipCommon { get => _skipCommon; set => Set(ref _skipCommon, value); }
    public bool SkipBlue { get => _skipBlue; set => Set(ref _skipBlue, value); }
    public bool SkipSpecial { get => _skipSpecial; set => Set(ref _skipSpecial, value); }
    public bool SkipGold { get => _skipGold; set => Set(ref _skipGold, value); }
    public bool SkipEmerald { get => _skipEmerald; set => Set(ref _skipEmerald, value); }
    public bool ShouldSkip(FishRarity r) => r switch
    {
        FishRarity.Common => _skipCommon, FishRarity.Blue => _skipBlue, FishRarity.Special => _skipSpecial,
        FishRarity.Gold => _skipGold, FishRarity.Emerald => _skipEmerald, _ => false,
    };
    /// <summary>Key that starts and stops auto fishing (default F7).</summary>
    public int HotkeyVk { get => _vk; set => Set(ref _vk, value); }
    public int HotkeyMods { get => _mods; set => Set(ref _mods, value); }
    /// <summary>True when holding the mouse moves your green box to the right. Flip it if the box runs the wrong way.</summary>
    public bool HoldMovesRight { get => _right; set => Set(ref _right, value); }
    /// <summary>How long to hold the mouse to throw the rod.</summary>
    public double CastHoldMs { get => _cast; set => Set(ref _cast, Math.Clamp(Math.Round(value), 10, 3000)); }
}

public sealed class GeneralSettings : ObservableObject
{
    private bool _topmost, _notify = true, _restoreClose = true;
    /// <summary>Put Windows back to normal (pointer, keyboard/accessibility, PC tweaks, Game Mode) when Nighty closes.</summary>
    public bool RestoreOnClose { get => _restoreClose; set => Set(ref _restoreClose, value); }
    /// <summary>Brief bottom-right notification when something is toggled.</summary>
    public bool ShowNotifications { get => _notify; set => Set(ref _notify, value); }
    private int _stopVk = 0x78, _stopMods;
    private bool _startMin;
    private string _theme = "midnight";
    /// <summary>Theme id (midnight, light, cyberpunk, monochrome, ocean, forest, sunset, sakura). Old accent-only ids are migrated.</summary>
    public string Theme { get => _theme; set => Set(ref _theme, value ?? "midnight"); }

    private string? _accentOverride;
    /// <summary>Accent colour picked on top of the theme (#RRGGBB), or null to use the theme's own.</summary>
    public string? AccentOverride { get => _accentOverride; set => Set(ref _accentOverride, value); }
    /// <summary>Per-colour overrides that survive theme changes: Accent, Accent2, Bg, Sidebar, Cards, Text, Muted, Borders.</summary>
    public Dictionary<string, string> CustomColors { get; set; } = new();

    private bool _anim = true, _sounds;
    /// <summary>Smooth motion everywhere. Off makes every change instant.</summary>
    public bool Animations { get => _anim; set => Set(ref _anim, value); }
    /// <summary>Little sounds for button presses and for starting or stopping the clicker.</summary>
    public bool SoundEffects { get => _sounds; set => Set(ref _sounds, value); }
    private string _font = "";
    /// <summary>Font used by the whole interface. Empty = Segoe UI Variable.</summary>
    public string FontName { get => _font; set => Set(ref _font, value ?? ""); }

    private string _bgImage = "";
    private double _bgStrength = 0.35;
    private bool _bgFit;
    /// <summary>Picture drawn behind the window (PNG, JPG or GIF), copied into the app data folder.</summary>
    public string BackgroundImage { get => _bgImage; set => Set(ref _bgImage, value ?? ""); }
    public double BackgroundStrength { get => _bgStrength; set => Set(ref _bgStrength, Math.Clamp(Math.Round(value, 2), 0.05, 1)); }
    /// <summary>False = fill the window, true = fit the whole picture.</summary>
    public bool BackgroundFit { get => _bgFit; set => Set(ref _bgFit, value); }

    private bool _remember = true;
    private string _lastPage = "";
    private double _winL, _winT, _winW, _winH;
    private bool _winMax;
    /// <summary>Open on the same page and in the same place.</summary>
    public bool RememberWindow { get => _remember; set => Set(ref _remember, value); }
    public string LastPage { get => _lastPage; set => Set(ref _lastPage, value ?? ""); }
    public double WindowLeft { get => _winL; set => Set(ref _winL, value); }
    public double WindowTop { get => _winT; set => Set(ref _winT, value); }
    public double WindowWidth { get => _winW; set => Set(ref _winW, value); }
    public double WindowHeight { get => _winH; set => Set(ref _winH, value); }
    public bool WindowMaximized { get => _winMax; set => Set(ref _winMax, value); }

    private bool _splash = true;
    /// <summary>Show the loading screen while Nighty starts.</summary>
    public bool ShowSplash { get => _splash; set => Set(ref _splash, value); }

    private PrecisionMode _precision = PrecisionMode.Balanced;
    /// <summary>How exactly the clicker lands each click, traded against CPU use.</summary>
    public PrecisionMode Precision { get => _precision; set => Set(ref _precision, value); }

    private bool _discord = false, _dActivity = true, _dTime = true;
    private string _discordId = "";
    /// <summary>Show "Using Nighty" on your Discord profile. Needs your own Discord application id.</summary>
    public bool DiscordPresence { get => _discord; set => Set(ref _discord, value); }
    public string DiscordAppId { get => _discordId; set => Set(ref _discordId, (value ?? "").Trim()); }
    public bool DiscordShowActivity { get => _dActivity; set => Set(ref _dActivity, value); }
    public bool DiscordShowTime { get => _dTime; set => Set(ref _dTime, value); }
    /// <summary>Open in the taskbar instead of on screen (used with Start with Windows, and by the --minimized flag).</summary>
    public bool StartMinimized { get => _startMin; set => Set(ref _startMin, value); }
    public bool AlwaysOnTop { get => _topmost; set => Set(ref _topmost, value); }
    public int StopHotkeyVk { get => _stopVk; set => Set(ref _stopVk, value); }
    public int StopHotkeyMods { get => _stopMods; set => Set(ref _stopMods, value); }
}

/// <summary>Original system values captured before Nighty changes them, so they can be restored.</summary>
public sealed class SystemBackups
{
    public bool GameModeActive { get; set; }
    public string? OriginalPowerScheme { get; set; }
    /// <summary>PC tweaks currently switched on, and the original value of every setting they changed ("id|subkey|name" to "N" / "I:n" / "S:text").</summary>
    public HashSet<string> TweaksApplied { get; set; } = new();
    /// <summary>Tweaks that were switched on when Nighty last closed; they are switched back on at the next start.</summary>
    public HashSet<string> TweaksToReapply { get; set; } = new();
    public Dictionary<string, string> Tweaks { get; set; } = new();

    public bool HasGameBarBackup { get; set; }
    public int? OriginalGameBarValue { get; set; }

    public bool HasMovementBackup { get; set; }
    public uint StickyFlags { get; set; }
    public uint FilterFlags { get; set; }
    public uint ToggleFlags { get; set; }
    public int KeyboardDelay { get; set; }
    public int KeyboardSpeed { get; set; }

    public int ScrollLinesOriginal { get; set; } = -1;
    public int DoubleClickOriginal { get; set; } = -1;
    public bool HasPointerBackup { get; set; }
    public int PointerSpeed { get; set; }
    public int[] MouseParams { get; set; } = Array.Empty<int>();

    public DnsBackup? Dns { get; set; }
    public Dictionary<string, int> BrightnessOriginal { get; set; } = new();
}

public sealed class DnsBackup
{
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public int InterfaceIndex { get; set; }
    public bool WasStatic { get; set; }
    public List<string> Servers { get; set; } = new();
}

public sealed class AppSettings
{
    public GeneralSettings General { get; set; } = new();
    public ClickerSettings Clicker { get; set; } = new();
    public ObservableCollection<ClickerPreset> Presets { get; set; } = new();
    public GameSettings Game { get; set; } = new();
    public BowSwitchSettings Bow { get; set; } = new();
    public FishingSettings Fishing { get; set; } = new();
    public UtilitySettings Utility { get; set; } = new();
    public OverlaySettings Overlays { get; set; } = new();
    public ObservableCollection<MacroDef> Macros { get; set; } = new();
    public ObservableCollection<ModEntry> Mods { get; set; } = new();
    public ObservableCollection<SlotMacroConfig> SlotMacros { get; set; } = new();
    public RobloxSettings Roblox { get; set; } = new();
    public RecordSettings Record { get; set; } = new();
    public SystemBackups Backups { get; set; } = new();
}

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Nighty.Mvvm;

namespace Nighty.Models;

public enum ClickButton { Left, Right, Middle }
public enum ActivationMode { Toggle, Hold }
public enum OverlayKind { Cps, Fps, Ping, Wasd, Mouse, Key }
public enum SocdMode { LastInput, Neutral, FirstInput }
public enum MacroStepType { KeyPress, KeyDown, KeyUp, Click, Wait }

public sealed class ClickerSettings : ObservableObject
{
    private bool _enabled;
    private double _cps = 12, _minCps = 10, _maxCps = 14;
    private bool _useRange, _onlyRoblox;
    private ClickButton _button = ClickButton.Left;
    private int _duty = 50, _vk = 0x75, _mods;
    private ActivationMode _mode = ActivationMode.Toggle;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double Cps { get => _cps; set => Set(ref _cps, Math.Clamp(Math.Round(value, 1), 1, 35)); }
    public bool UseRange { get => _useRange; set => Set(ref _useRange, value); }
    public double MinCps
    {
        get => _minCps;
        set { if (Set(ref _minCps, Math.Clamp(Math.Round(value, 1), 1, 35)) && _minCps > _maxCps) MaxCps = _minCps; }
    }
    public double MaxCps
    {
        get => _maxCps;
        set { if (Set(ref _maxCps, Math.Clamp(Math.Round(value, 1), 1, 35)) && _maxCps < _minCps) MinCps = _maxCps; }
    }
    public ClickButton Button { get => _button; set => Set(ref _button, value); }
    public int DutyCycle { get => _duty; set => Set(ref _duty, Math.Clamp(value, 5, 95)); }
    public ActivationMode Mode { get => _mode; set => Set(ref _mode, value); }
    public int HotkeyVk { get => _vk; set => Set(ref _vk, value); }
    public int HotkeyMods { get => _mods; set => Set(ref _mods, value); }
    public bool OnlyWhenRobloxFocused { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }

    public void CopyFrom(ClickerSettings o)
    {
        Cps = o.Cps; UseRange = o.UseRange; MinCps = o.MinCps; MaxCps = o.MaxCps; Button = o.Button;
        DutyCycle = o.DutyCycle; Mode = o.Mode; HotkeyVk = o.HotkeyVk; HotkeyMods = o.HotkeyMods;
        OnlyWhenRobloxFocused = o.OnlyWhenRobloxFocused;
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
    private bool _power = true, _priority = true, _timer = true, _gameMode = true, _awake;
    public bool HighPerformancePower { get => _power; set => Set(ref _power, value); }
    public bool RobloxHighPriority { get => _priority; set => Set(ref _priority, value); }
    public bool TimerResolution { get => _timer; set => Set(ref _timer, value); }
    public bool WindowsGameMode { get => _gameMode; set => Set(ref _gameMode, value); }
    public bool KeepDisplayAwake { get => _awake; set => Set(ref _awake, value); }
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
}

public sealed class DnsSettings : ObservableObject
{
    private string? _adapter;
    private bool _autoBest;
    public string? AdapterId { get => _adapter; set => Set(ref _adapter, value); }
    public bool AutoApplyBest { get => _autoBest; set => Set(ref _autoBest, value); }
}

public sealed class QosSettings : ObservableObject
{
    private int _dscp = 46;
    public int Dscp { get => _dscp; set => Set(ref _dscp, value); }
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

    [JsonIgnore] public bool IsCustom => Kind == OverlayKind.Key;
    [JsonIgnore] public string Glyph => Kind switch
    {
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
        _ => $"Shows when {Hotkeys.KeyName(KeyVk)} is held.",
    };
}

public sealed class OverlaySettings : ObservableObject
{
    private string _pingHost = "1.1.1.1";
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
    public string Description => Type switch
    {
        MacroStepType.KeyPress => $"Press {Hotkeys.KeyName(Value)}",
        MacroStepType.KeyDown => $"Hold {Hotkeys.KeyName(Value)}",
        MacroStepType.KeyUp => $"Release {Hotkeys.KeyName(Value)}",
        MacroStepType.Click => $"Click {(ClickButton)Math.Clamp(Value, 0, 2)} button",
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
    private double _bow = 2, _ret = 1, _switchDelay = 60, _hold = 80, _returnDelay = 120;
    private ClickButton _button = ClickButton.Left;
    private BowMode _mode = BowMode.Auto;
    private double _cooldown = 1250;
    private bool _requireFighting = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>Auto: runs by itself while fighting. Hotkey: runs when the trigger key is pressed.</summary>
    public BowMode Mode { get => _mode; set => Set(ref _mode, value); }
    /// <summary>Crossbow cooldown. Auto mode never starts a new switch sooner than this after the last one began.</summary>
    public double CooldownMs { get => _cooldown; set => Set(ref _cooldown, Math.Clamp(Math.Round(value), 100, 5000)); }
    /// <summary>Auto mode only fires while the auto clicker is clicking (manual clicks never trigger it).</summary>
    public bool OnlyWhileFighting { get => _requireFighting; set => Set(ref _requireFighting, value); }
    public int HotkeyVk { get => _vk; set => Set(ref _vk, value); }
    public int HotkeyMods { get => _mods; set => Set(ref _mods, value); }
    /// <summary>Hotbar slot (1-9) holding the bow.</summary>
    public double BowSlot { get => _bow; set => Set(ref _bow, Math.Clamp(Math.Round(value), 1, 9)); }
    /// <summary>Wait between selecting the bow and the shot, so the equip animation/cooldown can finish.</summary>
    public double SwitchDelayMs { get => _switchDelay; set => Set(ref _switchDelay, Math.Clamp(Math.Round(value), 0, 1000)); }
    public bool Shoot { get => _shoot; set => Set(ref _shoot, value); }
    public ClickButton ShootButton { get => _button; set => Set(ref _button, value); }
    public double HoldMs { get => _hold; set => Set(ref _hold, Math.Clamp(Math.Round(value), 10, 1000)); }
    public bool ReturnToSlot { get => _return; set => Set(ref _return, value); }
    public double ReturnSlot { get => _ret; set => Set(ref _ret, Math.Clamp(Math.Round(value), 1, 9)); }
    public double ReturnDelayMs { get => _returnDelay; set => Set(ref _returnDelay, Math.Clamp(Math.Round(value), 0, 2000)); }
    public bool OnlyWhenRobloxFocused { get => _onlyRoblox; set => Set(ref _onlyRoblox, value); }
}

public sealed class GeneralSettings : ObservableObject
{
    private bool _topmost;
    private int _stopVk = 0x78, _stopMods;
    public bool AlwaysOnTop { get => _topmost; set => Set(ref _topmost, value); }
    public int StopHotkeyVk { get => _stopVk; set => Set(ref _stopVk, value); }
    public int StopHotkeyMods { get => _stopMods; set => Set(ref _stopMods, value); }
}

/// <summary>Original system values captured before Nighty changes them, so they can be restored.</summary>
public sealed class SystemBackups
{
    public bool GameModeActive { get; set; }
    public string? OriginalPowerScheme { get; set; }
    public bool HasGameBarBackup { get; set; }
    public int? OriginalGameBarValue { get; set; }

    public bool HasMovementBackup { get; set; }
    public uint StickyFlags { get; set; }
    public uint FilterFlags { get; set; }
    public uint ToggleFlags { get; set; }
    public int KeyboardDelay { get; set; }
    public int KeyboardSpeed { get; set; }

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
    public UtilitySettings Utility { get; set; } = new();
    public OverlaySettings Overlays { get; set; } = new();
    public ObservableCollection<MacroDef> Macros { get; set; } = new();
    public ObservableCollection<ModEntry> Mods { get; set; } = new();
    public SystemBackups Backups { get; set; } = new();
}

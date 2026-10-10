using System.Collections.ObjectModel;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed class CombatViewModel : ObservableObject
{
    private string _presetName = "";
    private string _measured = "—";
    private string _state = "Off";
    private StatusKind _kind;
    private string _presetMessage = "";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public ClickerSettings Settings => Svc.S.Clicker;
    public ObservableCollection<ClickerPreset> Presets => Svc.S.Presets;

    public double ClicksPerHit { get => Settings.ClicksPerHit; set { Settings.ClicksPerHit = (int)value; OnPropertyChanged(); } }
    public double StopAfterClicks { get => Settings.StopAfterClicks; set { Settings.StopAfterClicks = (int)Math.Round(value); OnPropertyChanged(); OnPropertyChanged(nameof(AutoStopHint)); } }
    public double TimeLimitSec { get => Settings.TimeLimitSec; set { Settings.TimeLimitSec = (int)Math.Round(value); OnPropertyChanged(); OnPropertyChanged(nameof(AutoStopHint)); } }
    public double StartDelaySec { get => Settings.StartDelayMs / 1000.0; set { Settings.StartDelayMs = (int)Math.Round(value * 1000); OnPropertyChanged(); OnPropertyChanged(nameof(AutoStopHint)); } }
    public string AutoStopHint
    {
        get
        {
            var parts = new List<string>();
            if (Settings.StartDelayMs > 0) parts.Add($"The first click comes {Settings.StartDelayMs / 1000.0:0.0} s after you start.");
            if (Settings.StopAfterClicks > 0) parts.Add($"Each start sends {Settings.StopAfterClicks:N0} click{(Settings.StopAfterClicks == 1 ? "" : "s")}, then stops.");
            if (Settings.TimeLimitSec > 0) parts.Add($"Stops by itself after {Settings.TimeLimitSec} s.");
            return parts.Count == 0 ? "0 means never. Set a number of clicks for bursts, or a time limit." : string.Join(" ", parts);
        }
    }
    public string EngineText => Svc.Clicker.IsClicking && Svc.Clicker.EngineInfo.Length > 0 ? Svc.Clicker.EngineInfo
        : Settings.HitFix ? "HitFix is on. It takes effect every time the clicker starts." : "Standard timing. Turn HitFix on for steadier clicks.";
    // ---- hero card
    private bool _manualRun;
    public string ChipText => Svc.Clicker.IsClicking ? "CLICKING" : Settings.Enabled ? "ARMED" : "IDLE";
    public string HeroTitle => Svc.Clicker.IsClicking ? "Clicking" : "Ready";
    private string CpsLabel => Settings.UseRange ? $"{Settings.MinCps:0.##}-{Settings.MaxCps:0.##}" : $"{Settings.Cps:0.00}";
    private string KeyLabel => Settings.HotkeyVk > 0 ? Hotkeys.Format(Settings.HotkeyVk, Settings.HotkeyMods) : "no key set";
    public string HeroSummary => $"{Settings.Button} button · {CpsLabel} CPS · {(Settings.Mode == ActivationMode.Hold ? "Hold" : "Toggle")} {KeyLabel}";
    public string TargetValue => CpsLabel;
    public string MeasuredValue => Svc.Clicker.IsClicking ? $"{Svc.Clicker.MeasuredCps:0}" : "-";
    public string DutyValue => $"{Settings.DutyCycle:0.0}";
    public string ButtonValue => Settings.Button.ToString();
    public string ModeValue => Settings.Mode.ToString();
    public string ClicksValue => Svc.Clicker.TotalClicks.ToString("N0");
    public string StartLabel => Svc.Clicker.IsClicking ? "Stop clicking" : "Start clicking";
    public string StartGlyph => Svc.Clicker.IsClicking ? "" : "";
    public string StartHint => Settings.Mode == ActivationMode.Hold ? $"or hold {KeyLabel}" : $"or press {KeyLabel}";
    public string CpsText => $"{Settings.Cps:0.00}";
    public string PeriodText => $"{1000.0 / Math.Max(1, Settings.Cps):0.00} ms between clicks";
    public string DutyText => $"{Settings.DutyCycle:0.0}";
    public RelayCommand StartCommand { get; }
    public RelayCommand CpsUp { get; }
    public RelayCommand CpsDown { get; }
    public RelayCommand DutyUp { get; }
    public RelayCommand DutyDown { get; }

    private void RefreshHero()
    {
        foreach (var n in new[] { nameof(ChipText), nameof(HeroTitle), nameof(HeroSummary), nameof(TargetValue), nameof(MeasuredValue), nameof(DutyValue),
                                  nameof(ButtonValue), nameof(ModeValue), nameof(ClicksValue), nameof(StartLabel), nameof(StartGlyph), nameof(StartHint),
                                  nameof(CpsText), nameof(PeriodText), nameof(DutyText), nameof(EngineText) })
            OnPropertyChanged(n);
    }

    public string PresetName { get => _presetName; set => Set(ref _presetName, value); }
    public string PresetMessage { get => _presetMessage; private set => Set(ref _presetMessage, value); }
    public string MeasuredCps { get => _measured; private set => Set(ref _measured, value); }
    public string StateText { get => _state; private set => Set(ref _state, value); }
    public StatusKind StateKind { get => _kind; private set => Set(ref _kind, value); }
    public string HotkeyHint => Settings.Mode == ActivationMode.Toggle
        ? $"Press {Hotkeys.Format(Settings.HotkeyVk, Settings.HotkeyMods)} to start and stop clicking."
        : $"Hold {Hotkeys.Format(Settings.HotkeyVk, Settings.HotkeyMods)} to click; release to stop.";

    private bool _calibrating;
    private string _calMessage = "";
    public bool IsCalibrating { get => _calibrating; private set { Set(ref _calibrating, value); Calibrate.Refresh(); } }
    public string CalibrationMessage { get => _calMessage; private set => Set(ref _calMessage, value); }
    public RelayCommand Calibrate { get; }

    private async void DoCalibrate()
    {
        if (Svc.Clicker.IsClicking) { CalibrationMessage = "Stop the auto clicker first."; return; }
        IsCalibrating = true;
        try
        {
            var progress = new Progress<string>(m => CalibrationMessage = m);
            var r = await Task.Run(() => CalibrationService.Run(progress, CancellationToken.None));
            if (Settings.UseRange) { Settings.MaxCps = r.BestCps; Settings.MinCps = Math.Max(1, r.BestCps - 3); }
            else Settings.Cps = r.BestCps;
            CalibrationMessage = r.Summary;
        }
        catch (Exception ex) { Log.Error("Calibration failed", ex); CalibrationMessage = "Calibration failed."; }
        finally { IsCalibrating = false; }
    }

    public RelayCommand SavePreset { get; }
    public RelayCommand LoadPreset { get; }
    public RelayCommand DeletePreset { get; }

    public CombatViewModel()
    {
        Calibrate = new RelayCommand(DoCalibrate, () => !IsCalibrating);
        StartCommand = new RelayCommand(() =>
        {
            if (Svc.Clicker.IsClicking) { Svc.Clicker.Stop(); return; }
            _manualRun = true;   // started from the button, so hold mode does not need the key held
            Svc.Clicker.Start();
            Svc.Toast.Toggled("Auto Clicker", true);
        });
        CpsUp = new RelayCommand(() => Settings.Cps += 0.5);
        CpsDown = new RelayCommand(() => Settings.Cps -= 0.5);
        DutyUp = new RelayCommand(() => Settings.DutyCycle = Math.Min(95, Settings.DutyCycle + 5));
        DutyDown = new RelayCommand(() => Settings.DutyCycle = Math.Max(5, Settings.DutyCycle - 5));
        SavePreset = new RelayCommand(DoSave, () => !string.IsNullOrWhiteSpace(PresetName));
        LoadPreset = new RelayCommand(p =>
        {
            if (p is not ClickerPreset pr) return;
            Settings.CopyFrom(pr.Data);
            PresetMessage = $"Loaded “{pr.Name}”.";
        });
        DeletePreset = new RelayCommand(p =>
        {
            if (p is not ClickerPreset pr) return;
            Presets.Remove(pr);
            PresetMessage = $"Deleted “{pr.Name}”.";
        });

        Svc.Clicker.KeepClicking = () => _manualRun || Settings.Mode != ActivationMode.Hold
            || HotkeyService.IsDown((Settings.HotkeyVk, Settings.HotkeyMods));
        Svc.Hotkeys.Register("clicker", () => (Settings.HotkeyVk, Settings.HotkeyMods), OnHotkey);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClickerSettings.Enabled) && !Settings.Enabled) Svc.Clicker.Stop();
            if (e.PropertyName == nameof(ClickerSettings.Mode)) Svc.Clicker.Stop();
            if (e.PropertyName is nameof(ClickerSettings.Mode) or nameof(ClickerSettings.HotkeyVk) or nameof(ClickerSettings.HotkeyMods))
                OnPropertyChanged(nameof(HotkeyHint));
            RefreshHero();
        };
        Svc.Clicker.StateChanged += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(UpdateState);
        Svc.Clicker.AutoStopped += reason => Svc.Toast.Show("Auto Clicker", reason, true);
        Svc.Clicker.Blocked += () => Svc.Toast.Show("Auto Clicker", "Windows blocked the clicks. The game may be running as administrator: restart Nighty as administrator.", false);

        _timer.Tick += (_, _) => UpdateState();
        _timer.Start();
        UpdateState();
    }

    private void OnHotkey(bool down)
    {
        if (!Settings.Enabled) return;
        if (Settings.Mode == ActivationMode.Toggle)
        {
            if (!down) return;
            if (Svc.Clicker.IsClicking) Svc.Clicker.Stop(); else Svc.Clicker.Start();
            Svc.Toast.Toggled("Auto Clicker", Svc.Clicker.IsClicking);
        }
        else
        {
            if (down) Svc.Clicker.Start(); else Svc.Clicker.Stop();
        }
    }

    private void DoSave()
    {
        var name = PresetName.Trim();
        var data = Settings.Clone();
        var existing = Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            int idx = Presets.IndexOf(existing);
            Presets[idx] = new ClickerPreset { Name = existing.Name, Data = data };   // replace so the summary refreshes
            PresetMessage = $"Updated “{existing.Name}”.";
        }
        else
        {
            Presets.Add(new ClickerPreset { Name = name, Data = data });
            PresetMessage = $"Saved “{name}”.";
        }
        PresetName = "";
    }

    private void UpdateState()
    {
        if (!Svc.Clicker.IsClicking) _manualRun = false;
        RefreshHero();
        if (Svc.Clicker.IsClicking)
        {
            StateText = RobloxService.IsOwnWindowForeground() ? "Paused while Nighty is focused" : "Clicking";
            StateKind = StatusKind.Success;
            MeasuredCps = $"{Svc.Clicker.MeasuredCps:0} CPS";
        }
        else
        {
            StateText = Settings.Enabled ? "Armed — waiting for hotkey" : "Off";
            StateKind = Settings.Enabled ? StatusKind.Info : StatusKind.Neutral;
            MeasuredCps = "—";
        }
        SavePreset.Refresh();
    }
}

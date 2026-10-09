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

    public string PresetName { get => _presetName; set => Set(ref _presetName, value); }
    public string PresetMessage { get => _presetMessage; private set => Set(ref _presetMessage, value); }
    public string MeasuredCps { get => _measured; private set => Set(ref _measured, value); }
    public string StateText { get => _state; private set => Set(ref _state, value); }
    public StatusKind StateKind { get => _kind; private set => Set(ref _kind, value); }
    public string HotkeyHint => Settings.Mode == ActivationMode.Toggle
        ? $"Press {Hotkeys.Format(Settings.HotkeyVk, Settings.HotkeyMods)} to start and stop clicking."
        : $"Hold {Hotkeys.Format(Settings.HotkeyVk, Settings.HotkeyMods)} to click; release to stop.";

    public RelayCommand SavePreset { get; }
    public RelayCommand LoadPreset { get; }
    public RelayCommand DeletePreset { get; }

    public CombatViewModel()
    {
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

        Svc.Hotkeys.Register("clicker", () => (Settings.HotkeyVk, Settings.HotkeyMods), OnHotkey);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClickerSettings.Enabled) && !Settings.Enabled) Svc.Clicker.Stop();
            if (e.PropertyName == nameof(ClickerSettings.Mode)) Svc.Clicker.Stop();
            if (e.PropertyName is nameof(ClickerSettings.Mode) or nameof(ClickerSettings.HotkeyVk) or nameof(ClickerSettings.HotkeyMods))
                OnPropertyChanged(nameof(HotkeyHint));
        };
        Svc.Clicker.StateChanged += () => System.Windows.Application.Current.Dispatcher.BeginInvoke(UpdateState);

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

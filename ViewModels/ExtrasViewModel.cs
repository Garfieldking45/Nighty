using System.Windows;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;

namespace Nighty.ViewModels;

public sealed class ExtrasViewModel : ObservableObject
{
    private string _message = "";
    private readonly System.Windows.Threading.DispatcherTimer _auto = new() { Interval = TimeSpan.FromMilliseconds(15) };
    public BowSwitchSettings Bow => Svc.S.Bow;
    public FishingSettings Fishing => Svc.S.Fishing;
    public System.Collections.ObjectModel.ObservableCollection<SlotMacroConfig> QuickMacros => Svc.S.SlotMacros;
    public string FishStatus => Svc.Fishing.IsRunning ? "Fishing: " + Svc.Fishing.Status : "Auto fish is off.";
    public string FishButton => Svc.Fishing.IsRunning ? "Stop fishing" : "Start fishing";
    public string FishCounts => Svc.Fishing.TrackerText.Replace("\n", " — ");
    public RelayCommand FishCommand { get; }
    public RelayCommand ResetFishCommand { get; }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Summary
    {
        get
        {
            var b = Bow;
            var s = b.Mode == BowMode.Auto
                ? $"Automatic{(b.OnlyWhileFighting ? " while fighting" : "")}, → slot {b.BowSlot:0}"
                : $"Press {Hotkeys.Format(b.HotkeyVk, b.HotkeyMods)} → slot {b.BowSlot:0}";
            if (b.Shoot) s += $" → {b.ShootButton} click";
            if (b.ReturnToSlot) s += $" → back to your previous slot";
            return s;
        }
    }
    private static void RegisterQuick(SlotMacroConfig m)
    {
        Svc.Hotkeys.Register("quick:" + m.Kind, () => m.Enabled ? (m.HotkeyVk, m.HotkeyMods) : (0, 0), down =>
        {
            switch (m.Style)
            {
                case SlotMacroStyle.Hold: if (down) Svc.SlotMacros.Start(m); else Svc.SlotMacros.Stop(m.Kind); break;
                case SlotMacroStyle.Toggle:
                    if (down) { Svc.SlotMacros.Toggle(m); Svc.Toast.Toggled(m.Title, Svc.SlotMacros.IsRunning(m.Kind)); }
                    break;
                default: if (down) Svc.SlotMacros.Start(m); break;
            }
        });
    }

    public string LastRun => Svc.Bow.LastRunMs > 0 ? $"Last run took {Svc.Bow.LastRunMs:0} ms end to end." : "Not run yet.";
    public RelayCommand TestCommand { get; }

    public ExtrasViewModel()
    {
        TestCommand = new RelayCommand(() =>
        {
            Svc.Bow.Trigger(3000);
            Message = "Running in 3 seconds — click into Roblox now.";
        });
        Svc.Hotkeys.Register("bow", () => Bow.Enabled && Bow.Mode == BowMode.Hotkey ? (Bow.HotkeyVk, Bow.HotkeyMods) : (0, 0), down => { if (down) Svc.Bow.Trigger(); });
        Svc.Hotkeys.Register("bow-toggle", () => (Bow.ToggleVk, Bow.ToggleMods), down =>
        {
            if (!down) return;
            Bow.Enabled = !Bow.Enabled;
            Svc.Toast.Toggled("Bow Switch", Bow.Enabled);
        });
        foreach (var kind in Enum.GetValues<SlotMacroKind>())
            if (!QuickMacros.Any(m => m.Kind == kind))
            {
                var made = SlotMacroConfig.Create(kind);
                if (kind == SlotMacroKind.Crossbow) QuickMacros.Insert(0, made); else QuickMacros.Add(made);
            }
        foreach (var m in QuickMacros.ToList()) RegisterQuick(m);
        ResetFishCommand = new RelayCommand(() => Svc.Fishing.ResetStats());
        FishCommand = new RelayCommand(() => { Svc.Fishing.Toggle(); Svc.Toast.Toggled("Auto Fish", Svc.Fishing.IsRunning); });
        Svc.Hotkeys.Register("fish", () => (Fishing.HotkeyVk, Fishing.HotkeyMods), down =>
        {
            if (!down) return;
            Svc.Fishing.Toggle();
            Svc.Toast.Toggled("Auto Fish", Svc.Fishing.IsRunning);
        });
        Svc.Fishing.Changed += () => Application.Current.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(FishStatus)); OnPropertyChanged(nameof(FishButton)); OnPropertyChanged(nameof(FishCounts)); });
        _auto.Tick += (_, _) => { if (Bow.Enabled && Bow.Mode == BowMode.Auto && !Svc.Hotkeys.Suspended) Svc.Bow.TryAuto(); };
        _auto.Start();
        Bow.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Summary));
        Svc.Bow.Changed += () => Application.Current.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(LastRun)); if (!Svc.Bow.IsRunning) Message = ""; });
    }
}

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
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Summary
    {
        get
        {
            var b = Bow;
            var s = b.Mode == BowMode.Auto
                ? $"Automatic{(b.OnlyWhileFighting ? " while fighting" : "")}, at most every {b.CooldownMs:0} ms → slot {b.BowSlot:0}"
                : $"Press {Hotkeys.Format(b.HotkeyVk, b.HotkeyMods)} → slot {b.BowSlot:0}";
            if (b.Shoot) s += $" → wait {b.SwitchDelayMs:0} ms → {b.ShootButton} click ({b.HoldMs:0} ms)";
            if (b.ReturnToSlot) s += $" → wait {(b.Shoot ? b.ReturnDelayMs : b.SwitchDelayMs):0} ms → back to slot {b.ReturnSlot:0}";
            return s;
        }
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
        _auto.Tick += (_, _) => { if (Bow.Enabled && Bow.Mode == BowMode.Auto && !Svc.Hotkeys.Suspended) Svc.Bow.TryAuto(); };
        _auto.Start();
        Bow.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Summary));
        Svc.Bow.Changed += () => Application.Current.Dispatcher.BeginInvoke(() => { OnPropertyChanged(nameof(LastRun)); if (!Svc.Bow.IsRunning) Message = ""; });
    }
}

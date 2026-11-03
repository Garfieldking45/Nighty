using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

// ============================================================ Tracking helper

public sealed class TrackingViewModel : ObservableObject
{
    private string _message = "";
    private StatusKind _kind = StatusKind.Info;
    private string _current = "";

    public TrackingSettings Settings => Svc.S.Utility.Tracking;

    public double SlowSpeed { get => Settings.SlowSpeed; set { Settings.SlowSpeed = (int)value; OnPropertyChanged(); } }

    private string _liveText = "";
    /// <summary>What the slowdown sees right now, so a helper that does nothing always says why.</summary>
    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    private void RefreshLive()
    {
        int slot = Svc.Bow.CurrentSlot;
        LiveText = $"Right now: hotbar slot {(slot == 0 ? "unknown (press its number key once)" : slot.ToString())}  ·  Roblox {(Svc.Roblox.IsForeground ? "in front" : "not in front")}  ·  Auto Clicker {(Svc.Clicker.IsClicking ? "clicking" : "idle")}  ·  pointer {(Svc.Pointer.IsSlowed ? "slowed" : "normal")} ({Svc.Pointer.ReadCurrent().Speed}/20)";
    }

    public double ScrollLines { get => Settings.ScrollLines; set { Settings.ScrollLines = (int)value; OnPropertyChanged(); } }
    public double DoubleClickMs { get => Settings.DoubleClickMs; set { Settings.DoubleClickMs = (int)value; OnPropertyChanged(); } }
    public double Speed { get => Settings.PointerSpeed; set { Settings.PointerSpeed = (int)value; OnPropertyChanged(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public string CurrentText { get => _current; private set => Set(ref _current, value); }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand LoadCurrentCommand { get; }
    public RelayCommand PresetCommand { get; }

    public double Dpi { get => Settings.MouseDpi; set { Settings.MouseDpi = (int)value; OnPropertyChanged(); OnPropertyChanged(nameof(CalcText)); } }
    public double Sensitivity { get => Settings.GameSensitivity; set { Settings.GameSensitivity = value; OnPropertyChanged(); OnPropertyChanged(nameof(CalcText)); } }

    // Windows' pointer-speed steps: with "Enhance pointer precision" off, this is how far the cursor moves per mouse count.
    private static readonly double[] SpeedScale = { 0.03125, 0.0625, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875, 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.25, 3.5 };
    public string CalcText
    {
        get
        {
            double edpi = Settings.MouseDpi * Settings.GameSensitivity;
            double mult = SpeedScale[Math.Clamp(Settings.PointerSpeed, 1, 20) - 1];
            return $"eDPI: {edpi:0.##}  ·  Windows cursor moves ×{mult:0.###} per mouse count at pointer speed {Settings.PointerSpeed}";
        }
    }

    public TrackingViewModel()
    {
        ApplyCommand = new RelayCommand(() =>
        {
            Message = Svc.Pointer.Apply(Settings.PointerSpeed, Settings.EnhancePointerPrecision);
            Kind = Message.Contains("verified") ? StatusKind.Success : StatusKind.Warning;
            Refresh();
        });
        RestoreCommand = new RelayCommand(() =>
        {
            Message = Svc.Pointer.Restore(); Kind = StatusKind.Success;
            LoadCurrent();
        }, () => Svc.Pointer.HasBackup);
        LoadCurrentCommand = new RelayCommand(LoadCurrent);
        PresetCommand = new RelayCommand(o =>
        {
            if (o is not string s || !int.TryParse(s, out int v)) return;
            Speed = v; Settings.EnhancePointerPrecision = false;
            Message = $"Pointer speed set to {v} with acceleration off. Press Apply to use it."; Kind = StatusKind.Info;
        });
        Settings.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CalcText));
        RefreshLive();
        new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => RefreshLive(), Dispatcher.CurrentDispatcher).Start();
        Refresh();
    }

    private void LoadCurrent()
    {
        var c = Svc.Pointer.ReadCurrent();
        Speed = c.Speed; Settings.EnhancePointerPrecision = c.Precision;
        Refresh();
    }

    private void Refresh()
    {
        var c = Svc.Pointer.ReadCurrent();
        CurrentText = $"Windows is currently using pointer speed {c.Speed}/20 with “Enhance pointer precision” {(c.Precision ? "on" : "off")}.";
        RestoreCommand?.Refresh();
    }
}

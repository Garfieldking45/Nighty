using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;

namespace Nighty.ViewModels;

/// <summary>One of JinxyClicker's built-in click presets: a CPS, a click duty cycle (its CDC) and optionally hold mode.</summary>
public sealed record JinxyPreset(string Name, double Cps, double Duty, bool Hold = false)
{
    /// <summary>Nighty's clicker tops out at 100 CPS, so faster presets are clamped.</summary>
    public double AppliedCps => Math.Min(100, Cps);
    public int AppliedDuty => (int)Math.Round(Math.Clamp(Duty, 5, 95));
    public string CpsText => Cps > 100 ? $"{AppliedCps:0.##} CPS (was {Cps:0.##})" : $"{Cps:0.##} CPS";
    public string Line => $"{CpsText} · {AppliedDuty}% duty · {(Hold ? "Hold" : "Toggle")}";
    public string Ms => $"{1000.0 / AppliedCps:0.0} ms between · {1000.0 / AppliedCps * AppliedDuty / 100.0:0.0} ms held";
}

/// <summary>Every millisecond timing in one place: what the Auto Clicker works out from CPS and duty cycle, and the crossbow / whim shot timings.</summary>
public sealed class MsViewModel : ObservableObject
{
    public ClickerSettings Clicker => Svc.S.Clicker;
    public FishingSettings Fishing => Svc.S.Fishing;
    public IEnumerable<SlotMacroConfig> ShotMacros => Svc.S.SlotMacros.Where(m => m.IsSwapAndSwing);

    private double Cps => Clicker.UseRange ? (Clicker.MinCps + Clicker.MaxCps) / 2 : Clicker.Cps;
    private double Period => 1000.0 / Math.Max(1, Cps);
    private double Slice => Period / Math.Max(1, Clicker.ClicksPerHit);
    private double Hold => Math.Clamp(Slice * Clicker.DutyCycle / 100.0, Math.Min(4, Slice - 0.3), Math.Max(0.5, Slice - 1));

    public string RateText => Clicker.UseRange ? $"{Clicker.MinCps:0.#}-{Clicker.MaxCps:0.#} CPS" : $"{Clicker.Cps:0.##} CPS";
    public string PeriodText => $"{Period:0.00} ms";
    public string HoldText => $"{Hold:0.00} ms";
    public string GapText => $"{Math.Max(0, Slice - Hold):0.00} ms";
    public string DelayText => Clicker.StartDelayMs > 0 ? $"{Clicker.StartDelayMs} ms" : "none";
    public string MeasuredText => Svc.Clicker.IsClicking ? $"{Svc.Clicker.MeasuredCps:0} CPS" : "-";
    public double Duty { get => Clicker.DutyCycle; set { Clicker.DutyCycle = (int)Math.Round(value); } }
    public double CpsValue { get => Clicker.Cps; set { Clicker.Cps = value; } }

    public IReadOnlyList<JinxyPreset> JinxyPresets { get; } = new JinxyPreset[]
    {
        new("Measured", 41.2, 77.37, Hold: true),
        new("Ish", 193.62, 73.52),
        new("Snoopy", 75.65, 91.21),
        new("Stunned", 72.92, 17.16),
        new("Sky", 52.62, 82.62),
        new("Ara", 82.72, 27.28),
        new("Spooky", 29.28, 83.62),
        new("Milo", 53.87, 28.53),
        new("Lee", 29.62, 92.72),
        new("Sharkiffy", 72.53, 55.73),
        new("AraStxr", 65.33, 42.55),
        new("YoNoobLike", 85.86, 64.25),
    };

    private string _presetMessage = "";
    public string PresetMessage { get => _presetMessage; private set => Set(ref _presetMessage, value); }
    public RelayCommand ApplyPreset { get; }

    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MsViewModel()
    {
        ApplyPreset = new RelayCommand(o =>
        {
            if (o is not JinxyPreset p) return;
            if (Svc.Clicker.IsClicking) { PresetMessage = "Stop the auto clicker first."; return; }
            Clicker.UseRange = false;
            Clicker.Cps = p.AppliedCps;
            Clicker.DutyCycle = p.AppliedDuty;
            Clicker.Mode = p.Hold ? ActivationMode.Hold : ActivationMode.Toggle;
            PresetMessage = $"Applied {p.Name}: {p.Line}";
        });
        var names = new[] { nameof(RateText), nameof(PeriodText), nameof(HoldText), nameof(GapText), nameof(DelayText), nameof(Duty), nameof(CpsValue) };
        Clicker.PropertyChanged += (_, _) => { foreach (var n in names) OnPropertyChanged(n); };
        _timer.Tick += (_, _) => OnPropertyChanged(nameof(MeasuredText));
        _timer.Start();
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed class UtilityViewModel : ObservableObject
{
    private int _tab;
    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public BrightnessViewModel Brightness { get; } = new();
    public MovementViewModel Movement { get; } = new();
    public TrackingViewModel Tracking { get; } = new();
    public DnsViewModel Dns { get; } = new();
    public QosViewModel Qos { get; } = new();
}

// ============================================================ Brightness

public sealed class BrightnessViewModel : ObservableObject
{
    private DisplayInfo? _selected;
    private bool _loading = true;
    private double _value;
    private string _message = "";
    private StatusKind _kind = StatusKind.Info;
    private readonly DispatcherTimer _apply = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _suppress;

    public ObservableCollection<DisplayInfo> Displays { get; } = new();
    public bool IsLoading { get => _loading; private set { Set(ref _loading, value); OnPropertyChanged(nameof(IsSupported)); OnPropertyChanged(nameof(NoDisplays)); } }
    public DisplayInfo? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _suppress = true; Value = value?.Current ?? 0; _suppress = false;
            OnPropertyChanged(nameof(IsSupported)); OnPropertyChanged(nameof(Min)); OnPropertyChanged(nameof(Max)); OnPropertyChanged(nameof(UnsupportedReason));
        }
    }
    public bool IsSupported => !IsLoading && Selected is { Supported: true };
    public bool NoDisplays => !IsLoading && Displays.Count == 0;
    public string? UnsupportedReason => Selected is { Supported: false } s ? s.Reason : null;
    public double Min => Selected?.Min ?? 0;
    public double Max => Selected?.Max ?? 100;
    public double Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value) || _suppress || Selected is not { Supported: true }) return;
            _apply.Stop(); _apply.Start();   // throttle slow DDC/CI writes
        }
    }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }

    public AsyncCommand RefreshCommand { get; }
    public RelayCommand RestoreCommand { get; }

    // ---- software boost (gamma ramp), works on any display
    private readonly DispatcherTimer _softApply = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private double _soft = Svc.SoftBrightness.Level;
    private string _softMessage = "";
    public double SoftLevel { get => _soft; set { if (Set(ref _soft, Math.Round(value))) { OnPropertyChanged(nameof(SoftLabel)); _softApply.Stop(); _softApply.Start(); } } }
    public string SoftLabel => SoftLevel > 100 ? $"+{SoftLevel - 100:0}% boost" : SoftLevel < 100 ? $"{SoftLevel:0}% (dimmed)" : "Normal range";
    public string SoftMessage { get => _softMessage; private set => Set(ref _softMessage, value); }
    public RelayCommand SoftResetCommand { get; }

    public BrightnessViewModel()
    {
        _softApply.Tick += (_, _) =>
        {
            _softApply.Stop();
            var (ok, msg) = Svc.SoftBrightness.Set((int)SoftLevel);
            SoftMessage = msg;
            if (Svc.SoftBrightness.Level != (int)SoftLevel) { _soft = Svc.SoftBrightness.Level; OnPropertyChanged(nameof(SoftLevel)); OnPropertyChanged(nameof(SoftLabel)); }
            if (!ok) SoftMessage = msg;
        };
        SoftResetCommand = new RelayCommand(() =>
        {
            _softApply.Stop();
            var (_, msg) = Svc.SoftBrightness.Reset();
            _soft = 100; OnPropertyChanged(nameof(SoftLevel)); OnPropertyChanged(nameof(SoftLabel));
            SoftMessage = msg;
        });
        RefreshCommand = new AsyncCommand(Refresh);
        RestoreCommand = new RelayCommand(Restore, () => Selected != null && Svc.S.Backups.BrightnessOriginal.ContainsKey(Selected.Id));
        _apply.Tick += (_, _) =>
        {
            _apply.Stop();
            if (Selected == null) return;
            var d = Selected;
            if (!Svc.S.Backups.BrightnessOriginal.ContainsKey(d.Id))
            {
                Svc.S.Backups.BrightnessOriginal[d.Id] = d.Current;
                Svc.Settings.Save();
            }
            if (Svc.Brightness.TrySet(d, (int)Value, out var err)) { Message = $"Brightness set to {d.Current}%."; Kind = StatusKind.Success; }
            else { Message = "Could not change brightness: " + err; Kind = StatusKind.Error; }
            RestoreCommand.Refresh();
        };
        _ = RefreshCommand.CanExecute(null);
        RefreshCommand.Execute(null);
    }

    private async Task Refresh()
    {
        IsLoading = true; Message = "";
        var list = await Svc.Brightness.EnumerateAsync();
        Displays.Clear();
        foreach (var d in list) Displays.Add(d);
        Selected = Displays.FirstOrDefault(d => d.Supported) ?? Displays.FirstOrDefault();
        IsLoading = false;
        if (Displays.Count == 0) { Message = "No controllable displays were found."; Kind = StatusKind.Warning; }
        RestoreCommand.Refresh();
    }

    private void Restore()
    {
        if (Selected == null || !Svc.S.Backups.BrightnessOriginal.TryGetValue(Selected.Id, out var orig)) return;
        if (Svc.Brightness.TrySet(Selected, orig, out var err))
        {
            _suppress = true; Value = Selected.Current; _suppress = false;
            Svc.S.Backups.BrightnessOriginal.Remove(Selected.Id);
            Svc.Settings.Save();
            Message = $"Restored original brightness ({orig}%)."; Kind = StatusKind.Success;
        }
        else { Message = "Could not restore: " + err; Kind = StatusKind.Error; }
        RestoreCommand.Refresh();
    }
}

// ============================================================ Movement helper

public sealed class MovementViewModel : ObservableObject
{
    private string _message = "";
    private StatusKind _kind = StatusKind.Info;
    private string _current = "";

    public MovementSettings Settings => Svc.S.Utility.Movement;
    public SocdSettings Socd => Svc.S.Utility.Socd;
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public string CurrentText { get => _current; private set => Set(ref _current, value); }
    public bool HasBackup => Svc.Movement.HasBackup;
    public RelayCommand RestoreCommand { get; }

    public MovementViewModel()
    {
        RestoreCommand = new RelayCommand(() =>
        {
            Message = Svc.Movement.Restore(); Kind = StatusKind.Success;
            Settings.DisableStickyKeysShortcut = Settings.DisableFilterKeysShortcut = Settings.DisableToggleKeysShortcut = Settings.FastKeyRepeat = false;
            RefreshCurrent();
        }, () => Svc.Movement.HasBackup);
        Settings.PropertyChanged += (_, _) =>
        {
            var r = Svc.Movement.Apply(Settings);
            Message = r; Kind = r.StartsWith("Applied") ? StatusKind.Success : StatusKind.Error;
            RefreshCurrent();
        };
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        var c = Svc.Movement.ReadCurrent();
        string On(uint f) => (f & 4) != 0 ? "on" : "off";
        CurrentText = $"Sticky Keys shortcut: {On(c.Sticky)}  ·  Filter Keys shortcut: {On(c.Filter)}  ·  Toggle Keys shortcut: {On(c.Toggle)}  ·  Key repeat delay/rate: {c.Delay}/{c.Speed}";
        OnPropertyChanged(nameof(HasBackup));
        RestoreCommand?.Refresh();
    }
}

// ============================================================ Tracking helper

public sealed class TrackingViewModel : ObservableObject
{
    private string _message = "";
    private StatusKind _kind = StatusKind.Info;
    private string _current = "";

    public TrackingSettings Settings => Svc.S.Utility.Tracking;
    public double SlowSpeed { get => Settings.SlowSpeed; set { Settings.SlowSpeed = (int)value; OnPropertyChanged(); Svc.Pointer.UpdateSlotSlowdown(); } }
    public double OtherSlotSpeed { get => Settings.OtherSlotSpeed; set { Settings.OtherSlotSpeed = (int)value; OnPropertyChanged(); Svc.Pointer.UpdateSlotSlowdown(); } }
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

// ============================================================ DNS

public sealed class DnsViewModel : ObservableObject
{
    private AdapterInfo? _adapter;
    private bool _busy;
    private string _message = "";
    private StatusKind _kind = StatusKind.Info;
    private CancellationTokenSource? _cts;

    public DnsSettings Settings => Svc.S.Utility.Dns;
    public ObservableCollection<DnsProvider> Providers { get; } = new();
    public ObservableCollection<AdapterInfo> Adapters { get; } = new();

    public AdapterInfo? Adapter
    {
        get => _adapter;
        set { if (Set(ref _adapter, value)) { if (value != null) Svc.S.Utility.Dns.AdapterId = value.Id; OnPropertyChanged(nameof(CurrentDns)); MarkCurrent(); } }
    }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); TestCommand.Refresh(); ApplyCommand.Refresh(); ApplyBestCommand.Refresh(); RestoreCommand.Refresh(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public string CurrentDns => Adapter == null ? "No active network adapter found" :
        (Adapter.DnsServers.Count == 0 ? "None reported" : string.Join(", ", Adapter.DnsServers)) + (Adapter.IsStatic ? "  (manually set)" : "  (automatic / DHCP)");
    public string BackupText => Svc.Dns.Backup is { } b
        ? $"Saved previous configuration for {b.AdapterName}: {(b.WasStatic ? string.Join(", ", b.Servers) : "automatic (DHCP)")}"
        : "No changes made by Nighty — nothing to restore.";
    public bool HasBackup => Svc.Dns.HasBackup;
    public bool NeedsAdmin => !Elevation.IsAdmin;

    public AsyncCommand TestCommand { get; }
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand ApplyBestCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RefreshAdaptersCommand { get; }
    public RelayCommand AddCustomCommand { get; }
    public RelayCommand RemoveCustomCommand { get; }
    public string CustomText { get => _customText; set => Set(ref _customText, value); }
    private string _customText = "";
    private const int MaxCustom = 6;

    private static bool TryParseCustom(string text, out string primary, out string secondary)
    {
        primary = secondary = "";
        var parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2) return false;
        foreach (var part in parts)
            if (!System.Net.IPAddress.TryParse(part, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || part.Count(c => c == '.') != 3) return false;
        primary = parts[0]; secondary = parts.Length == 2 ? parts[1] : "";
        return true;
    }

    private static DnsProvider MakeCustom(string primary, string secondary) =>
        new() { Name = "Custom " + primary, Primary = primary, Secondary = secondary, IsCustom = true };

    public DnsViewModel()
    {
        foreach (var p in Svc.Dns.CreateProviders()) Providers.Add(p);
        foreach (var c in Settings.Custom.ToList())
            if (TryParseCustom(c, out var p1, out var p2)) Providers.Add(MakeCustom(p1, p2));
        AddCustomCommand = new RelayCommand(() =>
        {
            if (!TryParseCustom(CustomText.Trim(), out var p1, out var p2)) { Message = "Enter an IPv4 address such as 192.168.1.1 (optionally a second one after a comma)."; Kind = StatusKind.Warning; return; }
            if (Providers.Count(x => x.IsCustom) >= MaxCustom) { Message = "Remove a custom resolver first (up to 6)."; Kind = StatusKind.Warning; return; }
            if (Providers.Any(x => x.Primary == p1)) { Message = "That resolver is already in the list."; Kind = StatusKind.Warning; return; }
            Providers.Add(MakeCustom(p1, p2));
            Settings.Custom.Add(p2.Length == 0 ? p1 : $"{p1},{p2}");
            Svc.Settings.Save();
            CustomText = ""; MarkCurrent();
            Message = $"Added {p1}. Press Test all to include it."; Kind = StatusKind.Info;
        });
        RemoveCustomCommand = new RelayCommand(o =>
        {
            if (o is not DnsProvider d || !d.IsCustom) return;
            Providers.Remove(d);
            Settings.Custom.RemoveAll(c => c.StartsWith(d.Primary));
            Svc.Settings.Save();
        });
        TestCommand = new AsyncCommand(Test, () => !IsBusy);
        ApplyCommand = new AsyncCommand(p => Apply(p as DnsProvider, true), p => !IsBusy && Adapter != null);
        ApplyBestCommand = new AsyncCommand(() => Apply(Providers.FirstOrDefault(x => x.IsBest), true), () => !IsBusy && Adapter != null && Providers.Any(x => x.IsBest));
        RestoreCommand = new AsyncCommand(Restore, () => !IsBusy && Svc.Dns.HasBackup);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        RefreshAdaptersCommand = new RelayCommand(LoadAdapters);
        LoadAdapters();
    }

    private void LoadAdapters()
    {
        var keep = Adapter?.Id ?? Svc.S.Utility.Dns.AdapterId;
        Adapters.Clear();
        foreach (var a in Svc.Dns.GetAdapters()) Adapters.Add(a);
        Adapter = Adapters.FirstOrDefault(a => a.Id == keep) ?? Adapters.FirstOrDefault();
        OnPropertyChanged(nameof(CurrentDns)); OnPropertyChanged(nameof(BackupText)); OnPropertyChanged(nameof(HasBackup));
        ApplyCommand?.Refresh(); RestoreCommand?.Refresh();
    }

    private void MarkCurrent()
    {
        foreach (var p in Providers)
            p.IsCurrent = Adapter != null && Adapter.DnsServers.Count > 0 && Adapter.DnsServers.Contains(p.Primary);
    }

    private async Task Test()
    {
        _cts = new CancellationTokenSource();
        DnsProvider? autoApply = null;
        IsBusy = true; Kind = StatusKind.Info; Message = "Measuring real DNS response times… this takes a few seconds.";
        foreach (var p in Providers) { p.LatencyMs = null; p.Success = 0; p.Total = 0; p.IsBest = false; p.Status = "Waiting"; p.Kind = StatusKind.Neutral; }
        try
        {
            foreach (var p in Providers) await Svc.Dns.MeasureAsync(p, _cts.Token);
            var best = Providers.Where(p => p.LatencyMs != null && p.Total > 0 && p.Success * 100 >= p.Total * 80).OrderBy(p => p.LatencyMs).FirstOrDefault();
            if (best != null) { best.IsBest = true; Message = $"Fastest reachable resolver from this PC right now: {best.Name} ({best.LatencyMs:0} ms median). Results vary by network and time."; Kind = StatusKind.Success;
                if (Settings.AutoApplyBest && Adapter != null && !best.IsCurrent) autoApply = best; }
            else { Message = "No resolver answered reliably. Check your connection or firewall (UDP port 53)."; Kind = StatusKind.Error; }
        }
        catch (OperationCanceledException)
        {
            foreach (var p in Providers.Where(p => p.Status is "Waiting" or "Testing…")) { p.Status = "Cancelled"; p.Kind = StatusKind.Neutral; }
            Message = "Test cancelled."; Kind = StatusKind.Warning;
        }
        finally { IsBusy = false; }
        if (autoApply != null) await Apply(autoApply, confirm: false);
    }

    private async Task Apply(DnsProvider? p, bool confirm = true)
    {
        if (p == null || Adapter == null) return;
        if (confirm && !Dialogs.Confirm("Change DNS servers?", $"Set DNS for “{Adapter.Name}” to {p.Name} ({p.Servers}).\n\nWindows will ask for administrator approval. Your current configuration is saved first so you can restore it.", "Apply DNS")) return;
        IsBusy = true; Kind = StatusKind.Info; Message = "Waiting for administrator approval…";
        try
        {
            var (ok, msg) = await Svc.Dns.ApplyAsync(Adapter, p);
            Message = ok ? msg : "Not applied: " + msg; Kind = ok ? StatusKind.Success : StatusKind.Error;
        }
        finally { IsBusy = false; LoadAdapters(); }
    }

    private async Task Restore()
    {
        IsBusy = true; Kind = StatusKind.Info; Message = "Waiting for administrator approval…";
        try
        {
            var (ok, msg) = await Svc.Dns.RestoreAsync();
            Message = ok ? msg : "Not restored: " + msg; Kind = ok ? StatusKind.Success : StatusKind.Error;
        }
        finally { IsBusy = false; LoadAdapters(); }
    }
}

// ============================================================ QoS

public sealed record DscpChoice(int Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class QosViewModel : ObservableObject
{
    private bool _busy;
    private string _message = "", _status = "";
    private StatusKind _kind = StatusKind.Info, _statusKind = StatusKind.Neutral;
    private DscpChoice _choice;

    public List<DscpChoice> Choices { get; } = new()
    {
        new(46, "DSCP 46 — Expedited Forwarding (voice/real-time)"),
        new(34, "DSCP 34 — AF41 (interactive video/gaming)"),
        new(26, "DSCP 26 — AF31 (streaming)"),
        new(0, "DSCP 0 — Default (no marking)"),
    };
    public DscpChoice Choice { get => _choice; set { if (Set(ref _choice, value)) Svc.S.Utility.Qos.Dscp = value.Value; } }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); ApplyCommand.Refresh(); RemoveCommand.Refresh(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public bool PolicyExists { get; private set; }
    public string AppName => QosService.AppName;
    public bool UseOnHome { get => Svc.S.Utility.Qos.UseOnHomeNetworks; set { Svc.S.Utility.Qos.UseOnHomeNetworks = value; OnPropertyChanged(); } }

    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public QosViewModel()
    {
        _choice = Choices.FirstOrDefault(c => c.Value == Svc.S.Utility.Qos.Dscp) ?? Choices[0];
        ApplyCommand = new AsyncCommand(Apply, () => !IsBusy);
        RemoveCommand = new AsyncCommand(Remove, () => !IsBusy && PolicyExists);
        RefreshCommand = new RelayCommand(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        var p = Svc.Qos.ReadPolicy();
        PolicyExists = p.Exists;
        Status = p.Exists ? $"Policy “{QosService.PolicyName}” is installed: {p.App} → DSCP {p.Dscp}. " + (Svc.Qos.NlaDisabled() ? "Policies apply on every network." : "Policies apply on domain networks only.") : "No Nighty QoS policy is installed.";
        StatusKind = p.Exists ? StatusKind.Success : StatusKind.Neutral;
        OnPropertyChanged(nameof(PolicyExists));
        RemoveCommand?.Refresh();
    }

    private async Task Apply()
    {
        IsBusy = true; Kind = StatusKind.Info; Message = Elevation.IsAdmin ? "Applying…" : "Waiting for administrator approval…";
        try { var (ok, msg) = await Svc.Qos.ApplyAsync(Choice.Value, UseOnHome); Message = ok ? msg : "Not applied: " + msg; Kind = ok ? StatusKind.Success : StatusKind.Error; }
        finally { IsBusy = false; Refresh(); }
    }

    private async Task Remove()
    {
        IsBusy = true; Kind = StatusKind.Info; Message = Elevation.IsAdmin ? "Removing…" : "Waiting for administrator approval…";
        try { var (ok, msg) = await Svc.Qos.RemoveAsync(); Message = ok ? msg : "Not removed: " + msg; Kind = ok ? StatusKind.Success : StatusKind.Error; }
        finally { IsBusy = false; Refresh(); }
    }
}

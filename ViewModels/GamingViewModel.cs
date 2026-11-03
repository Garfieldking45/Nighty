using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Nighty.Mvvm;
using Nighty.Services;

namespace Nighty.ViewModels;

/// <summary>One row of the "what it does" list.</summary>
public sealed class BoostLineViewModel : ObservableObject
{
    private string _status = "";
    private StatusKind _kind;
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Why { get; init; }
    public string Status { get => _status; set => Set(ref _status, value); }
    public StatusKind Kind { get => _kind; set => Set(ref _kind, value); }
}

/// <summary>The Gaming page: one button that boosts Roblox's FPS, and a plain list of what it does on this PC.</summary>
public sealed class GamingViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _robloxText = "Roblox is not running";

    public ObservableCollection<BoostLineViewModel> Lines { get; } = new();
    public AsyncCommand BoostCommand { get; }
    public AsyncCommand UndoCommand { get; }

    public bool IsOn => Svc.Boost.IsOn;
    public bool IsBusy => Svc.Boost.Busy;
    public string ButtonLabel => IsBusy ? "Working…" : IsOn ? "FPS boost is ON" : "Click to boost FPS";
    public string ButtonHint => IsOn ? "Click to turn it off and put everything back" : "One click. Everything is put back when you turn it off.";

    public string Headline => !IsOn ? "" : Svc.Boost.IsApplied ? "Roblox is boosted." : "Ready. The boost starts the moment Roblox opens.";
    public string RestartHint => Svc.Boost.NeedsRestart ? "Roblox was already open: restart it once so the graphics card and FPS cap take effect." : "";
    public string RobloxText { get => _robloxText; private set => Set(ref _robloxText, value); }

    public GamingViewModel()
    {
        foreach (var l in Svc.Boost.Lines())
            Lines.Add(new BoostLineViewModel { Id = l.Id, Title = l.Title, Why = l.Why, Status = l.Status, Kind = l.Kind });

        BoostCommand = new AsyncCommand(async () =>
        {
            if (Svc.Boost.IsOn) await Svc.Boost.DisableAsync(); else await Svc.Boost.EnableAsync();
        }, () => !IsBusy);
        UndoCommand = new AsyncCommand(async () => await Svc.Boost.DisableAsync(), () => !IsBusy);

        Svc.Boost.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        Svc.GameMode.Changed += () => Application.Current?.Dispatcher.BeginInvoke(Refresh);
        _timer.Tick += (_, _) => Sample();
    }

    /// <summary>The page became visible or hidden: the Roblox readout runs only while it is shown.</summary>
    public void SetVisible(bool visible)
    {
        if (!visible) { _timer.Stop(); return; }
        Sample(); _timer.Start();
        Refresh();
        _ = Svc.Boost.RefreshPreviewAsync();   // read-only look at this PC, so the list says what applies before anything is switched on
    }

    private void Refresh()
    {
        foreach (var l in Svc.Boost.Lines())
        {
            var row = Lines.FirstOrDefault(r => r.Id == l.Id);
            if (row == null) continue;
            row.Status = l.Status; row.Kind = l.Kind;
        }
        foreach (var n in new[] { nameof(IsOn), nameof(IsBusy), nameof(ButtonLabel), nameof(ButtonHint), nameof(Headline), nameof(RestartHint) }) OnPropertyChanged(n);
        BoostCommand.Refresh(); UndoCommand.Refresh();
    }

    private void Sample()
    {
        var mb = Svc.Usage.RobloxMemoryMb();
        RobloxText = mb is double m ? $"Roblox is running · {m:0} MB" : "Roblox is not running";
    }
}

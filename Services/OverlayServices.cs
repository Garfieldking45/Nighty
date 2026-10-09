using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>Round-trip time to a configurable host using ICMP. Runs only while something needs it.</summary>
public sealed class PingService
{
    private CancellationTokenSource? _cts;
    public int? LastMs { get; private set; }
    public string Status { get; private set; } = "Idle";
    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var ping = new Ping();
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var r = await ping.SendPingAsync(Svc.S.Overlays.PingHost, 1000);
                    if (r.Status == IPStatus.Success) { LastMs = (int)r.RoundtripTime; Status = "OK"; }
                    else { LastMs = null; Status = r.Status == IPStatus.TimedOut ? "Timed out" : r.Status.ToString(); }
                }
                catch (Exception ex) { LastMs = null; Status = ex.InnerException?.Message ?? ex.Message; }
                try { await Task.Delay(1000, cts.Token); } catch { break; }
            }
        });
    }

    public void Stop() { _cts?.Cancel(); _cts = null; LastMs = null; Status = "Idle"; }
}

/// <summary>
/// Real Roblox frame rate by counting DXGI Present events for the Roblox process via ETW (the technique PresentMon
/// uses). ETW sessions need administrator rights or membership of "Performance Log Users", so without either this reports "unavailable" instead of a number.
/// </summary>
public sealed class FpsService
{
    private const string SessionName = "NightyFpsSession";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private TraceEventSession? _session;
    private CancellationTokenSource? _cts;
    private HashSet<int> _pids = new();
    private long _frames;

    public double? Fps { get; private set; }
    public string Status { get; private set; } = "Idle";
    public bool IsRunning => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        if (!Elevation.CanTraceEtw) { Status = "Needs one-time setup"; return; }
        var cts = _cts = new CancellationTokenSource();
        Status = "Starting";
        _ = Task.Run(() => RunSession());
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, cts.Token); } catch { break; }
                _pids = Process.GetProcessesByName(RobloxService.ProcessName).Select(p => { var id = p.Id; p.Dispose(); return id; }).ToHashSet();
                long frames = Interlocked.Exchange(ref _frames, 0);
                if (_pids.Count == 0) { Fps = null; Status = "Roblox not running"; }
                else if (frames == 0) { Fps = null; Status = "No frames detected"; }
                else { Fps = frames; Status = "Measuring"; }
            }
        });
    }

    private void RunSession()
    {
        try
        {
            TraceEventSession.GetActiveSession(SessionName)?.Stop();
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.Dynamic.All += (TraceEvent e) =>
            {
                if (e.ProviderGuid == DxgiProvider && (int)e.ID == 42 && _pids.Contains(e.ProcessID))
                    Interlocked.Increment(ref _frames);
            };
            _session.Source.Process();
        }
        catch (Exception ex)
        {
            Log.Error("FPS tracing failed", ex);
            Status = ex is UnauthorizedAccessException ? "Needs one-time setup" : "Tracing unavailable";
            Fps = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        Fps = null;
        Status = "Idle";
    }
}

/// <summary>Click-through, always-on-top overlay window that shows one overlay.</summary>
public sealed class OverlayWindow : Window
{
    private readonly OverlayConfig _cfg;
    private readonly Border _chrome;
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold };
    private readonly Dictionary<int, Border> _caps = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private Nighty.Controls.CrosshairVisual? _cross;

    public OverlayWindow(OverlayConfig cfg)
    {
        _cfg = cfg;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;

        _chrome = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x10, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6),
            LayoutTransform = _scale,
            Child = BuildContent(),
        };
        if (_cfg.Kind == OverlayKind.Crosshair)
        {
            _chrome.Background = Brushes.Transparent;
            _chrome.BorderThickness = new Thickness(0);
            _chrome.Padding = new Thickness(0);
        }
        Content = _chrome;
        SizeChanged += (_, _) => Reposition();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        };
        ApplyConfig();
    }

    private UIElement BuildContent()
    {
        switch (_cfg.Kind)
        {
            case OverlayKind.Wasd:
            {
                var g = new Grid();
                for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
                for (int i = 0; i < 2; i++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
                Add(g, 0x57, "W", 1, 0); Add(g, 0x41, "A", 0, 1); Add(g, 0x53, "S", 1, 1); Add(g, 0x44, "D", 2, 1);
                return g;
            }
            case OverlayKind.Mouse:
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(Cap(0x01, "L", 44)); sp.Children.Add(Cap(0x04, "M", 44)); sp.Children.Add(Cap(0x02, "R", 44));
                return sp;
            }
            case OverlayKind.Crosshair:
                return _cross = new Nighty.Controls.CrosshairVisual(_cfg);
            case OverlayKind.Key:
                return Cap(_cfg.KeyVk, Hotkeys.KeyName(_cfg.KeyVk).ToUpperInvariant(), 44);
            default:
                return _text;
        }
    }

    private void Add(Grid g, int vk, string label, int col, int row)
    {
        var cap = Cap(vk, label, 34);
        Grid.SetColumn(cap, col); Grid.SetRow(cap, row);
        g.Children.Add(cap);
    }

    private Border Cap(int vk, string label, double size)
    {
        var b = new Border
        {
            Width = size, Height = size > 40 ? 38 : 34, Margin = new Thickness(2), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x46)), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = label, Foreground = Brushes.White, FontSize = label.Length > 3 ? 10 : 14, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _caps[vk] = b;
        return b;
    }

    public void ApplyConfig()
    {
        _scale.ScaleX = _scale.ScaleY = _cfg.Scale;
        Opacity = _cfg.Opacity;
        _cross?.Update();
        Reposition();
    }

    private void Reposition()
    {
        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        Left = _cfg.X / 100.0 * Math.Max(0, sw - ActualWidth);
        Top = _cfg.Y / 100.0 * Math.Max(0, sh - ActualHeight);
        if (_cfg.Kind == OverlayKind.Crosshair) { Left = Math.Round(Left); Top = Math.Round(Top); }   // keep the centre on a whole pixel
    }

    public void Refresh()
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        var idle = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x24));
        foreach (var (vk, cap) in _caps)
        {
            bool down = (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
            cap.Background = down ? accent : idle;
        }
        switch (_cfg.Kind)
        {
            case OverlayKind.Cps:
            {
                var (l, r) = Svc.Cps.Read();
                _text.Text = Svc.Cps.IsActive ? $"CPS  {l}  |  {r}" : "CPS  —";
                break;
            }
            case OverlayKind.Fps:
                _text.Text = Svc.Fps.Fps is double f ? $"FPS  {f:0}" : $"FPS  —  ({Svc.Fps.Status})";
                break;
            case OverlayKind.Ping:
                _text.Text = Svc.Ping.LastMs is int ms ? $"PING  {ms} ms" : $"PING  —  ({Svc.Ping.Status})";
                break;
        }
    }
}

/// <summary>Creates/updates/destroys overlay windows from settings and drives their refresh.</summary>
public sealed class OverlayManager
{
    private readonly Dictionary<Guid, OverlayWindow> _windows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _tick;

    public OverlayManager() { _timer.Tick += (_, _) => Tick(); }

    public void Start()
    {
        Sync();
        _timer.Start();
    }

    public void Shutdown()
    {
        _timer.Stop();
        foreach (var w in _windows.Values) w.Close();
        _windows.Clear();
        Svc.Cps.Stop(); Svc.Ping.Stop(); Svc.Fps.Stop();
    }

    /// <summary>Reconcile windows with the current settings. Safe to call on any change.</summary>
    public void Sync()
    {
        var items = Svc.S.Overlays.Items;
        foreach (var id in _windows.Keys.Except(items.Where(i => i.Enabled).Select(i => i.Id)).ToList())
        {
            _windows[id].Close();
            _windows.Remove(id);
        }
        foreach (var cfg in items.Where(i => i.Enabled))
        {
            if (!_windows.TryGetValue(cfg.Id, out var w))
            {
                w = new OverlayWindow(cfg);
                _windows[cfg.Id] = w;
                w.Show();
            }
            w.ApplyConfig();
        }

        bool Wants(OverlayKind k) => items.Any(i => i.Enabled && i.Kind == k);
        if (Wants(OverlayKind.Cps)) Svc.Cps.Start(); else Svc.Cps.Stop();
        if (Wants(OverlayKind.Ping)) Svc.Ping.Start(); else Svc.Ping.Stop();
        if (Wants(OverlayKind.Fps)) Svc.Fps.Start(); else Svc.Fps.Stop();
    }

    private void Tick()
    {
        foreach (var w in _windows.Values) w.Refresh();
        _tick++;
    }
}

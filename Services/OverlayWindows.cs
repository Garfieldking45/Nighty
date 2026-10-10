using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>Click-through, always-on-top overlay window that shows one overlay.</summary>
public sealed class OverlayWindow : Window
{
    private readonly OverlayConfig _cfg;
    private readonly Border _chrome;
    private TextBlock _text = new() { FontSize = 14, FontWeight = FontWeights.SemiBold };
    private TextBlock? _big, _sub;
    private Polyline? _graph;
    private readonly Dictionary<int, Border> _caps = new();
    private readonly Dictionary<int, TextBlock> _capCps = new();
    private readonly Dictionary<int, int> _presses = new();
    private readonly Dictionary<int, bool> _wasDown = new();
    private readonly Queue<double> _samples = new();
    private long _lastSample;
    private readonly ScaleTransform _scale = new(1, 1);
    private Nighty.Controls.CrosshairVisual? _cross;
    private string _signature = "";

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

        _chrome = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6), LayoutTransform = _scale };
        if (_cfg.Kind == OverlayKind.Crosshair) { _chrome.Background = Brushes.Transparent; _chrome.BorderThickness = new Thickness(0); _chrome.Padding = new Thickness(0); }
        _chrome.Child = BuildContent();
        _signature = Signature();
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

    private static Brush B(string hex, double alpha = 1)
    {
        var c = ThemeService.TryParse(hex, out var v) ? v : Colors.White;
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>What the content is built from: when any of it changes the content is rebuilt.</summary>
    private string Signature() => $"{_cfg.Kind}|{_cfg.CpsLayout}|{_cfg.ShowSpace}|{_cfg.ShowShift}|{_cfg.SideButtons}|{_cfg.CpsOnButtons}|{_cfg.PressCounter}|{_cfg.KeyVk}";

    private UIElement BuildContent()
    {
        _caps.Clear(); _capCps.Clear(); _big = _sub = null; _graph = null;
        switch (_cfg.Kind)
        {
            case OverlayKind.Wasd:
            {
                var g = new Grid();
                for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
                int rows = _cfg.ShowSpace || _cfg.ShowShift ? 3 : 2;
                for (int i = 0; i < rows; i++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
                Add(g, 0x57, "W", 1, 0); Add(g, 0x41, "A", 0, 1); Add(g, 0x53, "S", 1, 1); Add(g, 0x44, "D", 2, 1);
                if (_cfg.ShowShift) { var sh = Cap(0x10, "⇧", 34); Grid.SetColumn(sh, 0); Grid.SetRow(sh, 2); g.Children.Add(sh); }
                if (_cfg.ShowSpace)
                {
                    var sp = Cap(0x20, "SPACE", 72); sp.Width = 72;
                    Grid.SetColumn(sp, _cfg.ShowShift ? 1 : 0); Grid.SetColumnSpan(sp, 2); Grid.SetRow(sp, 2); g.Children.Add(sp);
                }
                return g;
            }
            case OverlayKind.Mouse:
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                if (_cfg.SideButtons) sp.Children.Add(Cap(0x05, "4", 40));
                sp.Children.Add(Cap(0x01, "L", 44, _cfg.CpsOnButtons)); sp.Children.Add(Cap(0x04, "M", 44)); sp.Children.Add(Cap(0x02, "R", 44, _cfg.CpsOnButtons));
                if (_cfg.SideButtons) sp.Children.Add(Cap(0x06, "5", 40));
                return sp;
            }
            case OverlayKind.Crosshair:
                return _cross = new Nighty.Controls.CrosshairVisual(_cfg);
            case OverlayKind.Key:
            {
                var cap = Cap(_cfg.KeyVk, Hotkeys.KeyName(_cfg.KeyVk).ToUpperInvariant(), 44, _cfg.PressCounter);
                return cap;
            }
            case OverlayKind.Cps when _cfg.CpsLayout == CpsLayout.Big:
            {
                _big = new TextBlock { FontSize = 30, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
                _sub = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 };
                var st = new StackPanel { MinWidth = 70 };
                st.Children.Add(_big); st.Children.Add(_sub);
                return st;
            }
            case OverlayKind.Cps when _cfg.CpsLayout == CpsLayout.Graph:
            {
                _text = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold };
                _graph = new Polyline { Width = 150, Height = 40, StrokeThickness = 2, Stretch = Stretch.None, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 6, 0, 0) };
                var st = new StackPanel();
                st.Children.Add(_text); st.Children.Add(_graph);
                return st;
            }
            default:
                _text = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
                return _text;
        }
    }

    private void Add(Grid g, int vk, string label, int col, int row)
    {
        var cap = Cap(vk, label, 34);
        Grid.SetColumn(cap, col); Grid.SetRow(cap, row);
        g.Children.Add(cap);
    }

    private Border Cap(int vk, string label, double size, bool withSecond = false)
    {
        var main = new TextBlock { Text = label, FontSize = label.Length > 3 ? 10 : 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        UIElement content = main;
        if (withSecond)
        {
            var second = new TextBlock { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.75, Text = "0" };
            _capCps[vk] = second;
            var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            main.VerticalAlignment = VerticalAlignment.Top;
            st.Children.Add(main); st.Children.Add(second);
            content = st;
        }
        var b = new Border
        {
            Width = size, Height = withSecond ? 46 : size > 40 ? 38 : 34, Margin = new Thickness(2), CornerRadius = new CornerRadius(6),
            Background = B("#1C1C24"), BorderBrush = B("#3A3A46"), BorderThickness = new Thickness(1), Child = content,
        };
        _caps[vk] = b;
        _presses[vk] = 0;
        return b;
    }

    public void ApplyConfig()
    {
        var sig = Signature();
        if (sig != _signature) { _signature = sig; _chrome.Child = BuildContent(); }

        _scale.ScaleX = _scale.ScaleY = _cfg.Scale;
        Opacity = _cfg.Opacity;
        if (_cfg.Kind != OverlayKind.Crosshair)
        {
            _chrome.Background = B(_cfg.BgColor, _cfg.BgOpacity);
            _chrome.BorderBrush = B(_cfg.BorderColor);
            _chrome.BorderThickness = new Thickness(_cfg.ShowBorder ? 1 : 0);
            _chrome.CornerRadius = new CornerRadius(_cfg.CornerRadius);
            _chrome.Effect = _cfg.Shadow ? new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.6, Color = Colors.Black } : null;
            var fg = B(_cfg.TextColor);
            _text.Foreground = fg;
            if (_big != null) _big.Foreground = fg;
            if (_sub != null) _sub.Foreground = fg;
            if (_graph != null) _graph.Stroke = B(_cfg.HighlightColor);
            foreach (var cap in _caps.Values) SetCapText(cap, fg);
        }
        _cross?.Update();
        Reposition();
    }

    private static void SetCapText(Border cap, Brush fg)
    {
        if (cap.Child is TextBlock t) t.Foreground = fg;
        else if (cap.Child is StackPanel sp) foreach (var c in sp.Children.OfType<TextBlock>()) c.Foreground = fg;
    }

    public void Reposition()
    {
        var area = Svc.Overlays.Area;
        Left = area.Left + _cfg.X / 100.0 * Math.Max(0, area.Width - ActualWidth);
        Top = area.Top + _cfg.Y / 100.0 * Math.Max(0, area.Height - ActualHeight);
        if (_cfg.Kind == OverlayKind.Crosshair) { Left = Math.Round(Left); Top = Math.Round(Top); }   // keep the centre on a whole pixel
    }

    /// <summary>One line per enabled feature with its key; a dot marks the ones running right now.</summary>
    private static string HudText()
    {
        var lines = new List<string>();
        void Add(string name, int vk, int mods, bool running) =>
            lines.Add($"{(running ? "●" : "○")}  {name}" + (vk > 0 ? $"  [{Hotkeys.Format(vk, mods)}]" : ""));
        var c = Svc.S.Clicker;
        if (c.Enabled) Add("Auto Clicker", c.HotkeyVk, c.HotkeyMods, Svc.Clicker.IsClicking);
        var b = Svc.S.Bow;
        if (b.Enabled) Add("Bow Switch", b.Mode == BowMode.Hotkey ? b.HotkeyVk : 0, b.HotkeyMods, Svc.Bow.IsRunning);
        foreach (var m in Svc.S.SlotMacros.Where(m => m.Enabled)) Add(m.Title, m.HotkeyVk, m.HotkeyMods, Svc.SlotMacros.IsRunning(m.Kind));
        foreach (var m in Svc.S.Macros.Where(m => m.HotkeyEnabled && m.HotkeyVk > 0)) Add(m.Name, m.HotkeyVk, m.HotkeyMods, Svc.Macros.IsRunning(m.Id));
        Add("Auto Fish", Svc.S.Fishing.HotkeyVk, Svc.S.Fishing.HotkeyMods, Svc.Fishing.IsRunning);
        return lines.Count == 0 ? "No macros enabled" : string.Join("\n", lines);
    }

    public void Refresh()
    {
        var hi = B(_cfg.HighlightColor);
        var idle = B("#1C1C24");
        foreach (var (vk, cap) in _caps)
        {
            bool down = (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
            // "include the clicker" off: a button held by the auto clicker doesn't light the cap
            if (down && !_cfg.IncludeClicker && Svc.Clicker.IsClicking && ((vk == 0x01 && Svc.S.Clicker.Button == ClickButton.Left) || (vk == 0x02 && Svc.S.Clicker.Button == ClickButton.Right) || (vk == 0x04 && Svc.S.Clicker.Button == ClickButton.Middle))) down = false;
            cap.Background = down ? hi : idle;
            _wasDown.TryGetValue(vk, out bool was);
            if (down && !was) _presses[vk] = _presses.GetValueOrDefault(vk) + 1;
            _wasDown[vk] = down;
            if (_cfg.Kind == OverlayKind.Key && _cfg.PressCounter && _capCps.TryGetValue(vk, out var cnt)) cnt.Text = _presses[vk].ToString();
        }

        string P(string label, string value) => _cfg.Labels ? $"{label}  {value}" : value;
        switch (_cfg.Kind)
        {
            case OverlayKind.Cps:
            {
                var (l, r) = Svc.Cps.Read(_cfg.CpsSource);
                int shown = _cfg.CpsButtons switch { CpsButtons.Left => l, CpsButtons.Right => r, _ => l + r };
                string both = _cfg.CpsButtons switch { CpsButtons.Left => $"{l}", CpsButtons.Right => $"{r}", _ => $"{l}  |  {r}" };
                if (_big != null) { _big.Text = Svc.Cps.IsActive ? shown.ToString() : "—"; if (_sub != null) _sub.Text = _cfg.Labels ? "CPS" : ""; }
                else if (_graph != null)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastSample >= 100) { _lastSample = now; _samples.Enqueue(shown); while (_samples.Count > 60) _samples.Dequeue(); DrawGraph(); }
                    _text.Text = Svc.Cps.IsActive ? P("CPS", shown.ToString()) : P("CPS", "—");
                }
                else _text.Text = Svc.Cps.IsActive ? P("CPS", both) : P("CPS", "—");
                // clicks per second on the mouse overlay's buttons
                if (_capCps.TryGetValue(0x01, out var lt)) lt.Text = Svc.Cps.Read(_cfg.IncludeClicker ? CpsSource.Both : CpsSource.Mine).Left.ToString();
                break;
            }
            case OverlayKind.Mouse:
            {
                var (l, r) = Svc.Cps.Read(_cfg.IncludeClicker ? CpsSource.Both : CpsSource.Mine);
                if (_capCps.TryGetValue(0x01, out var lt)) lt.Text = l.ToString();
                if (_capCps.TryGetValue(0x02, out var rt)) rt.Text = r.ToString();
                break;
            }
            case OverlayKind.Fps:
                if (Svc.Fps.Fps is double f) _text.Text = P("FPS", $"{f:0}") + (_cfg.ShowFrameTime ? $"  ·  {1000.0 / Math.Max(1, f):0.0} ms" : "");
                else _text.Text = P("FPS", $"—  ({Svc.Fps.Status})");
                break;
            case OverlayKind.FishTracker:
                _text.Text = Svc.Fishing.TrackerText;
                break;
            case OverlayKind.Hud:
                _text.Text = HudText();
                break;
            case OverlayKind.Ping:
                if (Svc.Ping.LastMs is int ms)
                {
                    _text.Text = P("PING", $"{ms} ms");
                    _text.Foreground = _cfg.ColorBySpeed ? B(ms < 80 ? "#4ADE80" : ms < 150 ? "#FACC15" : "#F87171") : B(_cfg.TextColor);
                }
                else { _text.Text = P("PING", $"—  ({Svc.Ping.Status})"); _text.Foreground = B(_cfg.TextColor); }
                break;
        }
        // CPS on the Mouse overlay also needs the monitor running even when no CPS overlay is on
    }

    private void DrawGraph()
    {
        if (_graph == null) return;
        var vals = _samples.ToArray();
        double max = Math.Max(10, vals.Length > 0 ? vals.Max() : 10);
        var pts = new PointCollection();
        for (int i = 0; i < vals.Length; i++) pts.Add(new Point(i * (150.0 / 59), 40 - vals[i] / max * 38));
        _graph.Points = pts;
    }
}

/// <summary>Creates/updates/destroys overlay windows from settings and drives their refresh.</summary>
public sealed class OverlayManager
{
    private readonly Dictionary<Guid, OverlayWindow> _windows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _tick;
    private bool _shown = true;

    /// <summary>The rectangle (in device-independent pixels) that overlay positions are percentages of: the screen, or the Roblox window.</summary>
    public Rect Area { get; private set; } = new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

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
        UpdateArea();
        foreach (var cfg in items.Where(i => i.Enabled))
        {
            if (!_windows.TryGetValue(cfg.Id, out var w))
            {
                w = new OverlayWindow(cfg);
                _windows[cfg.Id] = w;
                w.Show();
                if (!_shown) w.Visibility = Visibility.Hidden;
            }
            w.ApplyConfig();
        }

        bool Wants(OverlayKind k) => items.Any(i => i.Enabled && i.Kind == k);
        if (Wants(OverlayKind.Cps) || Wants(OverlayKind.Mouse) || Wants(OverlayKind.Key)) Svc.Cps.Start(); else Svc.Cps.Stop();
        if (Wants(OverlayKind.Ping)) Svc.Ping.Start(); else Svc.Ping.Stop();
        if (Wants(OverlayKind.Fps)) Svc.Fps.Start(); else Svc.Fps.Stop();
    }

    private void UpdateArea()
    {
        var area = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        if (Svc.S.Overlays.FollowRobloxWindow)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(RobloxService.ProcessName))
                {
                    try
                    {
                        var h = p.MainWindowHandle;
                        if (h != IntPtr.Zero && NativeMethods.GetWindowRect(h, out var r) && r.R > r.L && r.B > r.T)
                        {
                            double k = Application.Current?.MainWindow is { } mw ? VisualTreeHelper.GetDpi(mw).DpiScaleX : 1;
                            area = new Rect(r.L / k, r.T / k, (r.R - r.L) / k, (r.B - r.T) / k);
                            break;
                        }
                    }
                    finally { p.Dispose(); }
                }
            }
            catch { /* keep the screen */ }
        }
        if (area != Area) { Area = area; foreach (var w in _windows.Values) w.Reposition(); }
    }

    private void Tick()
    {
        if (_tick % 10 == 0)   // twice a second
        {
            UpdateArea();
            bool show = Svc.S.Overlays.Visibility switch
            {
                OverlayVisibility.WhileRobloxOpen => Svc.Roblox.IsRunning || RobloxService.IsOwnWindowForeground(),
                OverlayVisibility.WhileRobloxFront => Svc.Roblox.IsForeground || RobloxService.IsOwnWindowForeground(),
                _ => true,
            };
            if (show != _shown)
            {
                _shown = show;
                foreach (var w in _windows.Values) w.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            }
        }
        if (_shown) foreach (var w in _windows.Values) w.Refresh();
        _tick++;
    }
}

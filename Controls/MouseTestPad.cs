using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nighty.Controls;

/// <summary>
/// A pad to check how the pointer follows the mouse. Reads raw mouse counts (what the sensor reports) and compares them with
/// how far Windows actually moved the pointer, which shows the real pointer multiplier, whether acceleration is on, and the
/// mouse's polling rate. Nothing is stored and nothing leaves the PC.
/// </summary>
public sealed class MouseTestPad : FrameworkElement
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;

    public static readonly DependencyProperty MouseDpiProperty = DependencyProperty.Register(nameof(MouseDpi), typeof(int), typeof(MouseTestPad), new PropertyMetadata(800));
    public static readonly DependencyProperty MultiplierTextProperty = DependencyProperty.Register(nameof(MultiplierText), typeof(string), typeof(MouseTestPad), new PropertyMetadata("Waiting for movement"));
    public static readonly DependencyProperty AccelTextProperty = DependencyProperty.Register(nameof(AccelText), typeof(string), typeof(MouseTestPad), new PropertyMetadata("Move fast too, to check acceleration"));
    public static readonly DependencyProperty DpiTextProperty = DependencyProperty.Register(nameof(DpiText), typeof(string), typeof(MouseTestPad), new PropertyMetadata(""));
    public static readonly DependencyProperty PollingTextProperty = DependencyProperty.Register(nameof(PollingText), typeof(string), typeof(MouseTestPad), new PropertyMetadata("Polling: move the mouse over the pad"));
    public int MouseDpi { get => (int)GetValue(MouseDpiProperty); set => SetValue(MouseDpiProperty, value); }
    public string MultiplierText { get => (string)GetValue(MultiplierTextProperty); private set => SetValue(MultiplierTextProperty, value); }
    public string AccelText { get => (string)GetValue(AccelTextProperty); private set => SetValue(AccelTextProperty, value); }
    public string DpiText { get => (string)GetValue(DpiTextProperty); private set => SetValue(DpiTextProperty, value); }
    public string PollingText { get => (string)GetValue(PollingTextProperty); private set => SetValue(PollingTextProperty, value); }

    private HwndSource? _src;
    private readonly List<Point> _trail = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private Point _lastPos; private bool _hasLast;
    private double _pxSlow, _cntSlow, _pxFast, _cntFast, _px, _cnt;
    private readonly Queue<long> _stamps = new();   // raw event times (ticks) while moving, for the polling rate
    private double _winPx, _winCnt; private int _winEvents;

    public MouseTestPad()
    {
        Height = 190; Focusable = false; Cursor = Cursors.Cross; ClipToBounds = true;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        _timer.Tick += (_, _) => Update();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var dpi = VisualTreeHelper.GetDpi(this);
        if (_hasLast) _winPx += Math.Sqrt(Math.Pow((p.X - _lastPos.X) * dpi.DpiScaleX, 2) + Math.Pow((p.Y - _lastPos.Y) * dpi.DpiScaleY, 2));
        _lastPos = p; _hasLast = true;
        _trail.Add(p); if (_trail.Count > 90) _trail.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { _hasLast = false; base.OnMouseLeave(e); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _px = _cnt = _pxSlow = _cntSlow = _pxFast = _cntFast = 0; _trail.Clear(); _stamps.Clear();
        MultiplierText = "Waiting for movement"; AccelText = "Move fast too, to check acceleration"; DpiText = ""; PollingText = "Polling: move the mouse over the pad";
        InvalidateVisual();
    }

    private void Attach()
    {
        _src = (HwndSource?)PresentationSource.FromVisual(this);
        if (_src == null) return;
        _src.AddHook(Hook);
        RegisterRawInputDevices(new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK, Target = _src.Handle } }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _timer.Start();
    }

    private void Detach()
    {
        _timer.Stop();
        if (_src == null) return;
        _src.RemoveHook(Hook);
        RegisterRawInputDevices(new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE, Target = IntPtr.Zero } }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _src = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT || !IsMouseOver) return IntPtr.Zero;
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, 24);
        if (size == 0 || size > 256) return IntPtr.Zero;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, 24) != size) return IntPtr.Zero;
            if (Marshal.ReadInt32(buf, 0) != 0) return IntPtr.Zero;                 // RIM_TYPEMOUSE
            int dx = Marshal.ReadInt32(buf, 36), dy = Marshal.ReadInt32(buf, 40);   // lLastX / lLastY
            if (dx == 0 && dy == 0) return IntPtr.Zero;
            double counts = Math.Sqrt((double)dx * dx + (double)dy * dy);
            _winCnt += counts; _winEvents++;
            long now = Environment.TickCount64;
            _stamps.Enqueue(now);
            while (_stamps.Count > 0 && now - _stamps.Peek() > 1000) _stamps.Dequeue();
        }
        finally { Marshal.FreeHGlobal(buf); }
        return IntPtr.Zero;
    }

    private void Update()
    {
        // Every 200 ms: take the pointer pixels and raw counts seen in this window and file them as slow or fast movement.
        if (_winCnt > 30 && _winPx > 5)
        {
            double perEvent = _winCnt / Math.Max(1, _winEvents);
            _px += _winPx; _cnt += _winCnt;
            if (perEvent < 4) { _pxSlow += _winPx; _cntSlow += _winCnt; }
            else if (perEvent > 12) { _pxFast += _winPx; _cntFast += _winCnt; }
        }
        _winPx = _winCnt = 0; _winEvents = 0;

        if (_cnt > 200)
        {
            double ratio = (_cntSlow > 100 ? _pxSlow / _cntSlow : _px / _cnt);
            MultiplierText = $"{ratio:0.00}x pointer pixels per mouse count";
            DpiText = $"Effective DPI on the desktop: about {MouseDpi * ratio:0} ({MouseDpi} × {ratio:0.00})";
            if (_cntSlow > 100 && _cntFast > 100)
            {
                double slow = _pxSlow / _cntSlow, fast = _pxFast / _cntFast;
                AccelText = Math.Abs(fast / slow - 1) > 0.12 ? $"Acceleration: on (fast moves go {fast / slow:0.00}x further)" : "Acceleration: off (consistent)";
            }
            else AccelText = "Move slowly, then fast, to check acceleration";
        }

        long now = Environment.TickCount64;
        while (_stamps.Count > 0 && now - _stamps.Peek() > 1000) _stamps.Dequeue();
        if (_stamps.Count > 40)
        {
            var arr = _stamps.ToArray();
            // Rate over the continuous stretch of movement in the last second (skip pauses).
            double span = (arr[^1] - arr[0]) / 1000.0;
            double hz = span > 0.1 ? (arr.Length - 1) / span : 0;
            if (hz > 0) PollingText = hz >= 950 ? $"Polling about {Math.Round(hz / 125) * 125:0} Hz" : $"Polling about {hz:0} Hz";
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var panel = (Brush)Application.Current.FindResource("Panel2Brush");
        var border = (Brush)Application.Current.FindResource("BorderStrongBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        dc.DrawRoundedRectangle(panel, new Pen(border, 1), new Rect(0.5, 0.5, w - 1, h - 1), 12, 12);
        var tf = new FormattedText(_trail.Count == 0 ? "Move your mouse here. Slow, then fast. Click to start over." : "Click to start over", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(tf, new Point(14, 12));
        for (int i = 1; i < _trail.Count; i++)
        {
            var a = accent.Clone(); a.Opacity = i / (double)_trail.Count;
            dc.DrawLine(new Pen(a, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, _trail[i - 1], _trail[i]);
        }
    }
}

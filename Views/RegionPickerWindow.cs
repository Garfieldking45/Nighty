using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Nighty.Native;
using Nighty.Services;

namespace Nighty.Views;

/// <summary>
/// A see-through sheet over every screen. Drag a rectangle with the mouse; the result is in physical screen pixels (what the screen
/// copy uses), whatever the display scaling. Esc or a click without dragging cancels.
/// </summary>
public sealed class RegionPickerWindow : Window
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private readonly Canvas _canvas = new();
    private readonly Border _box = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(255, 140, 26)), BorderThickness = new Thickness(2), Background = new SolidColorBrush(Color.FromArgb(40, 255, 140, 26)), Visibility = Visibility.Collapsed };
    private POINT _start;
    private bool _dragging;

    internal HitReader.Region? Result { get; private set; }

    public RegionPickerWindow(string hint)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        Topmost = true; ShowInTaskbar = false; Cursor = Cursors.Cross;
        Left = 0; Top = 0; Width = 10; Height = 10;
        Content = _canvas;
        var label = new TextBlock
        {
            Text = hint, Foreground = Brushes.White, FontSize = 20, FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(14, 8, 14, 8), Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 30)),
        };
        Canvas.SetLeft(label, 40); Canvas.SetTop(label, 40);
        _canvas.Children.Add(label);
        _canvas.Children.Add(_box);

        SourceInitialized += (_, _) =>
        {
            // Cover the whole virtual desktop in physical pixels, so it also works across monitors with different scaling.
            int x = ScreenGrabber.GetSystemMetrics(76), y = ScreenGrabber.GetSystemMetrics(77), w = ScreenGrabber.GetSystemMetrics(78), h = ScreenGrabber.GetSystemMetrics(79);
            NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_SHOWWINDOW);
        };
        Loaded += (_, _) => { Activate(); Focus(); };
        MouseLeftButtonDown += (_, e) =>
        {
            GetCursorPos(out _start);
            _dragging = true;
            CaptureMouse();
            _box.Visibility = Visibility.Visible;
            Place(_start, _start);
            e.Handled = true;
        };
        MouseMove += (_, _) => { if (_dragging) { GetCursorPos(out var now); Place(_start, now); } };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            GetCursorPos(out var end);
            int x = Math.Min(_start.X, end.X), y = Math.Min(_start.Y, end.Y), w = Math.Abs(end.X - _start.X), h = Math.Abs(end.Y - _start.Y);
            if (w >= 4 && h >= 4) Result = new HitReader.Region(x, y, w, h);
            Close();
            e.Handled = true;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Draws the rectangle between two screen-pixel points.</summary>
    private void Place(POINT a, POINT b)
    {
        var p1 = PointFromScreen(new Point(a.X, a.Y));
        var p2 = PointFromScreen(new Point(b.X, b.Y));
        Canvas.SetLeft(_box, Math.Min(p1.X, p2.X)); Canvas.SetTop(_box, Math.Min(p1.Y, p2.Y));
        _box.Width = Math.Abs(p2.X - p1.X); _box.Height = Math.Abs(p2.Y - p1.Y);
    }
}

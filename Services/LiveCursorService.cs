using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nighty.Controls;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Shows the chosen crosshair straight away, without waiting for Roblox to restart. Roblox only reads its cursor picture when
/// it starts, so until then Nighty draws the same picture in a click-through window that follows the pointer (in first person
/// the game keeps the pointer in the middle of its window, so the crosshair sits at the centre). Nothing is injected into
/// Roblox. The window disappears for any Roblox that started after the picture was written, because that one already has it.
/// </summary>
public sealed class LiveCursorService
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(8) };
    private LiveCursorWindow? _win;
    private BitmapSource? _image;
    private int _px;
    private bool _centered;
    private bool _forever;                       // the file could not be written, so the overlay is the only way to see it
    private long _lastCheck;
    private bool _show;

    public bool IsShowing => _win != null && _show;

    public void Start()
    {
        _timer.Tick -= Tick; _timer.Tick += Tick;
        Refresh();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _win?.Close();
        _win = null;
    }

    /// <summary>Call when the cursor Roblox should have changes (applied, restored, size or brightness edited).</summary>
    public void Refresh()
    {
        var r = Svc.S.Roblox;
        var spec = string.IsNullOrEmpty(r.CursorId) ? null : CursorLibrary.Find(r.CursorId);
        if (spec == null || !spec.CenterHotspot) { Hide(); _image = null; _timer.Stop(); return; }
        _px = RobloxAssetsService.SizeToPx(r.CursorSize);
        _image = CursorRenderer.Render(spec, _px, r.CursorBrightness);
        _centered = spec.CenterHotspot;
        _lastCheck = 0;
        if (!_timer.IsEnabled) _timer.Start();
    }

    /// <summary>Marks the picture as just written. <paramref name="fileWritten"/> false keeps the overlay for as long as Nighty runs.</summary>
    public void Applied(bool fileWritten)
    {
        Svc.S.Roblox.CursorAppliedUtc = DateTime.UtcNow;
        _forever = !fileWritten;
        Refresh();
    }

    public void Cleared()
    {
        Svc.S.Roblox.CursorAppliedUtc = null;
        _forever = false;
        Refresh();
    }

    /// <summary>True while a Roblox is running that was started before the picture was written (so it still shows the old one).</summary>
    private bool NeedsOverlay()
    {
        if (_forever) return Process.GetProcessesByName(RobloxService.ProcessName).Length > 0;
        var applied = Svc.S.Roblox.CursorAppliedUtc;
        if (applied == null) return false;
        foreach (var p in Process.GetProcessesByName(RobloxService.ProcessName))
        {
            try { if (p.StartTime.ToUniversalTime() < applied.Value) return true; }
            catch { /* process went away or access denied */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    private void Tick(object? s, EventArgs e)
    {
        if (_image == null) { Hide(); return; }
        long now = Environment.TickCount64;
        if (now - _lastCheck > 400)
        {
            _lastCheck = now;
            _show = Svc.Roblox.IsForeground && NeedsOverlay();
        }
        if (!_show) { Hide(); return; }

        if (_win == null) { _win = new LiveCursorWindow(); _win.Show(); }
        _win.SetImage(_image);
        NativeMethods.GetCursorPos(out var pt);
        _win.PlaceAt(pt.X, pt.Y, _px, _centered);
    }

    private void Hide()
    {
        _show = false;
        if (_win != null) { _win.Close(); _win = null; }
    }
}

/// <summary>A click-through, topmost window holding one picture, positioned in physical pixels.</summary>
public sealed class LiveCursorWindow : Window
{
    private readonly Image _img = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private IntPtr _hwnd;
    private BitmapSource? _current;

    public LiveCursorWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -32000;   // off screen until the first placement
        Width = Height = 1;
        Content = _img;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        };
    }

    public void SetImage(BitmapSource img)
    {
        if (ReferenceEquals(_current, img)) return;
        _current = img;
        _img.Source = img;
    }

    /// <summary>Puts the picture's click point (its centre, or its top-left corner) on the given screen pixel.</summary>
    public void PlaceAt(int x, int y, int size, bool centered)
    {
        if (_hwnd == IntPtr.Zero) return;
        int left = centered ? x - size / 2 : x, top = centered ? y - size / 2 : y;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, left, top, size, size, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}

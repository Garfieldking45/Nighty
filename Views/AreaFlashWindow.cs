using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Nighty.Native;
using Nighty.Services;

namespace Nighty.Views;

/// <summary>
/// An orange outline drawn over a screen rectangle (physical pixels) that the mouse passes straight through and that never takes focus.
/// Used to show exactly where Nighty is reading the hit counter from. The caller closes it.
/// </summary>
public sealed class AreaFlashWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    internal AreaFlashWindow(HitReader.Region r)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Left = 0; Top = 0; Width = 10; Height = 10;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 140, 26)), BorderThickness = new Thickness(4),
            Background = new SolidColorBrush(Color.FromArgb(36, 255, 140, 26)),
        };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            const int pad = 6;
            NativeMethods.SetWindowPos(h, NativeMethods.HWND_TOPMOST, r.X - pad, r.Y - pad, r.W + pad * 2, r.H + pad * 2, NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        };
    }
}

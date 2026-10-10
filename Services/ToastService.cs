using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>
/// Small click-through notifications in the bottom-right corner, shown above the game for a moment so a toggle can be
/// confirmed without alt-tabbing. Never takes focus.
/// </summary>
public sealed class ToastService
{
    private ToastWindow? _win;
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1800) };

    public ToastService() { _hide.Tick += (_, _) => { _hide.Stop(); _win?.FadeOut(); }; }

    /// <summary>Shows "name: ON/OFF". Safe to call from any thread.</summary>
    public void Toggled(string name, bool on) => Show(name, on ? "ON" : "OFF", on);

    public void Show(string title, string detail, bool good = true)
    {
        if (!Svc.S.General.ShowNotifications) return;
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(() =>
        {
            try
            {
                _win ??= new ToastWindow();
                _win.Set(title, detail, good);
                _hide.Stop(); _hide.Start();
            }
            catch (Exception ex) { Log.Warn("Notification failed", ex); }
        });
    }

    public void Shutdown() { _hide.Stop(); _win?.Close(); _win = null; }
}

internal sealed class ToastWindow : Window
{
    private readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _detail = new() { FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 0) };

    public ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_title); row.Children.Add(_detail);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 9, 14, 9), Child = row,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)ex);
        };
        SizeChanged += (_, _) => Place();
        Opacity = 0;
        Show();
    }

    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 20;
        Top = wa.Bottom - ActualHeight - 20;
    }

    public void Set(string title, string detail, bool good)
    {
        _title.Text = title;
        _detail.Text = detail;
        _detail.Foreground = new SolidColorBrush(good ? Color.FromRgb(0x4A, 0xDE, 0x80) : Color.FromRgb(0xF8, 0x71, 0x71));
        UpdateLayout(); Place();
        if (!IsVisible) Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    public void FadeOut() => BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
}

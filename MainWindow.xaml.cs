using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nighty.Controls;
using Nighty.Native;
using Nighty.Services;
using Nighty.ViewModels;

namespace Nighty;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RestoreBounds_();
        SourceInitialized += (_, _) => ApplyFrameStyle();
        StateChanged += (_, _) => UpdateMaxState();
        Closing += (_, _) => SaveBounds();
        Loaded += (_, _) =>
        {
            Backdrop.Refresh();
            UpdatePin();
            Svc.S.General.PropertyChanged += OnGeneralChanged;
        };
        Closed += (_, _) => Svc.S.General.PropertyChanged -= OnGeneralChanged;
        ThemeService.Changed += ApplyFrameStyle;
        UpdateMaxState();
    }

    private void OnGeneralChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Models.GeneralSettings.BackgroundImage):
            case nameof(Models.GeneralSettings.BackgroundFit):
            case nameof(Models.GeneralSettings.BackgroundStrength):
                Backdrop.Refresh(); break;
            case nameof(Models.GeneralSettings.AlwaysOnTop):
                UpdatePin(); break;
        }
    }

    /// <summary>Fades the window in; used by the loading screen hand-over.</summary>
    public void FadeIn()
    {
        if (!Fx.Enabled) { Opacity = 1; return; }
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380)) { EasingFunction = Fx.EaseOut });
    }

    private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 1 && DataContext is MainViewModel vm) vm.OpenHitCommand.Execute(e.AddedItems[0]);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Key == Key.Escape) { vm.SearchText = ""; e.Handled = true; }
        else if (e.Key == Key.Enter && vm.SearchResults.Count > 0) { vm.OpenHitCommand.Execute(vm.SearchResults[0]); e.Handled = true; }
    }

    // ---------------- custom caption ----------------
    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Pin_Click(object sender, RoutedEventArgs e) => Svc.S.General.AlwaysOnTop = !Svc.S.General.AlwaysOnTop;

    private void UpdatePin()
    {
        bool on = Svc.S.General.AlwaysOnTop;
        PinButton.Foreground = on ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("MutedBrush");
        PinButton.ToolTip = on ? "Nighty stays on top (click to turn off)" : "Keep Nighty on top";
    }

    /// <summary>A maximized chrome-less window overhangs the screen by its resize border; pull the content back in.</summary>
    private void UpdateMaxState()
    {
        bool max = WindowState == WindowState.Maximized;
        Frame.Margin = max ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1, SystemParameters.WindowResizeBorderThickness.Top + 1,
            SystemParameters.WindowResizeBorderThickness.Right + 1, SystemParameters.WindowResizeBorderThickness.Bottom + 1) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restore down" : "Maximize";
    }

    /// <summary>Rounded corners on Windows 11 and a dark/light frame that matches the theme.</summary>
    private void ApplyFrameStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = ThemeService.IsLight ? 0 : 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));     // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2;
        NativeMethods.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));    // DWMWA_WINDOW_CORNER_PREFERENCE = round
        var c = ThemeService.Current("SidebarBrush");
        int border = c.R | c.G << 8 | c.B << 16;
        NativeMethods.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));   // DWMWA_BORDER_COLOR (Windows 11)
    }

    // ---------------- remember where we were ----------------
    private void RestoreBounds_()
    {
        var g = Svc.S.General;
        if (!g.RememberWindow) return;
        if (g.WindowWidth >= MinWidth && g.WindowHeight >= MinHeight)
        {
            var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            // Only restore a position that is still (mostly) on a connected screen.
            if (area.Contains(new Point(g.WindowLeft + 80, g.WindowTop + 20)) && area.Contains(new Point(g.WindowLeft + g.WindowWidth - 80, g.WindowTop + 60)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = g.WindowLeft; Top = g.WindowTop; Width = g.WindowWidth; Height = g.WindowHeight;
            }
        }
        if (g.WindowMaximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        try
        {
            var g = Svc.S.General;
            if (DataContext is MainViewModel vm) g.LastPage = vm.Selected.Title;
            if (!g.RememberWindow) return;
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            g.WindowMaximized = WindowState == WindowState.Maximized;
            if (b.Width > 0 && b.Height > 0) { g.WindowLeft = b.Left; g.WindowTop = b.Top; g.WindowWidth = b.Width; g.WindowHeight = b.Height; }
        }
        catch (Exception ex) { Log.Warn("Saving window position failed", ex); }
    }
}

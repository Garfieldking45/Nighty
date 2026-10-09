using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Nighty.ViewModels;
using Nighty.Native;

namespace Nighty;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
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

    /// <summary>Uses the native frame (so window controls, snapping and DPI behave) with a dark caption on Windows 10/11.</summary>
    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));       // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x000C0A0A;                                                    // COLORREF (BGR) of the app background
        NativeMethods.DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));    // DWMWA_CAPTION_COLOR (Windows 11)
        int text = 0x00F7F4F4;
        NativeMethods.DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));       // DWMWA_TEXT_COLOR
    }
}

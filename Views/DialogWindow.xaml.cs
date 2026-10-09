using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nighty.Models;
using Nighty.Services;

namespace Nighty.Views;

public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, string okText, bool showCancel = true, bool danger = false, UIElement? extra = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (danger) OkButton.Background = OkButton.BorderBrush = (Brush)FindResource("DangerBrush");
        if (extra != null) ExtraHost.Content = extra; else ExtraHost.Visibility = Visibility.Collapsed;
        Owner = Application.Current.MainWindow is { IsVisible: true } w ? w : null;
        if (Owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnOk(object s, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object s, RoutedEventArgs e) => DialogResult = false;
    private void OnDrag(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}

/// <summary>Helpers for the app's modal dialogs.</summary>
public static class Dialogs
{
    public static bool Confirm(string title, string message, string okText, bool danger = false)
        => new DialogWindow(title, message, okText, true, danger).ShowDialog() == true;

    public static void Info(string title, string message)
        => new DialogWindow(title, message, "OK", false).ShowDialog();

    /// <summary>Waits for the user to press a key and returns its virtual-key code (null if cancelled).</summary>
    public static int? CaptureKey(string title, string message)
    {
        var readout = new TextBlock { Text = "Waiting for a key…", FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var box = new Border
        {
            Child = readout, Padding = new Thickness(0, 16, 0, 16), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.FindResource("Panel2Brush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderStrongBrush"), BorderThickness = new Thickness(1),
        };
        var dlg = new DialogWindow(title, message, "Add", true, false, box);
        int vk = 0;
        dlg.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Escape or Key.Enter or Key.Tab) return;
            vk = KeyInterop.VirtualKeyFromKey(key);
            readout.Text = Hotkeys.KeyName(vk);
            e.Handled = true;
        };
        return dlg.ShowDialog() == true && vk > 0 ? vk : null;
    }
}

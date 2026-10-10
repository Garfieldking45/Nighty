using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nighty.Views;

/// <summary>A see-through full-screen layer: click anywhere to pick that screen position. Right-click or Esc cancels.</summary>
public sealed class SpotPicker : Window
{
    public (int X, int Y)? Result { get; private set; }

    public SpotPicker()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));   // almost invisible, but still catches the mouse
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        Content = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 60, 0, 0),
            Padding = new Thickness(18, 10, 18, 10), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x14)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = "Click the spot on your screen. Right-click or Esc cancels.", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold },
        };
        MouseLeftButtonDown += (_, e) =>
        {
            var p = PointToScreen(e.GetPosition(this));   // physical pixels, what SetCursorPos uses
            Result = ((int)Math.Round(p.X), (int)Math.Round(p.Y));
            Close();
        };
        MouseRightButtonDown += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => { Activate(); Focus(); };
    }
}

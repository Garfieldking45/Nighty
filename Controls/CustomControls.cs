using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;

namespace Nighty.Controls;

/// <summary>Row with title/description on the left and arbitrary content (usually a switch) on the right.</summary>
public class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow));
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow));
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    static SettingRow() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(typeof(SettingRow)));
}

/// <summary>Click, then press the key (and modifiers) to bind. Esc cancels, Backspace/Delete clears. Mouse side buttons and middle click can be bound too.</summary>
public class HotkeyBox : Button
{
    public static readonly DependencyProperty VkProperty = DependencyProperty.Register(nameof(Vk), typeof(int), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));
    public static readonly DependencyProperty ModsProperty = DependencyProperty.Register(nameof(Mods), typeof(int), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Refresh()));
    public static readonly DependencyProperty AllowModifiersProperty = DependencyProperty.Register(nameof(AllowModifiers), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(true));

    private bool _capturing;

    public int Vk { get => (int)GetValue(VkProperty); set => SetValue(VkProperty, value); }
    public int Mods { get => (int)GetValue(ModsProperty); set => SetValue(ModsProperty, value); }
    public bool AllowModifiers { get => (bool)GetValue(AllowModifiersProperty); set => SetValue(AllowModifiersProperty, value); }

    public HotkeyBox()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        MinWidth = 120;
        Refresh();
    }

    private void Refresh() => Content = _capturing ? "Press a key…" : Hotkeys.Format(Vk, Mods);

    protected override void OnClick()
    {
        base.OnClick();
        _capturing = true;
        Svc.Hotkeys.Suspended = true;
        Refresh();
        Focus();
        Mouse.Capture(this, CaptureMode.SubTree);   // lets side buttons be bound from anywhere
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (!_capturing) { base.OnPreviewMouseDown(e); return; }
        int vk = e.ChangedButton switch { MouseButton.XButton1 => 0x05, MouseButton.XButton2 => 0x06, MouseButton.Middle => 0x04, _ => 0 };
        if (vk != 0) { e.Handled = true; Vk = vk; Mods = 0; End(); return; }
        End();   // any other click cancels
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (_capturing && !IsKeyboardFocused) End(); }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); End(); }

    private void End()
    {
        if (!_capturing) return;
        _capturing = false;
        if (Mouse.Captured == this) Mouse.Capture(null);
        Svc.Hotkeys.Suspended = false;
        Refresh();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape) { End(); return; }
        if (key is Key.Back or Key.Delete) { Vk = 0; Mods = 0; End(); return; }
        int mods = 0;
        if (AllowModifiers)
        {
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods |= Hotkeys.Ctrl;
            if (m.HasFlag(ModifierKeys.Alt)) mods |= Hotkeys.Alt;
            if (m.HasFlag(ModifierKeys.Shift)) mods |= Hotkeys.Shift;
            if (m.HasFlag(ModifierKeys.Windows)) mods |= Hotkeys.Win;
        }
        Vk = KeyInterop.VirtualKeyFromKey(key);
        Mods = mods;
        End();
    }
}

/// <summary>Draws the click waveform: button down for DutyCycle % of each click period.</summary>
public class DutyWaveform : FrameworkElement
{
    public static readonly DependencyProperty DutyCycleProperty = DependencyProperty.Register(nameof(DutyCycle), typeof(double), typeof(DutyWaveform),
        new FrameworkPropertyMetadata(50.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double DutyCycle { get => (double)GetValue(DutyCycleProperty); set => SetValue(DutyCycleProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var border = (Brush)Application.Current.FindResource("BorderBrush");
        var muted = (Brush)Application.Current.FindResource("DimBrush");
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        const int cycles = 4;
        double pad = 8, top = 26, bottom = h - 10, period = (w - 2 * pad) / cycles, duty = Math.Clamp(DutyCycle, 1, 99) / 100.0;

        dc.DrawLine(new Pen(border, 1), new Point(pad, bottom), new Point(w - pad, bottom));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double x = pad;
            ctx.BeginFigure(new Point(x, bottom), true, true);
            for (int i = 0; i < cycles; i++)
            {
                ctx.LineTo(new Point(x, top), true, false);
                ctx.LineTo(new Point(x + period * duty, top), true, false);
                ctx.LineTo(new Point(x + period * duty, bottom), true, false);
                ctx.LineTo(new Point(x + period, bottom), true, false);
                x += period;
            }
            ctx.LineTo(new Point(w - pad, bottom), true, false);
        }
        geo.Freeze();
        var fill = new SolidColorBrush(Color.FromArgb(0x30, 0x3B, 0x82, 0xF6));
        dc.DrawGeometry(fill, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, geo);

        var tf = new FormattedText($"Button held {DutyCycle:0}% of each click", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(tf, new Point(pad, 0));
    }
}

/// <summary>Banner for notices. Kind: Info, Warning, Error, Success.</summary>
public class InfoBanner : Border
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InfoBanner), new PropertyMetadata("", (d, _) => ((InfoBanner)d).Rebuild()));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(InfoBanner), new PropertyMetadata(StatusKind.Info, (d, _) => ((InfoBanner)d).Rebuild()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public InfoBanner()
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 11, 14, 11);
        Margin = new Thickness(0, 0, 0, 14);
        Rebuild();
    }

    private void Rebuild()
    {
        var (glyph, color) = Kind switch
        {
            StatusKind.Warning => ("", Color.FromRgb(0xF5, 0x9E, 0x0B)),
            StatusKind.Error => ("", Color.FromRgb(0xEF, 0x44, 0x44)),
            StatusKind.Success => ("", Color.FromRgb(0x22, 0xC5, 0x5E)),
            _ => ("", Color.FromRgb(0x3B, 0x82, 0xF6)),
        };
        Background = new SolidColorBrush(Color.FromArgb(0x14, color.R, color.G, color.B));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 15,
            Foreground = new SolidColorBrush(color), Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top,
        });
        var tb = new TextBlock { Text = Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = (Brush)Application.Current.FindResource("TextBrush"), LineHeight = 18 };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);
        Child = grid;
    }
}

/// <summary>Small coloured status dot.</summary>
public class StatusDot : Border
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusDot),
        new PropertyMetadata(StatusKind.Neutral, (d, _) => ((StatusDot)d).Update()));
    public StatusKind Kind { get => (StatusKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public StatusDot()
    {
        Width = Height = 8; CornerRadius = new CornerRadius(4); VerticalAlignment = VerticalAlignment.Center;
        Update();
    }

    private void Update() => Background = (Brush)new StatusBrushConverter().Convert(Kind, typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nighty.Controls;

/// <summary>
/// A big borderless number you can click and type into. Enter or leaving the box applies it (clamped to the range and rounded to
/// <see cref="Decimals"/>); Escape or text that is not a number puts the old value back.
/// </summary>
public class NumberEntry : TextBox
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(NumberEntry),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumberEntry)d).UpdateText(), Coerce));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberEntry), new PropertyMetadata(0.0, OnRangeChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberEntry), new PropertyMetadata(100.0, OnRangeChanged));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(NumberEntry), new PropertyMetadata(0, (d, _) => ((NumberEntry)d).UpdateText()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

    public NumberEntry()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        MinWidth = 60;
        VerticalContentAlignment = VerticalAlignment.Center;
        ToolTip = "Type a value and press Enter";
        UpdateText();
    }

    private static object Coerce(DependencyObject d, object v)
    {
        var s = (NumberEntry)d;
        double x = double.IsNaN((double)v) ? s.Minimum : Math.Round((double)v, Math.Max(0, s.Decimals));
        return Math.Clamp(x, s.Minimum, Math.Max(s.Minimum, s.Maximum));
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => d.CoerceValue(ValueProperty);

    private void UpdateText() => Text = Value.ToString("F" + Math.Max(0, Decimals), CultureInfo.CurrentCulture);

    /// <summary>Applies what was typed, then shows the value that was actually accepted.</summary>
    internal void Commit()
    {
        var t = Text.Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
            double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            Value = v;
        UpdateText();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); SelectAll(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); Commit(); }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // The first click selects the whole number so typing replaces it.
        if (!IsKeyboardFocusWithin) { Focus(); e.Handled = true; }
        else base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Commit(); SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateText(); SelectAll(); e.Handled = true; }
        else base.OnPreviewKeyDown(e);
    }
}

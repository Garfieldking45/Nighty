using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Nighty.Controls;

/// <summary>Slider with − / + buttons and a value readout, all bound to the same <see cref="Value"/>.</summary>
public partial class ValueStepper : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ValueStepper),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ValueStepper)d).UpdateText(), Coerce));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(ValueStepper), new PropertyMetadata(0.0, OnRangeChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(ValueStepper), new PropertyMetadata(100.0, OnRangeChanged));
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(nameof(Step), typeof(double), typeof(ValueStepper), new PropertyMetadata(1.0));
    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(ValueStepper), new PropertyMetadata(0, (d, _) => ((ValueStepper)d).UpdateText()));
    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(ValueStepper), new PropertyMetadata("", (d, _) => ((ValueStepper)d).UpdateText()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    public ValueStepper() { InitializeComponent(); UpdateText(); }

    private static object Coerce(DependencyObject d, object v)
    {
        var s = (ValueStepper)d;
        double x = Math.Round((double)v, Math.Max(0, s.Decimals));
        return Math.Clamp(x, s.Minimum, Math.Max(s.Minimum, s.Maximum));
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => d.CoerceValue(ValueProperty);

    private void UpdateText() =>
        ValueText.Text = Value.ToString("F" + Math.Max(0, Decimals), CultureInfo.CurrentCulture) + Suffix;

    private void OnDecrease(object sender, RoutedEventArgs e) => Value = Math.Round(Value - Step, 4);
    private void OnIncrease(object sender, RoutedEventArgs e) => Value = Math.Round(Value + Step, 4);
}

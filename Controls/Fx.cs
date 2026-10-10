using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nighty.Services;

namespace Nighty.Controls;

public enum FxMode { None, Hover, Switch, Nav, Badge }

/// <summary>
/// Small, code-driven motion for control templates. Templates only declare named parts (HoverLayer, PressScale, Thumb...),
/// this class animates them, and snaps instantly when animations are turned off in Settings.
/// </summary>
public static class Fx
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached("Mode", typeof(FxMode), typeof(Fx),
        new PropertyMetadata(FxMode.None, OnModeChanged));
    public static FxMode GetMode(DependencyObject o) => (FxMode)o.GetValue(ModeProperty);
    public static void SetMode(DependencyObject o, FxMode v) => o.SetValue(ModeProperty, v);

    public static bool Enabled
    {
        get { try { return Svc.Settings == null || Svc.S.General.Animations; } catch { return true; } }
    }

    public static TimeSpan Ms(int ms) => Enabled ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
    public static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction Back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };

    /// <summary>Animates (or snaps) a dependency property.</summary>
    public static void To(IAnimatable target, DependencyProperty p, double value, int ms = 140, IEasingFunction? ease = null, int delayMs = 0)
    {
        if (!Enabled || ms <= 0)
        {
            target.BeginAnimation(p, null);
            ((DependencyObject)target).SetValue(p, value);
            return;
        }
        target.BeginAnimation(p, new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? EaseOut, BeginTime = TimeSpan.FromMilliseconds(delayMs) },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        switch ((FxMode)e.NewValue)
        {
            case FxMode.Hover: HookHover(fe); break;
            case FxMode.Switch: HookSwitch(fe); break;
            case FxMode.Nav: HookNav(fe); break;
            case FxMode.Badge: HookBadge(fe); break;
        }
    }

    private static T? Part<T>(Control c, string name) where T : class
    {
        c.ApplyTemplate();
        return c.Template?.FindName(name, c) as T;
    }

    // ---------------- hover glow + press squish ----------------
    private static void HookHover(FrameworkElement fe)
    {
        if (fe is not Control c) return;
        c.MouseEnter += (_, _) => { if (Part<UIElement>(c, "HoverLayer") is { } h && c.IsEnabled) To(h, UIElement.OpacityProperty, 1, 120); };
        c.MouseLeave += (_, _) =>
        {
            if (Part<UIElement>(c, "HoverLayer") is { } h) To(h, UIElement.OpacityProperty, 0, 220);
            Press(c, false);
        };
        c.PreviewMouseLeftButtonDown += (_, _) => Press(c, true);
        c.PreviewMouseLeftButtonUp += (_, _) => Press(c, false);
        if (c is ButtonBase b && c is not ToggleButton) b.Click += (_, _) => Sfx.Click();
    }

    private static void Press(Control c, bool down)
    {
        if (Part<ScaleTransform>(c, "PressScale") is not { } s) return;
        double v = down ? 0.965 : 1;
        if (!Enabled) { s.BeginAnimation(ScaleTransform.ScaleXProperty, null); s.BeginAnimation(ScaleTransform.ScaleYProperty, null); s.ScaleX = s.ScaleY = 1; return; }
        var dur = TimeSpan.FromMilliseconds(down ? 70 : 180);
        var ease = down ? EaseOut : Back;
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(v, dur) { EasingFunction = ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(v, dur) { EasingFunction = ease });
    }

    // ---------------- toggle switch ----------------
    private static void HookSwitch(FrameworkElement fe)
    {
        if (fe is not ToggleButton t) return;
        t.Loaded += (_, _) => ApplySwitch(t, false);
        t.Checked += (_, _) => { ApplySwitch(t, true); Sfx.Toggle(true); };
        t.Unchecked += (_, _) => { ApplySwitch(t, true); Sfx.Toggle(false); };
        t.MouseEnter += (_, _) => { if (Part<UIElement>(t, "HoverLayer") is { } h) To(h, UIElement.OpacityProperty, 1, 120); };
        t.MouseLeave += (_, _) => { if (Part<UIElement>(t, "HoverLayer") is { } h) To(h, UIElement.OpacityProperty, 0, 200); };
    }

    private static void ApplySwitch(ToggleButton t, bool animate)
    {
        bool on = t.IsChecked == true;
        if (Part<TranslateTransform>(t, "ThumbShift") is { } shift)
            ToTransform(shift, TranslateTransform.XProperty, on ? 20 : 0, animate ? 200 : 0, Back);
        if (Part<UIElement>(t, "OnTrack") is { } track) To(track, UIElement.OpacityProperty, on ? 1 : 0, animate ? 180 : 0);
        if (Part<ScaleTransform>(t, "ThumbScale") is { } sc)
        {
            ToTransform(sc, ScaleTransform.ScaleXProperty, on ? 1.08 : 1, animate ? 200 : 0, Back);
            ToTransform(sc, ScaleTransform.ScaleYProperty, on ? 1.08 : 1, animate ? 200 : 0, Back);
        }
    }

    private static void ToTransform(Animatable target, DependencyProperty p, double value, int ms, IEasingFunction ease)
    {
        if (!Enabled || ms <= 0) { target.BeginAnimation(p, null); target.SetValue(p, value); return; }
        target.BeginAnimation(p, new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    // ---------------- sidebar item ----------------
    private static void HookNav(FrameworkElement fe)
    {
        if (fe is not ListBoxItem item) return;
        item.Loaded += (_, _) => ApplyNav(item, false);
        item.Selected += (_, _) => ApplyNav(item, true);
        item.Unselected += (_, _) => ApplyNav(item, true);
        item.MouseEnter += (_, _) => { if (Part<UIElement>(item, "HoverLayer") is { } h) To(h, UIElement.OpacityProperty, 1, 120); };
        item.MouseLeave += (_, _) => { if (Part<UIElement>(item, "HoverLayer") is { } h) To(h, UIElement.OpacityProperty, 0, 200); };
        item.PreviewMouseLeftButtonDown += (_, _) => Press(item, true);
        item.PreviewMouseLeftButtonUp += (_, _) => Press(item, false);
    }

    private static void ApplyNav(ListBoxItem item, bool animate)
    {
        if (Part<ScaleTransform>(item, "BarScale") is { } bar)
            ToTransform(bar, ScaleTransform.ScaleYProperty, item.IsSelected ? 1 : 0, animate ? 260 : 0, Back);
        if (Part<TranslateTransform>(item, "ContentShift") is { } shift)
            ToTransform(shift, TranslateTransform.XProperty, item.IsSelected ? 3 : 0, animate ? 220 : 0, EaseOut);
    }

    // ---------------- option-card check badge ----------------
    private static void HookBadge(FrameworkElement fe)
    {
        if (fe is not ToggleButton t) return;
        t.Loaded += (_, _) => ApplyBadge(t, false);
        t.Checked += (_, _) => ApplyBadge(t, true);
        t.Unchecked += (_, _) => ApplyBadge(t, true);
        t.MouseEnter += (_, _) => { if (Part<UIElement>(t, "HoverLayer") is { } h) To(h, UIElement.OpacityProperty, 1, 120); };
        t.MouseLeave += (_, _) => { if (Part<UIElement>(t, "HoverLayer") is { } h) To(h, UIElement.OpacityProperty, 0, 200); };
        t.PreviewMouseLeftButtonDown += (_, _) => Press(t, true);
        t.PreviewMouseLeftButtonUp += (_, _) => Press(t, false);
    }

    private static void ApplyBadge(ToggleButton t, bool animate)
    {
        bool on = t.IsChecked == true;
        if (Part<ScaleTransform>(t, "BadgeScale") is { } s)
        {
            ToTransform(s, ScaleTransform.ScaleXProperty, on ? 1 : 0, animate ? 260 : 0, Back);
            ToTransform(s, ScaleTransform.ScaleYProperty, on ? 1 : 0, animate ? 260 : 0, Back);
        }
    }
}

/// <summary>Little synthesized UI sounds (no audio files shipped). Only play when Settings → Sound effects is on.</summary>
public static class Sfx
{
    private static System.Media.SoundPlayer? _click, _on, _off, _start, _stop;

    private static bool Enabled { get { try { return Svc.Settings != null && Svc.S.General.SoundEffects; } catch { return false; } } }

    public static void Click() { if (Enabled) Play(ref _click, 880, 28, 0.18); }
    public static void Toggle(bool on) { if (!Enabled) return; if (on) Play(ref _on, 1175, 45, 0.2); else Play(ref _off, 740, 45, 0.2); }
    public static void Start() { if (Enabled) Play(ref _start, 1320, 80, 0.22); }
    private static System.Media.SoundPlayer? _clip;
    /// <summary>The clip-saved chime. Controlled by its own setting on the Record page, not the general sound switch.</summary>
    public static void Clip() => Play(ref _clip, 1568, 150, 0.28);
    public static void Stop() { if (Enabled) Play(ref _stop, 660, 90, 0.22); }

    private static void Play(ref System.Media.SoundPlayer? slot, double hz, int ms, double volume)
    {
        try
        {
            slot ??= Make(hz, ms, volume);
            slot.Play();
        }
        catch { /* sound is optional */ }
    }

    /// <summary>16-bit mono PCM sine with a short attack and decay, wrapped in a WAV header.</summary>
    private static System.Media.SoundPlayer Make(double hz, int ms, double volume)
    {
        const int rate = 22050;
        int n = rate * ms / 1000;
        var ms2 = new MemoryStream();
        using (var w = new BinaryWriter(ms2, System.Text.Encoding.ASCII, true))
        {
            w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 2); w.Write("WAVEfmt "u8.ToArray());
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, i / (rate * 0.004)) * Math.Pow(1 - (double)i / n, 1.6);
                w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * env * volume * short.MaxValue));
            }
        }
        ms2.Position = 0;
        var p = new System.Media.SoundPlayer(ms2);
        p.Load();
        return p;
    }
}

/// <summary>
/// Hosts the current page. When the page changes it fades and slides in, and the cards on it rise in one after another.
/// </summary>
public class PageHost : ContentControl
{
    private readonly TranslateTransform _shift = new();
    private int _generation;

    public PageHost()
    {
        RenderTransform = _shift;
        IsTabStop = false;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (oldContent == null || newContent == null || !Fx.Enabled) return;
        int gen = ++_generation;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (gen == _generation) Reveal(); });
    }

    private void Reveal()
    {
        UpdateLayout();
        var cards = new List<(Border Card, double Y)>();
        Collect(this, cards, 0);
        cards.Sort((a, b) => a.Y.CompareTo(b.Y));
        int i = 0;
        foreach (var (card, _) in cards.Take(9))
        {
            var t = new TranslateTransform(0, 22);
            card.RenderTransform = t;
            card.Opacity = 0;
            int delay = 40 + i++ * 45;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Fx.EaseOut });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = Fx.EaseOut });
        }
        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(cards.Count == 0 ? 260 : 120)) { EasingFunction = Fx.EaseOut });
        if (cards.Count == 0)
        {
            _shift.Y = 14;
            _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = Fx.EaseOut });
        }
    }

    private void Collect(DependencyObject parent, List<(Border, double)> list, int depth)
    {
        var card = Application.Current.TryFindResource("Card") as Style;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int k = 0; k < n; k++)
        {
            var child = VisualTreeHelper.GetChild(parent, k);
            if (child is Border b && card != null && ReferenceEquals(b.Style, card) && b.IsVisible && b.ActualHeight > 0)
            {
                try { list.Add((b, b.TranslatePoint(new Point(0, 0), this).Y)); } catch { }
                continue;   // do not animate cards nested inside a card
            }
            if (depth < 14 && (child is not UIElement ue || ue.IsVisible)) Collect(child, list, depth + 1);
        }
    }
}

/// <summary>Lists for the overlay option drop-downs.</summary>
public static class OverlayChoices
{
    public static readonly Nighty.Models.CpsLayout[] CpsLayouts = Enum.GetValues<Nighty.Models.CpsLayout>();
    public static readonly Nighty.Models.CpsSource[] CpsSources = Enum.GetValues<Nighty.Models.CpsSource>();
    public static readonly Nighty.Models.CpsButtons[] CpsButtonSets = Enum.GetValues<Nighty.Models.CpsButtons>();
}

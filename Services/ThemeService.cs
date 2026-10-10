using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nighty.Models;

namespace Nighty.Services;

public sealed record ThemeChoice(string Id, string Name, string Accent);

/// <summary>One complete palette. Colours are #RRGGBB.</summary>
public sealed record ThemeDef(string Id, string Name, bool Light, string Bg, string Sidebar, string Panel, string Panel2, string Panel3,
    string Border, string BorderStrong, string Text, string Muted, string Dim, string Accent, string Accent2);

/// <summary>
/// Theme engine. Every colour the UI uses lives in one shared <see cref="SolidColorBrush"/> per token (created once, in
/// Application.Resources, referenced with DynamicResource). Switching theme animates those brushes, so the whole
/// window fades into the new palette instead of snapping.
/// </summary>
public static class ThemeService
{
    public static readonly IReadOnlyList<ThemeDef> Palettes = new ThemeDef[]
    {
        new("midnight", "Midnight", false, "#07080B", "#040405", "#0C0D11", "#101117", "#181A22", "#1C1E26", "#2A2D38", "#F5F6F8", "#8D909C", "#5C5F6B", "#4F8EF7", "#9A7CFF"),
        new("light", "Light", true, "#F3F4F8", "#FFFFFF", "#FFFFFF", "#F1F3F8", "#E6E9F2", "#E3E6EE", "#CDD2DF", "#151821", "#5F6577", "#8A90A2", "#3B6FE0", "#7B5CE6"),
        new("cyberpunk", "Cyberpunk", false, "#0B0712", "#07040D", "#120B1E", "#181029", "#231640", "#2A1A4A", "#3E2A6B", "#F4ECFF", "#A08BC8", "#6C5A94", "#FF2E93", "#00E5FF"),
        new("monochrome", "Monochrome", false, "#0A0A0A", "#050505", "#111111", "#161616", "#212121", "#232323", "#343434", "#F2F2F2", "#9A9A9A", "#666666", "#E8E8E8", "#A0A0A0"),
        new("ocean", "Ocean", false, "#06101A", "#040B12", "#0A1824", "#0E1F2E", "#14293C", "#16304A", "#23486A", "#EAF6FF", "#7FA3BF", "#54748D", "#22B8E8", "#3B82F6"),
        new("forest", "Forest", false, "#070D09", "#040805", "#0C1610", "#101D15", "#17291D", "#1C3324", "#2A4A36", "#EEF6F0", "#86A08E", "#587061", "#34C77B", "#A3E635"),
        new("sunset", "Sunset", false, "#110906", "#0B0504", "#1A0E0A", "#22130D", "#301C13", "#3A2216", "#53321F", "#FFF1E8", "#C49A82", "#8A6A58", "#FF7A3D", "#FF3D77"),
        new("sakura", "Sakura", true, "#FFF4F7", "#FFFFFF", "#FFFFFF", "#FFF0F4", "#FCE3EB", "#F5D5DF", "#EBBFCD", "#3A1F2B", "#85626F", "#B093A0", "#E8588A", "#B45CE8"),
    };

    /// <summary>Accent swatches offered on top of any theme. The theme's own accent is added in front.</summary>
    public static readonly IReadOnlyList<ThemeChoice> Accents = new ThemeChoice[]
    {
        new("blue", "Blue", "#4F8EF7"), new("violet", "Violet", "#8B7CF6"), new("green", "Green", "#2FBF8A"),
        new("amber", "Amber", "#F5A524"), new("rose", "Rose", "#F0527A"), new("cyan", "Cyan", "#22B8E8"),
    };

    /// <summary>Kept so older code and saved settings that use accent-only ids keep working.</summary>
    public static IReadOnlyList<ThemeChoice> Themes => Accents;

    public static ThemeChoice Find(string? id) => Accents.FirstOrDefault(t => t.Id == id) ?? Accents[0];
    public static ThemeDef Palette(string? id) => Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];

    private static readonly string[] BrushKeys =
    {
        "BgBrush", "SidebarBrush", "PanelBrush", "Panel2Brush", "Panel3Brush", "BorderBrush", "BorderStrongBrush", "TextBrush", "MutedBrush", "DimBrush",
        "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentSoftBrush", "OnAccentBrush", "NavActiveBrush", "NavHoverBrush", "TileBrush",
        "OptionActiveBrush", "HoverLayerBrush", "ThumbBrush", "ScrollThumbBrush", "ScrollThumbHoverBrush", "DangerSoftBrush", "DangerSoftBorderBrush", "DangerTextBrush",
        "SuccessSoftBrush", "Accent2Brush", "ShadowBrush",
    };

    private static Dictionary<string, Color> _cur = new();
    private static Dictionary<string, Color> _from = new(), _to = new();
    private static (string Key, string A, string B, bool Vertical)[] Gradients =
    {
        ("HeadingBarBrush", "Accent", "Accent2", true), ("ContentBackgroundBrush", "ContentA", "ContentB", false),
        ("PremiumBrush", "PremiumA", "PremiumB", false), ("PremiumSoftBrush", "PremiumSoftA", "PremiumSoftB", false),
    };
    private static bool _animating;
    private static long _animStart;
    private static double _animMs;

    public static bool IsLight { get; private set; }
    public static event Action? Changed;

    /// <summary>Applies the saved theme, accent and custom colours. Safe to call repeatedly.</summary>
    public static void ApplyFromSettings(bool animate = false)
    {
        var g = Svc.S.General;
        MigrateOldId(g);
        Apply(g.Theme, g.AccentOverride, g.CustomColors, animate && g.Animations);
    }

    private static void MigrateOldId(GeneralSettings g)
    {
        var old = Accents.FirstOrDefault(a => a.Id == g.Theme);
        if (old == null) return;
        g.Theme = "midnight";
        g.AccentOverride = old.Id == "blue" ? null : old.Accent;
    }

    /// <summary>Compatibility overload: an accent-only id from older builds.</summary>
    public static void Apply(string? id)
    {
        var g = Svc.S.General;
        var old = Accents.FirstOrDefault(a => a.Id == id);
        if (old != null) { g.AccentOverride = old.Id == "blue" ? null : old.Accent; ApplyFromSettings(true); return; }
        g.Theme = Palette(id).Id; ApplyFromSettings(true);
    }

    public static void Apply(string? themeId, string? accentOverride, IDictionary<string, string>? custom, bool animate)
    {
        try
        {
            var p = Palette(themeId);
            string C(string key, string def) => custom != null && custom.TryGetValue(key, out var v) && TryParse(v, out _) ? v : def;
            string accentHex = C("Accent", accentOverride is { Length: > 0 } ao && TryParse(ao, out _) ? ao : p.Accent);
            var accent = Parse(accentHex);
            var accent2 = Parse(C("Accent2", p.Accent2));
            var bg = Parse(C("Bg", p.Bg)); var side = Parse(C("Sidebar", p.Sidebar));
            var panel = Parse(C("Cards", p.Panel));
            // Panel2/Panel3 follow a custom card colour so nested surfaces stay in the same family.
            var panel2 = custom != null && custom.ContainsKey("Cards") ? Mix(panel, Parse(p.Text), p.Light ? 0.04 : 0.03) : Parse(p.Panel2);
            var panel3 = custom != null && custom.ContainsKey("Cards") ? Mix(panel, Parse(p.Text), p.Light ? 0.09 : 0.07) : Parse(p.Panel3);
            var border = Parse(C("Borders", p.Border));
            var borderStrong = custom != null && custom.ContainsKey("Borders") ? Mix(border, Parse(p.Text), p.Light ? 0.10 : 0.08) : Parse(p.BorderStrong);
            var text = Parse(C("Text", p.Text)); var muted = Parse(C("Muted", p.Muted));
            var dim = custom != null && custom.ContainsKey("Muted") ? Mix(muted, bg, 0.35) : Parse(p.Dim);
            IsLight = p.Light;

            var t = new Dictionary<string, Color>
            {
                ["BgBrush"] = bg, ["SidebarBrush"] = side, ["PanelBrush"] = panel, ["Panel2Brush"] = panel2, ["Panel3Brush"] = panel3,
                ["BorderBrush"] = border, ["BorderStrongBrush"] = borderStrong, ["TextBrush"] = text, ["MutedBrush"] = muted, ["DimBrush"] = dim,
                ["AccentBrush"] = accent,
                ["AccentHoverBrush"] = Shift(accent, p.Light ? -0.10 : 0.12),
                ["AccentPressedBrush"] = Shift(accent, p.Light ? -0.22 : -0.12),
                ["AccentSoftBrush"] = Color.FromArgb(p.Light ? (byte)0x26 : (byte)0x1A, accent.R, accent.G, accent.B),
                ["OnAccentBrush"] = Luma(accent) > 0.58 ? Color.FromRgb(0x06, 0x10, 0x1F) : Colors.White,
                ["NavActiveBrush"] = Mix(bg, accent, p.Light ? 0.14 : 0.10),
                ["NavHoverBrush"] = Mix(side, text, p.Light ? 0.04 : 0.035),
                ["TileBrush"] = Mix(panel, accent, p.Light ? 0.16 : 0.13),
                ["OptionActiveBrush"] = Mix(panel2, accent, p.Light ? 0.14 : 0.12),
                ["HoverLayerBrush"] = p.Light ? Color.FromArgb(0x12, 0, 0, 0) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF),
                ["ThumbBrush"] = p.Light ? Mix(muted, Colors.White, 0.35) : Color.FromRgb(0xB4, 0xB4, 0xBF),
                ["ScrollThumbBrush"] = Mix(panel3, text, p.Light ? 0.18 : 0.14),
                ["ScrollThumbHoverBrush"] = Mix(panel3, text, p.Light ? 0.32 : 0.28),
                ["DangerSoftBrush"] = p.Light ? Color.FromRgb(0xFD, 0xEC, 0xEC) : Color.FromRgb(0x1E, 0x12, 0x14),
                ["DangerSoftBorderBrush"] = p.Light ? Color.FromRgb(0xF2, 0xB8, 0xBB) : Color.FromRgb(0x4A, 0x22, 0x26),
                ["DangerTextBrush"] = p.Light ? Color.FromRgb(0xC2, 0x2D, 0x2D) : Color.FromRgb(0xFF, 0x8A, 0x8A),
                ["SuccessSoftBrush"] = p.Light ? Color.FromRgb(0xDD, 0xF5, 0xE4) : Color.FromRgb(0x16, 0x30, 0x1F),
                ["Accent2Brush"] = accent2,
                ["ShadowBrush"] = Color.FromArgb(p.Light ? (byte)0x40 : (byte)0x99, 0, 0, 0),
            };
            t["Accent"] = accent; t["Accent2"] = accent2;
            t["ContentA"] = bg; t["ContentB"] = Mix(bg, accent, p.Light ? 0.05 : 0.035);
            t["PremiumA"] = Shift(accent, -0.18); t["PremiumB"] = Shift(accent2, -0.18);
            t["PremiumSoftA"] = Mix(panel, accent, 0.18); t["PremiumSoftB"] = Mix(panel, accent2, 0.18);
            var res = Application.Current.Resources;
            StartTween(t, animate ? 300 : 0);

            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Error("Applying theme failed", ex); }
    }

    /// <summary>Sets a font family for the whole UI (empty = Segoe UI Variable).</summary>
    public static void ApplyFont(string? name)
    {
        try
        {
            var family = string.IsNullOrWhiteSpace(name) ? new FontFamily("Segoe UI Variable Text, Segoe UI") : new FontFamily(name + ", Segoe UI");
            Application.Current.Resources["UiFont"] = family;
        }
        catch (Exception ex) { Log.Warn("Applying font failed", ex); }
    }

    /// <summary>
    /// Moves every token colour from where it is to the new palette. Brushes are frozen once styles use them, so instead
    /// of animating one brush we publish a fresh interpolated brush every few frames; DynamicResource does the rest.
    /// </summary>
    private static void StartTween(Dictionary<string, Color> to, double ms)
    {
        _to = to;
        _from = _cur.Count == 0 ? new Dictionary<string, Color>(to) : new Dictionary<string, Color>(_cur);
        if (ms <= 0 || _cur.Count == 0) { Publish(1); return; }
        _animStart = Environment.TickCount64; _animMs = ms;
        if (!_animating) { _animating = true; System.Windows.Media.CompositionTarget.Rendering += OnRender; }
        Publish(0);
    }

    private static long _lastFrame;
    private static void OnRender(object? s, EventArgs e)
    {
        long now = Environment.TickCount64;
        if (now - _lastFrame < 30) return;   // ~30 steps a second is plenty for a colour fade and keeps the swap cheap
        _lastFrame = now;
        double p = Math.Clamp((now - _animStart) / _animMs, 0, 1);
        Publish(1 - Math.Pow(1 - p, 3));
        if (p >= 1) { _animating = false; System.Windows.Media.CompositionTarget.Rendering -= OnRender; }
    }

    private static void Publish(double t)
    {
        var res = Application.Current.Resources;
        var now = new Dictionary<string, Color>(_to.Count);
        foreach (var (key, target) in _to)
        {
            var from = _from.TryGetValue(key, out var f) ? f : target;
            now[key] = t >= 1 ? target : Lerp(from, target, t);
        }
        _cur = now;
        foreach (var key in BrushKeys)
        {
            var b = new SolidColorBrush(now[key]); b.Freeze();
            res[key] = b;
            if (key == "AccentBrush") res["AccentColor"] = now[key];
            else if (key == "AccentHoverBrush") res["AccentHoverColor"] = now[key];
            else if (key == "AccentPressedBrush") res["AccentPressedColor"] = now[key];
            else if (key == "BgBrush") res["BgColor"] = now[key];
            else if (key == "PanelBrush") res["PanelColor"] = now[key];
            else if (key == "TextBrush") res["TextColor"] = now[key];
            else if (key == "MutedBrush") res["MutedColor"] = now[key];
        }
        foreach (var (key, a, b2, vertical) in Gradients)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 1) };
            g.GradientStops.Add(new GradientStop(now[a], 0));
            g.GradientStops.Add(new GradientStop(now[b2], 1));
            g.Freeze();
            res[key] = g;
        }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public static bool TryParse(string? hex, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { c = (Color)ColorConverter.ConvertFromString(hex.StartsWith('#') ? hex : "#" + hex); return true; }
        catch { return false; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte M(byte x, byte y) => (byte)Math.Clamp(x + (y - x) * t, 0, 255);
        return Color.FromRgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    private static Color Shift(Color c, double amount)
    {
        byte F(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(F(c.R), F(c.G), F(c.B));
    }

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>WCAG contrast ratio between two colours, used by the Settings page to warn about unreadable combinations.</summary>
    public static double Contrast(Color a, Color b)
    {
        double L(Color c)
        {
            double F(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
        }
        double l1 = L(a), l2 = L(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>Current resolved colour of a token brush (after any running animation settles).</summary>
    public static Color Current(string key) => _cur.TryGetValue(key, out var c) ? c : Colors.Transparent;
}

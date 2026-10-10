using System.Windows;
using System.Windows.Media;

namespace Nighty.Services;

public sealed record ThemeChoice(string Id, string Name, string Accent);

/// <summary>UI presets: recolours the accent used for buttons, highlights and selections across the app.</summary>
public static class ThemeService
{
    // Every accent is dark enough for the white text drawn on accent buttons.
    public static readonly IReadOnlyList<ThemeChoice> Themes = new ThemeChoice[]
    {
        new("blue", "Nighty Blue", "#3B82F6"),
        new("sky", "Sky", "#4F7FE0"),
        new("violet", "Violet", "#7C5CFF"),
        new("crimson", "Crimson", "#E5484D"),
        new("gold", "Gold", "#B7791F"),
        new("emerald", "Emerald", "#15803D"),
        new("graphite", "Graphite", "#6B7280"),
    };

    public static ThemeChoice Find(string? id) => Themes.FirstOrDefault(t => t.Id == id) ?? Themes[0];

    public static void Apply(string? id)
    {
        try
        {
            var accent = (Color)ColorConverter.ConvertFromString(Find(id).Accent);
            Set("AccentColor", accent);
            Set("AccentBrush", accent);
            Set("AccentHoverBrush", Shift(accent, 0.12));
            Set("AccentPressedBrush", Shift(accent, -0.12));
            Set("AccentSoftBrush", Color.FromArgb(0x1A, accent.R, accent.G, accent.B));
        }
        catch (Exception ex) { Log.Error("Applying theme failed", ex); }
    }

    private static void Set(string key, Color c)
    {
        var res = Application.Current.Resources;
        if (key.EndsWith("Color")) { res[key] = c; return; }
        if (res[key] is SolidColorBrush b && !b.IsFrozen) b.Color = c;   // change in place so existing users of the brush update live
        else res[key] = new SolidColorBrush(c);
    }

    private static Color Shift(Color c, double amount)
    {
        byte F(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(F(c.R), F(c.G), F(c.B));
    }
}

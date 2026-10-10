using System.Windows;
using System.Windows.Media;

namespace Nighty.Services;

public sealed record ThemeChoice(string Id, string Name, string Accent);

/// <summary>UI presets: recolours the accent used for buttons, highlights and selections across the app.</summary>
public static class ThemeService
{
    // Bright accents: the text drawn on accent buttons is dark, like the rest of the Lyre-style look.
    public static readonly IReadOnlyList<ThemeChoice> Themes = new ThemeChoice[]
    {
        new("blue", "Blue", "#4F8EF7"),
        new("violet", "Violet", "#8B7CF6"),
        new("green", "Green", "#2FBF8A"),
        new("amber", "Amber", "#F5A524"),
        new("rose", "Rose", "#F0527A"),
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
            Set("TileBrush", Blend(accent, 0.12));
            Set("OptionActiveBrush", Blend(accent, 0.12));
            Set("NavActiveBrush", Blend(accent, 0.10));
        }
        catch (Exception ex) { Log.Error("Applying theme failed", ex); }
    }

    private static void Set(string key, Color c)
    {
        var res = Application.Current.Resources;
        if (key.EndsWith("Color")) res[key] = c;
        else res[key] = new SolidColorBrush(c);   // every user looks the brush up dynamically, so the whole UI follows
    }

    /// <summary>The accent mixed into near-black by <paramref name="amount"/>: the tinted navy of tiles and active items.</summary>
    private static Color Blend(Color accent, double amount)
    {
        byte M(byte a, byte b) => (byte)(b + (a - b) * amount);
        return Color.FromRgb(M(accent.R, 0x0A), M(accent.G, 0x0C), M(accent.B, 0x12));
    }

    private static Color Shift(Color c, double amount)
    {
        byte F(byte v) => (byte)Math.Clamp(amount >= 0 ? v + (255 - v) * amount : v * (1 + amount), 0, 255);
        return Color.FromRgb(F(c.R), F(c.G), F(c.B));
    }
}

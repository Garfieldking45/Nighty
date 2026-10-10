using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace Nighty.Services;

public sealed class FontEntry
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public bool IsUser { get; init; }
    public bool IsCollection => File.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);
    public FontFamily Family { get; init; } = new("Segoe UI");
    public int Styles { get; init; } = 1;
    public string Source => IsUser ? "Added to Nighty" : "Installed on Windows";
}

/// <summary>Fonts you can use in Roblox or in Nighty: everything installed on Windows, plus fonts you add (kept as a private copy).</summary>
public static class FontLibrary
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResource(string file);

    public static string Folder => Path.Combine(AppPaths.Root, "fonts");

    public static List<FontEntry> Load()
    {
        var list = new List<FontEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(Folder))
                foreach (var f in Directory.EnumerateFiles(Folder).Where(IsFontFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var e = FromFile(f, true);
                    if (e != null && seen.Add(e.Name)) list.Add(e);
                }
        }
        catch (Exception ex) { Log.Warn("Listing added fonts failed", ex); }

        foreach (var ff in System.Windows.Media.Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                if (!seen.Add(ff.Source)) continue;
                var faces = ff.GetTypefaces().ToList();
                var tf = faces.FirstOrDefault(t => t.Weight == System.Windows.FontWeights.Normal && t.Style == System.Windows.FontStyles.Normal) ?? faces.FirstOrDefault();
                if (tf == null || !tf.TryGetGlyphTypeface(out var gt) || gt.FontUri.Scheme != "file") continue;
                list.Add(new FontEntry { Name = ff.Source, File = gt.FontUri.LocalPath, Family = ff, Styles = faces.Count });
            }
            catch { /* a broken font should not break the list */ }
        }
        return list;
    }

    private static bool IsFontFile(string f) => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);

    private static FontEntry? FromFile(string file, bool user)
    {
        try
        {
            var gt = new GlyphTypeface(new Uri(file));
            string name = gt.Win32FamilyNames.TryGetValue(System.Globalization.CultureInfo.GetCultureInfo("en-us"), out var n) ? n : gt.Win32FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(file);
            var family = new FontFamily(new Uri(Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar), "./#" + name);
            return new FontEntry { Name = name, File = file, IsUser = user, Family = family };
        }
        catch { return null; }
    }

    /// <summary>Copies a font file into Nighty's library. Returns the entry or an error message.</summary>
    public static (FontEntry? Entry, string? Error) Add(string source)
    {
        try
        {
            if (!IsFontFile(source)) return (null, "Use a .ttf, .otf or .ttc font file.");
            Directory.CreateDirectory(Folder);
            var dest = Path.Combine(Folder, Path.GetFileName(source));
            if (File.Exists(dest)) return (null, "This font is already in Nighty's library.");
            File.Copy(source, dest);
            var e = FromFile(dest, true);
            if (e == null) { File.Delete(dest); return (null, "The font has no readable faces."); }
            return (e, null);
        }
        catch (Exception ex) { return (null, "Couldn't copy the font: " + ex.Message); }
    }

    public static string? Remove(FontEntry e)
    {
        if (!e.IsUser) return "Fonts that come with Windows can't be removed here.";
        try { File.Delete(e.File); return null; }
        catch (Exception ex) { return "The font is in use and couldn't be removed. Restart Nighty and try again. " + ex.Message; }
    }

    /// <summary>Installs a font for the current Windows account (no administrator needed) so other apps can use it.</summary>
    public static string InstallForUser(FontEntry e)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(e.File));
            if (!File.Exists(dest)) File.Copy(e.File, dest);
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
            string kind = e.File.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? " (OpenType)" : e.IsCollection ? " (collection)" : " (TrueType)";
            k.SetValue(e.Name + kind, dest);
            AddFontResource(dest);
            return "Installed for your Windows account. Some apps may need a restart to see it.";
        }
        catch (Exception ex) { return "Couldn't install the font: " + ex.Message; }
    }
}
